using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using AudioBridge.Utils;

namespace AudioBridge.DeviceMonitor;

public class DeviceWatcher : IDisposable
{
    private readonly AdbWrapper _adb;
    private System.Timers.Timer? _timer;
    private readonly Dictionary<string, DeviceInfo> _knownDevices = new();
    private string? _activeSerial;
    private bool _isPolling;

    public event EventHandler<DeviceInfo>? DeviceConnected;
    public event EventHandler<DeviceInfo>? DeviceDisconnected;
    public event EventHandler<string>? ErrorOccurred;

    public string? ActiveSerial => _activeSerial;

    public DeviceWatcher(AdbWrapper adb)
    {
        Logger.Debug("[Function] void DeviceWatcher::ctor(AdbWrapper) start");
        _adb = adb;
        Logger.Debug("[Function] void DeviceWatcher::ctor(AdbWrapper) end");
    }

    public void Start()
    {
        Logger.Debug("[Function] void DeviceWatcher::Start() start");
        _timer = new System.Timers.Timer(1000);
        _timer.Elapsed += OnPoll;
        _timer.AutoReset = true;
        _timer.Start();
        Logger.Info("设备监控已启动 (每1秒轮询)");
        Logger.Debug("[Function] void DeviceWatcher::Start() end");
    }

    public void Stop()
    {
        Logger.Debug("[Function] void DeviceWatcher::Stop() start");
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
        Logger.Debug("[Function] void DeviceWatcher::Stop() end");
    }

    /// <summary>Remove a device from the known devices map so it gets re-detected on next poll.</summary>
    public void ForgetDevice(string serial)
    {
        Logger.Debug($"[Function] void DeviceWatcher::ForgetDevice(string) start, serial={serial}");
        _knownDevices.Remove(serial);
        if (_activeSerial == serial)
            _activeSerial = null;
        Logger.Debug("[Function] void DeviceWatcher::ForgetDevice(string) end");
    }

    private void OnPoll(object? sender, ElapsedEventArgs e)
    {
        if (_isPolling) return;
        _isPolling = true;

        try
        {
            var current = _adb.GetDevices();
            var currentSerials = current.Select(d => d.Serial).ToHashSet();

            // Detect disconnections
            foreach (var kvp in _knownDevices.ToList())
            {
                if (!currentSerials.Contains(kvp.Key))
                {
                    Logger.Info($"设备已断开: {kvp.Key}");
                    kvp.Value.State = DeviceState.Disconnected;
                    DeviceDisconnected?.Invoke(this, kvp.Value);
                    _knownDevices.Remove(kvp.Key);

                    if (_activeSerial == kvp.Key)
                        _activeSerial = null;
                }
            }

            // Detect new connections
            foreach (var device in current)
            {
                if (_knownDevices.ContainsKey(device.Serial))
                    continue;

                Logger.Info($"检测到新设备: {device.Serial}");
                _knownDevices[device.Serial] = device;

                bool hasSpeaker = _adb.CheckHasSpeaker(device.Serial);
                device.HasSpeaker = hasSpeaker;

                if (hasSpeaker)
                {
                    device.State = DeviceState.Ready;
                    // Fetch and store device model
                    string? model = _adb.GetDeviceModel(device.Serial);
                    if (!string.IsNullOrEmpty(model))
                        device.Model = model;
                    Logger.Info($"设备 {device.Serial} ({device.Model}) 具有扬声器，准备就绪");
                    _activeSerial ??= device.Serial;
                    DeviceConnected?.Invoke(this, device);
                }
                else
                {
                    device.State = DeviceState.Unsupported;
                    Logger.Warn($"设备 {device.Serial} 不包含扬声器，无法作为音频输出设备");
                    ErrorOccurred?.Invoke(this, $"设备 {device.Serial} 不包含扬声器，无法使用");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"设备检测异常: {ex.Message}");
        }
        finally
        {
            _isPolling = false;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
