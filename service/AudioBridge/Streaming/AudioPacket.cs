using System;

namespace AudioBridge.Streaming;

public class AudioPacket
{
    public byte[] Data { get; }
    public DateTime Timestamp { get; }

    public AudioPacket(byte[] data)
    {
        Data = data;
        Timestamp = DateTime.UtcNow;
    }
}
