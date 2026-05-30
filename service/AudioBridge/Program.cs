using System;
using System.IO;
using System.Threading;
using AudioBridge.DeviceMonitor;
using AudioBridge.AudioCapture;
using AudioBridge.Streaming;
using AudioBridge.Utils;

namespace AudioBridge;

class Program
{
    private const int FREQUENCY = 1;

    private static Config? _config;
    private static DeviceWatcher? _deviceWatcher;
    private static AudioCaptureManager? _audioCapture;
    private static StreamForwarder? _streamForwarder;
    private static AdbWrapper? _adb;
    private static string? _activeSerial;

    static void Main(string[] args)
    {
        Logger.Debug("[Function] void Program::Main(string[]) start");

        Console.WriteLine("========================================");
        Console.WriteLine("  AudioBridge - 跨平台音频桥接服务 v1.0");
        Console.WriteLine("  将电脑音频转发到 Android 设备扬声器");
        Console.WriteLine("========================================");
        Console.WriteLine();

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string configPath = Path.Combine(baseDir, "requirement", "config.json");

        _config = Config.Load(configPath);
        Logger.Level = ParseLogLevel(_config.Logging.Level);
        SampleConverter.SetPreAttenuationDb(_config.Audio.PreAttenuationDb);

        Logger.Info("AudioBridge 服务启动中...");

        string? adbPath = FindAdb(_config.Adb.AdbPath);
        if (string.IsNullOrEmpty(adbPath))
        {
            Logger.Error("未找到 adb.exe，请安装 ADB 工具或配置 adbPath");
            Logger.Info("下载地址: https://developer.android.com/studio/releases/platform-tools");
            WaitAndExit();
            return;
        }
        Logger.Info($"ADB 路径: {adbPath}");

        _adb = new AdbWrapper(adbPath);

        if (!_adb.IsAvailable())
        {
            Logger.Error("ADB 不可用，请检查 adb.exe 是否正确安装");
            WaitAndExit();
            return;
        }

        _deviceWatcher = new DeviceWatcher(_adb);
        _deviceWatcher.DeviceConnected += OnDeviceConnected;
        _deviceWatcher.DeviceDisconnected += OnDeviceDisconnected;
        _deviceWatcher.ErrorOccurred += (_, msg) => Logger.Warn(msg);

        _audioCapture = new AudioCaptureManager();
        _audioCapture.AudioDataReceived += OnAudioDataReceived;
        _audioCapture.DefaultDeviceChanged += OnDefaultDeviceChanged;

        Console.CancelKeyPress += OnExit;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();

        _deviceWatcher.Start();

        Logger.Info("AudioBridge 服务已就绪，等待设备连接...");
        Logger.Info("请通过 USB 连接 Android 手机并开启 USB 调试");

        Logger.Debug("[Function] void Program::Main(string[]) normal debug informations: monitoring for devices");

        var waitEvent = new ManualResetEvent(false);
        waitEvent.WaitOne();

        Logger.Debug("[Function] void Program::Main(string[]) end");
    }

