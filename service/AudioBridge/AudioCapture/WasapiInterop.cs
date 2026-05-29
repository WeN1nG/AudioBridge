using System;
using System.Runtime.InteropServices;
using AudioBridge.Utils;

#pragma warning disable CA1416

namespace AudioBridge.AudioCapture;

// --- COM CLSID ---

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumerator { }

// --- Constants ---

internal enum EDataFlow
{
    eRender,
    eCapture,
    eAll
}

internal enum ERole
{
    eConsole,
    eMultimedia,
    eCommunications
}

internal static class AudioClientConst
{
    public const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;

    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x00000001;

    public const int WAVE_FORMAT_PCM = 1;
    public const int WAVE_FORMAT_IEEE_FLOAT = 3;
    public const int WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    public static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new("00000001-0000-0010-8000-00AA00389B71");
    public static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00AA00389B71");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
}

// --- Structures ---

[StructLayout(LayoutKind.Sequential)]
internal struct WaveFormatEx
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaveFormatExtensible
{
    public WaveFormatEx Format;
    public ushort wValidBitsPerSample;
    public uint dwChannelMask;
    public Guid SubFormat;
}

// --- COM Interfaces ---

[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int NotImpl1();
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
    [PreserveSig] int NotImpl3();
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient pClient);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
}

[Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid riid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    [PreserveSig] int NotImpl2();
    [PreserveSig] int NotImpl3();
}

[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
    [PreserveSig] int NotImpl3();
    [PreserveSig] int NotImpl4();
    [PreserveSig] int NotImpl5();
    [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
    [PreserveSig] int NotImpl7();
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int NotImpl10();
    [PreserveSig] int NotImpl11();
    [PreserveSig] int GetService(ref Guid riid, out IntPtr ppv);
}

[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
    [PreserveSig] int ReleaseBuffer(uint numFramesRead);
    [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
}

/// <summary>
/// Callback interface for audio device change notifications.
/// Implemented in C# and registered with IMMDeviceEnumerator.RegisterEndpointNotificationCallback.
/// </summary>
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnDefaultDeviceChanged(int dataFlow, int role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
    void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int key);
}
