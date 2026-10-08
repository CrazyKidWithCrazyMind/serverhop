namespace ServerHop.Core;

/// <summary>Hidden command-line modes: `ServerHop.exe --selftest | --help | --version`.</summary>
public static class Cli
{
    public static int Run(string[] args)
    {
        var cmd = args[0].ToLowerInvariant();
        switch (cmd)
        {
            case "--help":
            case "-h":
            case "/?":
                PrintHelp();
                return 0;

            case "--version":
            case "-v":
                Console.WriteLine("SERVERHOP 1.0.0");
                return 0;

            case "--selftest":
                return SelfTest();

            default:
                Console.WriteLine($"serverhop: unknown option '{cmd}'");
                Console.WriteLine();
                PrintHelp();
                return 2;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("SERVERHOP - Roblox Server Picker v1.0.0");
        Console.WriteLine();
        Console.WriteLine("  ServerHop.exe              start the GUI");
        Console.WriteLine("  ServerHop.exe --selftest   verify parsing + live Roblox endpoints (read-only)");
        Console.WriteLine("  ServerHop.exe --version    print version");
        Console.WriteLine("  ServerHop.exe --help       this text");
    }

    private static void PrintResult(int index, string label, string status, string detail)
        => Console.WriteLine($"[{index,1}/7] {label,-24} {status,-6} {detail}");

    private static int SelfTest()
    {
        Console.WriteLine("SERVERHOP SELFTEST");
        Console.WriteLine(new string('-', 64));

        var failures = 0;

        // 1 — app data dir writable
        try
        {
            Paths.EnsureAppDir();
            var probe = Path.Combine(Paths.App, "selftest.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            PrintResult(1, "app data dir", "OK", Paths.App);
        }
        catch (Exception ex)
        {
            failures++;
            PrintResult(1, "app data dir", "FAIL", ex.Message);
        }

        // 2 — place id parser (the one input the whole app hangs on)
        try
        {
            var cases = new (string Input, long? Want)[]
            {
                ("4924922222", 4924922222),
                ("https://www.roblox.com/games/4924922222/Brookhaven-RP", 4924922222),
                ("https://roblox.com/games/1234567890/Slug?foo=bar", 1234567890),
                ("https://www.roblox.com/games?placeId=5470524385", 5470524385),
                ("roblox://experiences/start?placeId=6872912243&gameInstanceId=x", 6872912243),
                ("https://www.roblox.com/share?code=abc&type=Server", null),
                ("not a link at all", null),
            };
            var bad = cases.Where(c => RobloxApi.ParsePlaceId(c.Input) != c.Want).ToList();
            if (bad.Count > 0)
            {
                failures++;
                PrintResult(2, "place-id parser", "FAIL",
                    $"{bad.Count}/{cases.Length} wrong, first: '{bad[0].Input}'");
            }
            else
            {
                PrintResult(2, "place-id parser", "OK", $"{cases.Length} url forms handled");
            }
        }
        catch (Exception ex)
        {
            failures++;
            PrintResult(2, "place-id parser", "FAIL", ex.Message);
        }

        // 3 — deep link format is exactly what Roblox documents
        try
        {
            var url = RobloxApi.JoinUrl(4924922222, "db761fba-9470-4fbc-807b-3e2bf1dd19c3");
            const string want =
                "roblox://experiences/start?placeId=4924922222&gameInstanceId=db761fba-9470-4fbc-807b-3e2bf1dd19c3";
            if (url != want) { failures++; PrintResult(3, "deep link format", "FAIL", url); }
            else PrintResult(3, "deep link format", "OK", url);
        }
        catch (Exception ex)
        {
            failures++;
            PrintResult(3, "deep link format", "FAIL", ex.Message);
        }

        // 4 — config round-trip (restores the real file afterwards)
        try
        {
            string? original = null;
            var existed = File.Exists(Paths.ConfigFile);
            if (existed) original = File.ReadAllText(Paths.ConfigFile);

            try
            {
                var cfg = new AppConfig { LastPlaceId = 111222333, LastGameName = "probe", SortMode = 1 };
                cfg.Save();
                var back = AppConfig.Load();
                if (back.LastPlaceId != 111222333 || back.LastGameName != "probe" || back.SortMode != 1)
                {
                    failures++;
                    PrintResult(4, "config round-trip", "FAIL", "values did not survive save/load");
                }
                else
                {
                    PrintResult(4, "config round-trip", "OK", Paths.ConfigFile);
                }
            }
            finally
            {
                if (existed) File.WriteAllText(Paths.ConfigFile, original!);
                else if (File.Exists(Paths.ConfigFile)) File.Delete(Paths.ConfigFile);
            }
        }
        catch (Exception ex)
        {
            failures++;
            PrintResult(4, "config round-trip", "FAIL", ex.Message);
        }

        // 5 — http client is dressed as a browser (Roblox 400s otherwise)
        try
        {
            var ua = RobloxApi.HttpUserAgent;
            var referrer = RobloxApi.HttpReferrer;
            if (string.IsNullOrWhiteSpace(ua) || string.IsNullOrWhiteSpace(referrer))
            {
                failures++;
                PrintResult(5, "http client headers", "FAIL", "missing UA or Referer");
            }
            else
            {
                PrintResult(5, "http client headers", "OK", "browser UA + roblox referrer");
            }
        }
        catch (Exception ex)
        {
            failures++;
            PrintResult(5, "http client headers", "FAIL", ex.Message);
        }

        // 6 — live: place → universe → game name (WARN on flake: Roblox rate-limits bursts)
        try
        {
            var universe = RobloxApi.ResolveUniverseAsync(4924922222).GetAwaiter().GetResult();
            var game = RobloxApi.GetGameAsync(universe, 4924922222, fetchIcon: false).GetAwaiter().GetResult();
            if (universe != 1686885941)
                PrintResult(6, "live game metadata", "WARN", $"unexpected universe {universe}");
            else
                PrintResult(6, "live game metadata", "OK",
                    $"{game.Name} (universe {universe}, {game.Playing} playing)");
        }
        catch (Exception ex)
        {
            PrintResult(6, "live game metadata", "WARN", "endpoints unreachable: " + ex.Message);
        }

        // 7 — live: public server page (same WARN tolerance)
        try
        {
            var page = RobloxApi.GetServersAsync(4924922222).GetAwaiter().GetResult();
            if (page.Servers.Count == 0)
                PrintResult(7, "live server list", "WARN", "0 servers returned");
            else
            {
                var ping = page.Servers[0].Ping;
                PrintResult(7, "live server list", "OK",
                    $"{page.Servers.Count} servers, first ping {ping} ms, more={page.NextCursor is not null}");
            }
        }
        catch (Exception ex)
        {
            PrintResult(7, "live server list", "WARN", "endpoints unreachable: " + ex.Message);
        }

        Console.WriteLine(new string('-', 64));
        if (failures == 0)
        {
            Console.WriteLine("SELFTEST PASSED - everything looks good.");
            return 0;
        }
        Console.WriteLine($"SELFTEST FAILED - {failures} problem(s) found.");
        return 1;
    }
}