    private static void OnDeviceConnected(object? sender, DeviceInfo device)
    {
        Logger.Debug($"[Function] void Program::OnDeviceConnected(...) start, serial={device.Serial}");

        _activeSerial = device.Serial;

        // Start forwarder FIRST so we don't drop UDP packets during setup
        string playerLocal = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "requirement", "audio_player");
        if (!File.Exists(playerLocal))
        {
            playerLocal = Path.Combine(
                Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory)?.Parent?.Parent?.Parent?.FullName ?? ".",
                "audio_player", "audio_player");
        }

        _streamForwarder = new StreamForwarder(_adb!, "/data/local/tmp/audio_player", 27777);
        _streamForwarder.ConnectionLost += OnForwarderConnectionLost;
        if (!_streamForwarder.Start(device.Serial, playerLocal,
                _config!.Audio.TargetSampleRate,
                _config.Audio.TargetChannels,
                _config.Audio.TargetBitsPerSample,
                _config.Audio.BufferSizeMs))
        {
            Logger.Error("音频转发启动失败");
            Logger.Debug("[Function] void Program::OnDeviceConnected(...) end (failed)");
            return;
        }

        // Configure WASAPI capture format to match audio_player expectations
        _audioCapture?.SetTargetFormat(
            _config!.Audio.TargetSampleRate,
            _config.Audio.TargetBitsPerSample,
            _config.Audio.TargetChannels);

        // Start audio capture AFTER forwarder is ready
        _audioCapture?.Start();

        Logger.Info($"设备 {device.Serial} 已就绪，开始转发音频");

        Logger.Debug("[Function] void Program::OnDeviceConnected(...) end");
    }

    private static void OnDeviceDisconnected(object? sender, DeviceInfo device)
    {
        Logger.Debug($"[Function] void Program::OnDeviceDisconnected(...) start, serial={device.Serial}");

        Logger.Info($"设备 {device.Serial} 已断开");

        _streamForwarder?.Stop();
        _streamForwarder?.Dispose();
        _streamForwarder = null;

        _audioCapture?.Stop();

        _activeSerial = null;

        Logger.Debug("[Function] void Program::OnDeviceDisconnected(...) end");
    }

    private static long _totalAudioDataCalls;
    private static void OnAudioDataReceived(object? sender, byte[] pcmData)
    {
        long call = Interlocked.Increment(ref _totalAudioDataCalls);
        if (call == 1 || call % 500 == 0)
        {
            Logger.Debug($"[Bridge] OnAudioDataReceived #{call}: pcmData size={pcmData.Length}B");
        }
        _streamForwarder?.SendAudioData(pcmData);
    }

    private static void OnForwarderConnectionLost(object? sender, EventArgs e)
    {
        Logger.Warn("音频转发连接丢失，正在触发设备断开以触发自动重连...");
        var serial = _activeSerial;
        if (serial != null)
        {
            OnDeviceDisconnected(sender, new DeviceInfo { Serial = serial });
            // Remove from watcher's known devices so it re-detects on next poll
            _deviceWatcher?.ForgetDevice(serial);
        }
    }

    private static void OnDefaultDeviceChanged(object? sender, EventArgs e)
    {
        // Defer to ThreadPool to avoid potential deadlock from WASAPI callback thread
        Task.Run(() =>
        {
            Logger.Warn("默认音频设备已更改，重新启动WASAPI捕获...");
            _audioCapture?.Stop();
            _audioCapture?.Start();
            Logger.Info("WASAPI捕获已在新设备上恢复");
        });
    }

    private static void OnExit(object? sender, ConsoleCancelEventArgs e)
    {
        Logger.Debug("[Function] void Program::OnExit(...) start");
        e.Cancel = true;
        Cleanup();
        Logger.Debug("[Function] void Program::OnExit(...) end");
    }

    private static void Cleanup()
    {
        Logger.Info("正在清理...");

        _streamForwarder?.Stop();
        _streamForwarder?.Dispose();

        _audioCapture?.Stop();
        _audioCapture?.Dispose();

        _deviceWatcher?.Stop();
        _deviceWatcher?.Dispose();

        Logger.Info("AudioBridge 服务已退出");
        Console.WriteLine($"Time: {DateTimeOffset.UtcNow.ToUnixTimeSeconds()} Frequency: {FREQUENCY}");

        Environment.Exit(0);
    }

    private static string? FindAdb(string configuredPath)
    {
        if (!string.IsNullOrEmpty(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        // Check alongside the exe first (for bundled deployment)
        string? localAdb = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "requirement", "adb.exe");
        if (File.Exists(localAdb))
            return localAdb;

        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "where",
                Arguments = "adb.exe",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc != null)
            {
                string? path = proc.StandardOutput.ReadLine();
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return path;
            }
        }
        catch { }

        string[] commonPaths = {
            @"F:\ADB\platform-tools\adb.exe",
            @"C:\ADB\adb.exe",
            @"C:\Android\platform-tools\adb.exe",
            @"C:\Program Files\Android\platform-tools\adb.exe"
        };

        foreach (var p in commonPaths)
        {
            if (File.Exists(p)) return p;
        }

        return null;
    }

    private static Logger.LogLevel ParseLogLevel(string level)
    {
        return level.ToLower() switch
        {
            "debug" => Logger.LogLevel.Debug,
            "info"  => Logger.LogLevel.Info,
            "warn"  => Logger.LogLevel.Warn,
            "error" => Logger.LogLevel.Error,
            _       => Logger.LogLevel.Info
        };
    }

    private static void WaitAndExit()
    {
        Logger.Info("按任意键退出...");
        try { Console.ReadKey(); } catch { }
        Console.WriteLine($"Time: {DateTimeOffset.UtcNow.ToUnixTimeSeconds()} Frequency: {FREQUENCY}");
        Environment.Exit(1);
    }
}
