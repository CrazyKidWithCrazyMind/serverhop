using System.Diagnostics;

namespace ServerHop.Core;

/// <summary>Launches Roblox straight into a chosen server via the documented deep link.</summary>
public static class Launcher
{
    /// <summary>Opens the roblox:// URL. Returns true when the OS accepted the request
    /// (the client then joins on its own — we cannot observe the in-game result).</summary>
    public static bool Join(long placeId, string serverId)
    {
        var url = RobloxApi.JoinUrl(placeId, serverId);
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            LogService.Err($"launch failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Opens the experience page in the default browser (fallback when a server died).</summary>
    public static void OpenInBrowser(long placeId)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"https://www.roblox.com/games/{placeId}")
            { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.Warn($"browser open failed: {ex.Message}");
        }
    }
}
