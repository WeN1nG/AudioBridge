using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AudioBridge.Utils;

// This project is Windows-only by design (WASAPI, ADB on Windows)
#pragma warning disable CA1416

namespace AudioBridge.AudioCapture;

public class AudioCaptureManager : IDisposable
{
    // WASAPI COM objects
    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;

    // Audio format info from WASAPI mix format
    private int _wasapiSampleRate;
    private int _wasapiChannels;
    private int _wasapiBitsPerSample;
    private int _wasapiFrameSize; // nBlockAlign from WAVEFORMATEX
    private bool _wasapiIsFloat;

    // Target format from config
    private int _targetSampleRate = 48000;
    private int _targetBitsPerSample = 32;
    private int _targetChannels = 2;

    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private long _totalPacketsCaptured;
    private long _totalBytesCaptured;
    private System.Timers.Timer? _statsTimer;

    // Device change notification
    private WasapiNotificationClient? _notificationClient;
    private volatile bool _deviceChanged;

    public bool IsRunning { get; private set; }
    public int WasapiSampleRate => _wasapiSampleRate;
    public int WasapiChannels => _wasapiChannels;
    public int WasapiBitsPerSample => _wasapiBitsPerSample;
    public bool WasapiIsFloat => _wasapiIsFloat;

    public event EventHandler<byte[]>? AudioDataReceived;
    /// <summary>Fired when the default audio device changes (HDMI/Bluetooth plug/unplug).</summary>
    public event EventHandler? DefaultDeviceChanged;

    public void SetTargetFormat(int sampleRate, int bitsPerSample, int channels)
    {
        _targetSampleRate = sampleRate;
        _targetBitsPerSample = bitsPerSample;
        _targetChannels = channels;
    }

