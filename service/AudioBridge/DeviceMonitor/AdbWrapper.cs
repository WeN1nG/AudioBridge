using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using AudioBridge.Utils;

namespace AudioBridge.DeviceMonitor;

public class AdbWrapper
{
    private readonly string _adbPath;

    public AdbWrapper(string adbPath)
    {
        Logger.Debug($"[Function] void AdbWrapper::ctor(string) start, adbPath={adbPath}");
        _adbPath = adbPath;
        Logger.Debug("[Function] void AdbWrapper::ctor(string) end");
    }

    public bool IsAvailable()
    {
        Logger.Debug("[Function] bool AdbWrapper::IsAvailable() start");
        try
        {
            var result = RunAdbCommand("--version");
            bool available = !string.IsNullOrEmpty(result);
            Logger.Debug($"[Function] bool AdbWrapper::IsAvailable() end => {available}");
            return available;
        }
        catch
        {
            Logger.Debug("[Function] bool AdbWrapper::IsAvailable() end => false");
            return false;
        }
    }

    public List<DeviceInfo> GetDevices()
    {
        Logger.Debug("[Function] List AdbWrapper::GetDevices() start");
        var devices = new List<DeviceInfo>();
        string? output = RunAdbCommand("devices");

        if (string.IsNullOrEmpty(output))
        {
            Logger.Debug("[Function] List AdbWrapper::GetDevices() end (empty)");
            return devices;
        }

        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.StartsWith("List of") || string.IsNullOrEmpty(line))
                continue;

            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1] == "device")
            {
                devices.Add(new DeviceInfo
                {
                    Serial = parts[0],
                    State = DeviceState.Connected
                });
                Logger.Debug($"    Found device: {parts[0]}");
            }
        }

        Logger.Debug($"[Function] List AdbWrapper::GetDevices() end => {devices.Count} devices");
        return devices;
    }

    public bool CheckHasSpeaker(string serial)
    {
        Logger.Debug($"[Function] bool AdbWrapper::CheckHasSpeaker(string) start, serial={serial}");
        string? output = RunAdbCommand($"-s {serial} shell dumpsys audio");

        if (string.IsNullOrEmpty(output))
        {
            Logger.Debug("[Function] bool AdbWrapper::CheckHasSpeaker(string) end => false (no output)");
            return false;
        }

        // Log first portion of dumpsys output for debugging format issues
        var sample = output.Length > 500 ? output[..500] : output;
        Logger.Debug($"dumpsys audio head: {sample}");

        // Check DEVICE_OUT_* constants (Android <13, AudioFlinger format)
        bool hasSpeaker = output.Contains("DEVICE_OUT_SPEAKER", StringComparison.Ordinal);
        bool hasEarpiece = output.Contains("DEVICE_OUT_EARPIECE", StringComparison.Ordinal);
        bool hasWiredHeadset = output.Contains("DEVICE_OUT_WIRED_HEADSET", StringComparison.Ordinal);
        bool hasUsbDevice = output.Contains("DEVICE_OUT_USB_DEVICE", StringComparison.Ordinal);

        // Check lowercase device names in "Devices: <name>" lines (Android 13+, AudioService format)
        // Output contains lines like: "Devices: speaker" or "2 (speaker): 3"
        bool hasSpeakerAlt = output.Contains("speaker", StringComparison.Ordinal);
        bool hasEarpieceAlt = output.Contains("earpiece", StringComparison.Ordinal);
        bool hasHeadsetAlt = output.Contains("headset", StringComparison.Ordinal) &&
            !output.Contains("DEVICE_OUT_WIRED_HEADSET", StringComparison.Ordinal); // avoid double-count

        bool result = hasSpeaker || hasEarpiece || hasWiredHeadset || hasUsbDevice
            || hasSpeakerAlt || hasEarpieceAlt || hasHeadsetAlt;
        Logger.Debug($"[Function] bool AdbWrapper::CheckHasSpeaker(string) end => {result} " +
            $"(speaker={hasSpeaker} earpiece={hasEarpiece} headset={hasWiredHeadset} usb={hasUsbDevice} " +
            $"speaker_alt={hasSpeakerAlt} earpiece_alt={hasEarpieceAlt} headset_alt={hasHeadsetAlt})");
        return result;
    }

    public bool PushFile(string local, string remote, string serial)
    {
        Logger.Debug($"[Function] bool AdbWrapper::PushFile(...) start");
        string? output = RunAdbCommand($"-s {serial} push \"{local}\" \"{remote}\"");
        bool success = output != null
            && !output.Contains("error", StringComparison.OrdinalIgnoreCase)
            && !output.Contains("failed", StringComparison.OrdinalIgnoreCase);
        Logger.Debug($"[Function] bool AdbWrapper::PushFile(...) end => {success}");
        return success;
    }

    public bool SetExecutable(string remote, string serial)
    {
        Logger.Debug("[Function] bool AdbWrapper::SetExecutable(...) start");
        RunAdbCommand($"-s {serial} shell chmod 755 \"{remote}\"");
        Logger.Debug("[Function] bool AdbWrapper::SetExecutable(...) end");
        return true;
    }

    public bool SetupForward(string serial, int port)
    {
        Logger.Debug($"[Function] bool AdbWrapper::SetupForward(...) start, port={port}");
        // Clean up any existing forward first to avoid "cannot bind listener" on restart
        RunAdbCommand($"-s {serial} forward --remove tcp:{port}");
        string? output = RunAdbCommand($"-s {serial} forward tcp:{port} tcp:{port}");
        bool ok = output == null || !output.Contains("error", StringComparison.OrdinalIgnoreCase);
        Logger.Debug($"[Function] bool AdbWrapper::SetupForward(...) end => {ok}");
        return ok;
    }

    public bool RemoveForward(string serial, int port)
    {
        Logger.Debug($"[Function] bool AdbWrapper::RemoveForward(...) start, port={port}");
        RunAdbCommand($"-s {serial} forward --remove tcp:{port}");
        Logger.Debug("[Function] bool AdbWrapper::RemoveForward(...) end");
        return true;
    }

    public Process StartAudioPlayer(string serial, string remotePath, int port = 0, int sampleRate = 48000, int channels = 2, int bitsPerSample = 32, int bufferMs = 50)
    {
        Logger.Debug("[Function] Process AdbWrapper::StartAudioPlayer(...) start");
        // shell -T (raw pipe): 仅用于启动 audio_player 并捕获其 stderr 诊断输出
        // PCM 数据通过 Unix 域套接字 + adb forward 传输，不走 stdin
        string args = $"-s {serial} shell -T \"{remotePath}\" {sampleRate} {channels} {bitsPerSample} {bufferMs} {port}";
        var psi = new ProcessStartInfo
        {
            FileName = _adbPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var process = new Process { StartInfo = psi };
        process.Start();
        Logger.Debug("[Function] Process AdbWrapper::StartAudioPlayer(...) end");
        return process;
    }

    public void KillAudioPlayer(Process? process)
    {
        Logger.Debug("[Function] void AdbWrapper::KillAudioPlayer(Process) start");
        if (process != null && !process.HasExited)
        {
            process.Kill(true);
            process.Dispose();
        }
        Logger.Debug("[Function] void AdbWrapper::KillAudioPlayer(Process) end");
    }

    public string? GetDeviceModel(string serial)
    {
        Logger.Debug($"[Function] string AdbWrapper::GetDeviceModel(string) start, serial={serial}");
        string? output = RunAdbCommand($"-s {serial} shell getprop ro.product.model");
        string? model = output?.Trim();
        if (string.IsNullOrEmpty(model) || model == "unknown")
        {
            // Fallback: try to get from build properties
            output = RunAdbCommand($"-s {serial} shell getprop ro.product.name");
            model = output?.Trim();
        }
        Logger.Debug($"[Function] string AdbWrapper::GetDeviceModel(string) end => '{model}'");
        return model;
    }

    public bool CleanupRemote(string remote, string serial)
    {
        Logger.Debug("[Function] bool AdbWrapper::CleanupRemote(...) start");
        RunAdbCommand($"-s {serial} shell rm -f \"{remote}\"");
        Logger.Debug("[Function] bool AdbWrapper::CleanupRemote(...) end");
        return true;
    }

    private string? RunAdbCommand(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _adbPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output;
        }
        catch (Exception ex)
        {
            Logger.Error($"ADB 命令失败: {args} - {ex.Message}");
            return null;
        }
    }
}
