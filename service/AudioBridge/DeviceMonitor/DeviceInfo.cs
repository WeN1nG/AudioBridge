namespace AudioBridge.DeviceMonitor;

public enum DeviceState
{
    Disconnected,
    Connected,
    Ready,
    Unsupported
}

public class DeviceInfo
{
    public string Serial { get; set; } = "";
    public string Model { get; set; } = "";
    public bool HasSpeaker { get; set; }
    public DeviceState State { get; set; } = DeviceState.Disconnected;
}
