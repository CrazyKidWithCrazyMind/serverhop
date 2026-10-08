namespace ServerHop.Core;

/// <summary>Well-known filesystem locations for SERVERHOP.</summary>
public static class Paths
{
    public static string Local { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>%LOCALAPPDATA%\ServerHop — config + log.</summary>
    public static string App { get; } = Path.Combine(Local, "ServerHop");

    public static string ConfigFile => Path.Combine(App, "config.json");
    public static string LogFile => Path.Combine(App, "serverhop.log");

    public static void EnsureAppDir() => Directory.CreateDirectory(App);
}
