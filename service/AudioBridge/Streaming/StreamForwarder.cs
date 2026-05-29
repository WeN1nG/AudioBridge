using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AudioBridge.DeviceMonitor;
using AudioBridge.Utils;

namespace AudioBridge.Streaming;

public class StreamForwarder : IDisposable
{
    private readonly AdbWrapper _adb;
    private readonly string _remotePath;
    private readonly int _port;
    private string? _serial;
    private Process? _adbProcess;
    private TcpClient? _tcpClient;
    private Stream? _dataStream;
    private Task? _stderrReaderTask;
    private volatile bool _isRunning;

    // Producer-consumer queue decouples capture thread from TCP write thread
    private BlockingCollection<AudioPacket>? _writeQueue;
    private CancellationTokenSource? _writeCts;
    private Task? _writeTask;

    // Signalled when audio_player reports it's listening on TCP port
    private readonly ManualResetEvent _playerReady = new(false);

    private const int MaxQueueDepth = 500;
    private long _totalPacketsProduced;
    private long _totalPacketsConsumed;
    private long _totalBytesWritten;

    private static readonly TimeSpan LatencyWarningThreshold = TimeSpan.FromMilliseconds(500);
    private long _latencyLogCounter;

    public bool IsConnected => _isRunning && _tcpClient?.Connected == true;

    /// <summary>Fired when the TCP write loop detects a connection failure.</summary>
    public event EventHandler? ConnectionLost;

    public StreamForwarder(AdbWrapper adb, string remotePath, int port = 27777)
    {
        Logger.Debug("[Function] void StreamForwarder::ctor(AdbWrapper, string, int) start");
        _adb = adb;
        _remotePath = remotePath;
        _port = port;
        Logger.Debug("[Function] void StreamForwarder::ctor(AdbWrapper, string, int) end");
    }

