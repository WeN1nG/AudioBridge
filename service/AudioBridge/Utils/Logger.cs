using System;
using System.IO;

namespace AudioBridge.Utils;

public static class Logger
{
    private static readonly string _logPath;
    private static readonly object _lock = new();

    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    public static LogLevel Level { get; set; } = LogLevel.Info;

    static Logger()
    {
        var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Log");
        Directory.CreateDirectory(logDir);
        CleanupOldLogs(logDir);
        var fileName = $"{DateTime.Now:yy-M-dd-HH-mm-ss}.log";
        _logPath = Path.Combine(logDir, fileName);
        File.WriteAllText(_logPath, $"=== AudioBridge Log Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [INFO] 日志文件: {_logPath}");
    }

    private static void CleanupOldLogs(string logDir)
    {
        var today = DateTime.Now.Date;
        foreach (var file in Directory.GetFiles(logDir, "*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (DateTime.TryParseExact(name, "yy-M-dd-HH-mm-ss", null,
                    System.Globalization.DateTimeStyles.None, out var fileDate))
            {
                if (fileDate.Date < today)
                {
                    File.Delete(file);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [INFO] 已删除旧日志: {Path.GetFileName(file)}");
                }
            }
        }
    }

    public static void Debug(string message)
    {
        if (Level <= LogLevel.Debug)
            WriteLine("DEBUG", message, ConsoleColor.Gray);
    }

    public static void Info(string message)
    {
        if (Level <= LogLevel.Info)
            WriteLine("INFO", message, ConsoleColor.Cyan);
    }

    public static void Warn(string message)
    {
        if (Level <= LogLevel.Warn)
            WriteLine("WARN", message, ConsoleColor.Yellow);
    }

    public static void Error(string message)
    {
        if (Level <= LogLevel.Error)
            WriteLine("ERROR", message, ConsoleColor.Red);
    }

    private static void WriteLine(string level, string message, ConsoleColor color)
    {
        var timestamp = DateTime.Now;
        var line = $"[{timestamp:HH:mm:ss}] [{level}] {message}";

        var original = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(line);
        Console.ForegroundColor = original;

        lock (_lock)
        {
            File.AppendAllText(_logPath, line + "\n");
        }
    }
}
