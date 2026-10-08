using System.Windows.Media;

namespace ServerHop.Core;

public sealed class LogEntry
{
    public string Time { get; init; } = "";
    public string Level { get; init; } = "INFO";
    public string Message { get; init; } = "";
    public Brush Color { get; init; } = Brushes.Gray;
}

/// <summary>Central log: raises an event for the UI, appends to %LOCALAPPDATA%\ServerHop\serverhop.log.</summary>
public static class LogService
{
    public static event Action<LogEntry>? OnEntry;

    private static readonly Brush InfoBrush = Frozen("#8A968B");
    private static readonly Brush OkBrush = Frozen("#A3FF12");
    private static readonly Brush WarnBrush = Frozen("#FFB020");
    private static readonly Brush ErrBrush = Frozen("#FF4B3E");

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static void Info(string message) => Emit("INFO", message, InfoBrush);
    public static void Ok(string message) => Emit("OK", message, OkBrush);
    public static void Warn(string message) => Emit("WARN", message, WarnBrush);
    public static void Err(string message) => Emit("ERR", message, ErrBrush);

    private static void Emit(string level, string message, Brush color)
    {
        var entry = new LogEntry
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Level = level,
            Message = message,
            Color = color
        };

        try
        {
            Paths.EnsureAppDir();
            File.AppendAllText(Paths.LogFile,
                $"[{entry.Time}] {entry.Level,-5} {entry.Message}{Environment.NewLine}");
        }
        catch { /* file logging is best-effort */ }

        OnEntry?.Invoke(entry);
    }
}