    public void Start()
    {
        Logger.Debug("[Function] void AudioCaptureManager::Start() start");

        if (IsRunning)
        {
            Logger.Warn("音频捕获已在运行中");
            return;
        }

        try
        {
            InitializeWasapi();
        }
        catch (Exception ex)
        {
            Logger.Error($"WASAPI 初始化失败: {ex.Message}");
            Logger.Warn("请确保系统有一个活动的音频输出设备");
            CleanupCom();
            Logger.Debug("[Function] void AudioCaptureManager::Start() end (failed)");
            return;
        }

        _cts = new CancellationTokenSource();
        _totalPacketsCaptured = 0;
        _totalBytesCaptured = 0;

        // Periodic stats timer
        _statsTimer = new System.Timers.Timer(5000);
        _statsTimer.Elapsed += (_, _) =>
        {
            long count = Interlocked.Exchange(ref _totalPacketsCaptured, 0);
            long bytes = Interlocked.Exchange(ref _totalBytesCaptured, 0);
            if (count > 0)
                Logger.Info($"音频捕获状态: 过去5秒 {count} 个buffer, {bytes / 1024} KB");
            else
                Logger.Warn("音频捕获状态: 过去5秒未捕获到数据");
        };
        _statsTimer.Start();

        // Start capture loop on dedicated thread
        _captureTask = Task.Factory.StartNew(() => CaptureLoop(_cts.Token),
            _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        IsRunning = true;
        Logger.Info("WASAPI 音频捕获已启动");
        Logger.Debug("[Function] void AudioCaptureManager::Start() end");
    }

    public void Stop()
    {
        Logger.Debug("[Function] void AudioCaptureManager::Stop() start");

        _cts?.Cancel();
        _statsTimer?.Stop();
        _statsTimer?.Dispose();
        _statsTimer = null;

        try { _captureTask?.Wait(2000); } catch { }

        StopWasapi();
        CleanupCom();
        IsRunning = false;

        Logger.Info("音频捕获已停止");
        Logger.Debug("[Function] void AudioCaptureManager::Stop() end");
    }

    // ---- WASAPI Initialization ----

    private void InitializeWasapi()
    {
        _enumerator = new MMDeviceEnumerator() as IMMDeviceEnumerator;
        if (_enumerator == null)
            throw new InvalidOperationException("无法创建 MMDeviceEnumerator COM 对象");

        // Register for default device change notifications
        _notificationClient = new WasapiNotificationClient();
        _notificationClient.DefaultDeviceChanged += () => _deviceChanged = true;
        int notifyHr = _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
        if (notifyHr != 0)
            Logger.Warn($"注册音频设备变更通知失败: HRESULT=0x{notifyHr:X8}，默认设备切换时将不会自动恢复");

        // Get default audio render (output) device
        int hr = _enumerator.GetDefaultAudioEndpoint((int)EDataFlow.eRender, (int)ERole.eConsole, out _device);
        if (hr != 0)
            throw new InvalidOperationException($"获取默认音频设备失败: HRESULT=0x{hr:X8}");

        // Activate IAudioClient
        var guidAudioClient = typeof(IAudioClient).GUID;
        object? audioClientObj = null;
        hr = _device.Activate(ref guidAudioClient, /*CLSCTX_ALL*/ 23, IntPtr.Zero, out audioClientObj);
        if (hr != 0 || audioClientObj == null)
            throw new InvalidOperationException($"IAudioClient 激活失败: HRESULT=0x{hr:X8}");
        _audioClient = (IAudioClient)audioClientObj;

        // Get mix format from the audio engine
        hr = _audioClient.GetMixFormat(out IntPtr formatPtr);
        if (hr != 0)
            throw new InvalidOperationException($"GetMixFormat 失败: HRESULT=0x{hr:X8}");

        // Parse format info from native memory (manual reads, avoid struct layout issues)
        short formatTag = Marshal.ReadInt16(formatPtr);
        _wasapiChannels = Marshal.ReadInt16(formatPtr + 2);
        _wasapiSampleRate = Marshal.ReadInt32(formatPtr + 4);
        _wasapiFrameSize = Marshal.ReadInt16(formatPtr + 12);
        short bitsPerSample = Marshal.ReadInt16(formatPtr + 14);
        short cbSize = Marshal.ReadInt16(formatPtr + 16);

        if ((ushort)formatTag == AudioClientConst.WAVE_FORMAT_EXTENSIBLE)
        {
            // WAVEFORMATEXTENSIBLE: read SubFormat GUID at offset 24
            short validBitsPerSample = Marshal.ReadInt16(formatPtr + 18);
            int g1 = Marshal.ReadInt32(formatPtr + 24);
            short g2 = Marshal.ReadInt16(formatPtr + 28);
            short g3 = Marshal.ReadInt16(formatPtr + 30);
            byte[] g4 = new byte[8];
            Marshal.Copy(formatPtr + 32, g4, 0, 8);
            Guid subFormat = new Guid(g1, g2, g3, g4);

            _wasapiIsFloat = subFormat == AudioClientConst.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
            _wasapiBitsPerSample = validBitsPerSample > 0 ? validBitsPerSample : bitsPerSample;
        }
        else
        {
            _wasapiIsFloat = (ushort)formatTag == AudioClientConst.WAVE_FORMAT_IEEE_FLOAT;
            _wasapiBitsPerSample = bitsPerSample;
        }

        Logger.Info($"WASAPI 混音器格式: {_wasapiSampleRate}Hz {_wasapiChannels}ch " +
                     $"{(_wasapiIsFloat ? "float" : _wasapiBitsPerSample + "bit int")} " +
                     $"frame_size={_wasapiFrameSize}B format_tag=0x{formatTag:X4}");

        // Initialize loopback capture — use the COMPLETE format pointer from GetMixFormat
        // (WAVEFORMATEXTENSIBLE is 40 bytes, not just the 18-byte WAVEFORMATEX header.
        //  Truncating it would lose the SubFormat GUID and cause Initialize to fail.)
        hr = _audioClient.Initialize(
            /*shareMode*/ 0, // AUDCLNT_SHAREMODE_SHARED
            /*streamFlags*/ AudioClientConst.AUDCLNT_STREAMFLAGS_LOOPBACK,
            /*hnsBufferDuration*/ 0, // default
            /*hnsPeriodicity*/ 0, // default
            formatPtr,
            IntPtr.Zero);
        Marshal.FreeCoTaskMem(formatPtr); // free AFTER Initialize

        if (hr != 0)
            throw new InvalidOperationException($"IAudioClient.Initialize(LOOPBACK) 失败: HRESULT=0x{hr:X8}");

        hr = _audioClient.GetBufferSize(out uint bufferFrames);
        if (hr != 0)
            throw new InvalidOperationException($"GetBufferSize 失败: HRESULT=0x{hr:X8}");
        Logger.Info($"WASAPI 缓冲区: {bufferFrames} frames ({bufferFrames * 1000 / (uint)_wasapiSampleRate}ms)");

        // Get IAudioCaptureClient
        Guid iidCapture = AudioClientConst.IID_IAudioCaptureClient;
        hr = _audioClient.GetService(ref iidCapture, out IntPtr capturePtr);
        if (hr != 0 || capturePtr == IntPtr.Zero)
            throw new InvalidOperationException($"获取 IAudioCaptureClient 失败: HRESULT=0x{hr:X8}");
        _captureClient = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capturePtr);
        Marshal.Release(capturePtr); // GetObjectForIUnknown adds a ref
    }

