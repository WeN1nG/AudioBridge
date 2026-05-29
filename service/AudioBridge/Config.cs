using System.IO;
using System.Text.Json;
using AudioBridge.Utils;

namespace AudioBridge;

public class Config
{
    public AudioConfig Audio { get; set; } = new();
    public AdbConfig Adb { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();

    public class AudioConfig
    {
        public int TargetSampleRate { get; set; } = 48000;
        public int TargetBitsPerSample { get; set; } = 32;
        public int TargetChannels { get; set; } = 2;
        public int BufferSizeMs { get; set; } = 50;
        public double PreAttenuationDb { get; set; } = -6.0;
    }

    public class AdbConfig
    {
        public string AdbPath { get; set; } = "";
        public string PlayerBinary { get; set; } = "audio_player";
        public string PlayerRemotePath { get; set; } = "/data/local/tmp/audio_player";
    }

    public class LoggingConfig
    {
        public string Level { get; set; } = "Debug";
    }

    public static Config Load(string path)
    {
        Logger.Debug("[Function] static Config Config::Load(string) start");

        if (!File.Exists(path))
        {
            Logger.Warn($"配置文件不存在: {path}，使用默认配置");
            Logger.Debug("[Function] static Config Config::Load(string) end (default)");
            return new Config();
        }

        string json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<Config>(json) ?? new Config();

        Logger.Debug("[Function] static Config Config::Load(string) end");
        return config;
    }
}
