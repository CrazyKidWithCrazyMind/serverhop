using System.Text.Json;

namespace ServerHop.Core;

/// <summary>Preferences that live inside SERVERHOP — last game, sort mode, refresh interval.</summary>
public sealed class AppConfig
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public long LastPlaceId { get; set; }
    public string LastGameName { get; set; } = "";
    public bool AutoRefresh { get; set; }
    public int SortMode { get; set; }        // 0=ping 1=players
    public bool Ascending { get; set; } = true;

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Paths.ConfigFile))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Paths.ConfigFile)) ?? new AppConfig();
        }
        catch { /* corrupt config is not worth failing over */ }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Paths.EnsureAppDir();
            File.WriteAllText(Paths.ConfigFile, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* non-fatal */ }
    }
}