    // ---- Capture Loop ----

    private void CaptureLoop(CancellationToken token)
    {
        Logger.Debug("[Function] void AudioCaptureManager::CaptureLoop() start");

        // Start the audio client — this makes data available for capture
        int hr = _audioClient!.Start();
        if (hr != 0)
        {
            Logger.Error($"IAudioClient.Start 失败: HRESULT=0x{hr:X8}");
            return;
        }

        Logger.Info("WASAPI 捕获循环已开始");

        while (!token.IsCancellationRequested)
        {
            try
            {
                // Check if default device changed (e.g. HDMI/Bluetooth plug/unplug)
                if (_deviceChanged)
                {
                    _deviceChanged = false;
                    Logger.Warn("默认音频设备已更改，正在重新启动WASAPI捕获...");
                    // Fire event so Program.cs can orchestrate full restart
                    DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
                    break;
                }

                // Poll for available packets
                hr = _captureClient!.GetNextPacketSize(out uint packetSize);
                if (hr != 0) break;

                while (packetSize > 0)
                {
                    hr = _captureClient.GetBuffer(out IntPtr data, out uint frames,
                        out uint flags, out ulong _, out ulong _);
                    if (hr != 0) break;

                    if (frames > 0 && (flags & AudioClientConst.AUDCLNT_BUFFERFLAGS_SILENT) == 0)
                    {
                        // Use _wasapiFrameSize (nBlockAlign) for correct bytes per frame
                        int byteCount = (int)frames * _wasapiFrameSize;
                        byte[] rawData = new byte[byteCount];
                        Marshal.Copy(data, rawData, 0, byteCount);

                        // Convert format if needed
                        byte[] outputData = ConvertFormat(rawData);

                        Interlocked.Increment(ref _totalPacketsCaptured);
                        Interlocked.Add(ref _totalBytesCaptured, outputData.Length);

                        AudioDataReceived?.Invoke(this, outputData);
                    }

                    _captureClient.ReleaseBuffer(frames);
                    hr = _captureClient.GetNextPacketSize(out packetSize);
                    if (hr != 0) break;
                }

                // Short sleep to avoid busy-wait when no data
                // (Do NOT sleep when data is flowing — inner while loop handles back-to-back packets)
                if (packetSize == 0)
                    Thread.Sleep(5);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Error($"WASAPI 捕获异常: {ex.Message}");
                break;
            }
        }

        // Stop the audio client
        try { _audioClient?.Stop(); } catch { }

        Logger.Debug("[Function] void AudioCaptureManager::CaptureLoop() end");
    }