    public bool Start(string serial, string localPlayerPath, int sampleRate = 48000, int channels = 2, int bitsPerSample = 32, int bufferMs = 50)
    {
        Logger.Debug($"[Function] bool StreamForwarder::Start(string, string) start, serial={serial}");
        _serial = serial;

        if (!File.Exists(localPlayerPath))
        {
            Logger.Error($"音频播放器文件不存在: {localPlayerPath}");
            return false;
        }

        // Step 1: Set up ADB forward (tcp:port -> device tcp:port)
        Logger.Info("正在设置 ADB 端口转发...");
        if (!_adb.SetupForward(serial, _port))
        {
            Logger.Error("ADB 端口转发设置失败");
            return false;
        }

        // Step 2: Push audio_player to device
        Logger.Info("正在推送音频播放器到设备...");
        if (!_adb.PushFile(localPlayerPath, _remotePath, serial))
        {
            Logger.Error("推送 audio_player 到设备失败");
            return false;
        }

        _adb.SetExecutable(_remotePath, serial);

        // Step 3: Start audio_player (TCP loopback mode: port > 0)
        Logger.Info($"正在启动音频播放器进程 (port={_port})...");
        _adbProcess = _adb.StartAudioPlayer(serial, _remotePath, _port, sampleRate, channels, bitsPerSample, bufferMs);
        _adbProcess.EnableRaisingEvents = true;
        _adbProcess.Exited += OnAudioPlayerExited;

        // Read stderr asynchronously for audio_player log messages
        _stderrReaderTask = Task.Factory.StartNew(() =>
        {
            try
            {
                string? line;
                while ((line = _adbProcess.StandardError.ReadLine()) != null)
                {
                    Logger.Info($"[audio_player] {line}");
                    // Detect when audio_player has started TCP listening
                    if (line.Contains("Waiting for TCP connection on"))
                    {
                        _playerReady.Set();
                    }
                }
                Logger.Warn("[audio_player] stderr stream ended (process may have exited)");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[audio_player] stderr reader error: {ex.Message}");
            }
        }, TaskCreationOptions.LongRunning);

        // Step 4: Wait for audio_player to report it's listening, then connect
        Logger.Info("正在等待音频播放器就绪...");
        bool ready = _playerReady.WaitOne(TimeSpan.FromSeconds(15));
        if (!ready)
        {
            Logger.Error("音频播放器未在15秒内就绪（可能进程崩溃或设备异常）");
            Cleanup();
            return false;
        }

        Logger.Info("正在连接音频转发通道...");
        _tcpClient = new TcpClient();
        try
        {
            _tcpClient.Connect("127.0.0.1", _port);
        }
        catch (Exception ex)
        {
            Logger.Error($"连接 ADB 转发端口失败: {ex.Message}");
            Cleanup();
            return false;
        }

        _dataStream = _tcpClient.GetStream();
        Logger.Info("音频转发通道已建立");

        // Step 5: Start dedicated write thread
        _writeQueue = new BlockingCollection<AudioPacket>();
        _writeCts = new CancellationTokenSource();
        _writeTask = Task.Factory.StartNew(() => WriteLoop(_writeCts.Token),
            _writeCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        _isRunning = true;
        Logger.Debug("[Function] bool StreamForwarder::Start(string, string) end => true");
        return true;
    }

    public void Stop()
    {
        Logger.Debug("[Function] void StreamForwarder::Stop() start");
        _isRunning = false;

        // Signal write queue to stop accepting new data
        try { _writeQueue?.CompleteAdding(); } catch { }

        // Wait for write loop to drain remaining data
        try { _writeTask?.Wait(3000); } catch { }

        Cleanup();

        Logger.Info("音频转发已停止");
        Logger.Debug("[Function] void StreamForwarder::Stop() end");
    }

    private void Cleanup()
    {
        // Close data stream and TcpClient
        try { _dataStream?.Close(); } catch { }
        _dataStream = null;
        try { _tcpClient?.Close(); } catch { }
        _tcpClient = null;

        // Wait for stderr reader
        try { _stderrReaderTask?.Wait(2000); } catch { }

        // Kill audio_player process
        if (_adbProcess != null)
        {
            _adb.KillAudioPlayer(_adbProcess);
            _adbProcess = null;
        }

        // Remove ADB forward
        if (_serial != null)
        {
            _adb.RemoveForward(_serial, _port);
        }

        _writeCts?.Cancel();
        _writeCts?.Dispose();
        _writeCts = null;
        _writeQueue?.Dispose();
        _writeQueue = null;

        _playerReady.Reset();
    }

    private void OnAudioPlayerExited(object? sender, EventArgs e)
    {
        int exitCode = -1;
        try { if (_adbProcess?.HasExited == true) exitCode = _adbProcess.ExitCode; } catch { }
        Logger.Warn($"[audio_player] 进程已退出 (exit code: {exitCode})");
    }

    /// <summary>Producer: called from UDP receive thread, never blocks.</summary>
    public void SendAudioData(byte[] pcmData)
    {
        if (!_isRunning || _writeQueue == null || _writeQueue.IsAddingCompleted)
            return;

        if (_tcpClient == null || !_tcpClient.Connected)
            return;

        if (_writeQueue.Count > MaxQueueDepth)
        {
            if (Interlocked.Increment(ref _totalPacketsProduced) % 100 == 0)
                Logger.Warn($"音频数据队列已满({_writeQueue.Count}/{MaxQueueDepth})，丢弃数据");
            return;
        }

        try
        {
            _writeQueue.Add(new AudioPacket(pcmData));
            long produced = Interlocked.Increment(ref _totalPacketsProduced);
            if (produced == 1 || produced % 500 == 0)
            {
                Logger.Debug($"[Forward] Produce: 包#{produced} size={pcmData.Length}B queue_depth={_writeQueue.Count}");
            }
        }
        catch (InvalidOperationException) { }
        catch (Exception ex)
        {
            Logger.Error($"音频数据入队失败: {ex.Message}");
        }
    }

    /// <summary>Consumer: runs on dedicated thread, writes to TcpClient stream.</summary>
    private void WriteLoop(CancellationToken token)
    {
        Logger.Debug("[Function] void StreamForwarder::WriteLoop() start");

        try
        {
            foreach (var packet in _writeQueue!.GetConsumingEnumerable(token))
            {
                if (_dataStream == null) continue;
                if (_tcpClient == null || !_tcpClient.Connected) break;

                // Track end-to-end latency periodically
                var latency = DateTime.UtcNow - packet.Timestamp;
                if (latency > LatencyWarningThreshold)
                {
                    long lc = Interlocked.Increment(ref _latencyLogCounter);
                    if (lc <= 3 || lc % 100 == 0)
                        Logger.Warn($"端到端延迟较高: {latency.TotalMilliseconds:F0}ms");
                }

                try
                {
                    _dataStream.Write(packet.Data, 0, packet.Data.Length);
                    long consumed = Interlocked.Increment(ref _totalPacketsConsumed);
                    long totalBytes = Interlocked.Add(ref _totalBytesWritten, packet.Data.Length);
                    if (consumed == 1 || consumed % 500 == 0)
                    {
                        Logger.Debug($"[Forward] Consume: 包#{consumed} written={packet.Data.Length}B total={totalBytes}B");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"音频转发写入失败: {ex.Message}");
                    ConnectionLost?.Invoke(this, EventArgs.Empty);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }

        Logger.Debug("[Function] void StreamForwarder::WriteLoop() end");
    }

    public void Dispose()
    {
        Stop();
    }
}