    // ---- Format Conversion ----

    private byte[] ConvertFormat(byte[] rawData)
    {
        if (rawData.Length == 0) return rawData;

        bool needSRC = _wasapiSampleRate != _targetSampleRate;

        // Passthrough if formats match exactly
        if (!_wasapiIsFloat && !needSRC &&
            _wasapiBitsPerSample == _targetBitsPerSample &&
            _wasapiChannels == _targetChannels)
        {
            return rawData;
        }

        // float → int conversion (most common case), with optional SRC
        if (_wasapiIsFloat && _targetBitsPerSample is 16 or 32)
        {
            if (needSRC)
            {
                Logger.Info($"采样率转换: {_wasapiSampleRate}Hz → {_targetSampleRate}Hz");
                return SampleConverter.ConvertFloatToIntWithSRC(
                    rawData, _wasapiSampleRate, _targetSampleRate,
                    _wasapiChannels, _targetBitsPerSample);
            }
            return SampleConverter.ConvertFloatToInt(rawData, _targetBitsPerSample);
        }

        // Integer format conversion (e.g. 24-bit → 32-bit, 16-bit → 32-bit, etc.)
        if (!_wasapiIsFloat && _wasapiBitsPerSample != _targetBitsPerSample)
        {
            Logger.Info($"位深转换: {_wasapiBitsPerSample}bit → {_targetBitsPerSample}bit");
            return SampleConverter.ConvertIntToInt(rawData, _wasapiBitsPerSample, _targetBitsPerSample);
        }

        // Sample rate or channel mismatch on non-float input (rare)
        if (needSRC || _wasapiChannels != _targetChannels)
        {
            Logger.Warn($"采样率/声道不匹配 ({_wasapiSampleRate}/{_wasapiChannels} → {_targetSampleRate}/{_targetChannels})，当前仅支持float输入的重采样");
            return rawData;
        }

        return rawData;
    }

    // ---- Cleanup ----

    private void StopWasapi()
    {
        try
        {
            // IAudioClient.Stop() to halt the capture stream
            if (_audioClient != null)
            {
                Marshal.ThrowExceptionForHR(_audioClient.Stop());
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"停止 WASAPI 客户端时发生轻微异常: {ex.Message}");
        }
    }

    private void CleanupCom()
    {
        // Unregister device notification callback
        if (_notificationClient != null && _enumerator != null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_notificationClient); } catch { }
            _notificationClient = null;
        }

        if (_captureClient != null)
        {
            Marshal.ReleaseComObject(_captureClient);
            _captureClient = null;
        }

        if (_audioClient != null)
        {
            Marshal.ReleaseComObject(_audioClient);
            _audioClient = null;
        }

        if (_device != null)
        {
            Marshal.ReleaseComObject(_device);
            _device = null;
        }

        if (_enumerator != null)
        {
            Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

/// <summary>
/// COM-callable wrapper for IMMNotificationClient device-change callbacks.
/// Fires managed events when WASAPI detects default audio device changes.
/// </summary>
internal class WasapiNotificationClient : IMMNotificationClient
{
    public event Action? DefaultDeviceChanged;
    public event Action<string>? DeviceAdded;
    public event Action<string>? DeviceRemoved;

    public void OnDeviceStateChanged(string deviceId, int newState) { }
    public void OnDeviceAdded(string deviceId) => DeviceAdded?.Invoke(deviceId);
    public void OnDeviceRemoved(string deviceId) => DeviceRemoved?.Invoke(deviceId);
    public void OnDefaultDeviceChanged(int dataFlow, int role, string defaultDeviceId)
    {
        // Only react to render (playback) device changes on the console role
        if (dataFlow == (int)EDataFlow.eRender && role == (int)ERole.eConsole)
            DefaultDeviceChanged?.Invoke();
    }
    public void OnPropertyValueChanged(string deviceId, int key) { }
}
