using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace ServerHop.Core;

/// <summary>A server row pre-baked for the UI (sorting happens in Vm, this only displays).</summary>
public sealed class ServerRow
{
    private static readonly Brush PingGood = Frozen("#A3FF12");
    private static readonly Brush PingMid = Frozen("#E9F1E6");
    private static readonly Brush PingSlow = Frozen("#FFB020");
    private static readonly Brush PingBad = Frozen("#FF4B3E");
    private static readonly Brush PingNone = Frozen("#59625B");

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public ServerInfo S { get; }

    public ServerRow(ServerInfo s)
    {
        S = s;
        PingBrush = s.Ping <= 0 ? PingNone
            : s.Ping <= 100 ? PingGood
            : s.Ping <= 200 ? PingMid
            : s.Ping <= 350 ? PingSlow
            : PingBad;
    }

    public string Id => S.Id;
    public string ShortId => S.Id.Length > 8 ? S.Id[..8] + "…" : S.Id;
    public string PingText => S.Ping <= 0 ? "—" : S.Ping.ToString();
    public string PlayersText => $"{S.Playing}/{S.MaxPlayers}";
    public double FillWidth => S.MaxPlayers <= 0 ? 0 : Math.Round(78.0 * S.Playing / S.MaxPlayers, 1);
    public bool IsFull => S.MaxPlayers > 0 && S.Playing >= S.MaxPlayers;
    public string JoinName => $"Join {ShortId}";
    public Brush PingBrush { get; }
}

/// <summary>Single view-model: status pill, game card, sorted server rows, persisted prefs.</summary>
public sealed class Vm : INotifyPropertyChanged
{
    public readonly AppConfig Config = AppConfig.Load();
    private readonly List<ServerInfo> _raw = new();

    public ObservableCollection<ServerRow> Rows { get; } = new();
    public ObservableCollection<LogEntry> Logs { get; } = new();

    public IReadOnlyList<ServerInfo> Servers => _raw;

    public Vm()
    {
        // fields, not setters: hydration must not trigger sorting side effects
        _sort = Math.Clamp(Config.SortMode, 0, 1);
        _asc = Config.Ascending;
        _auto = Config.AutoRefresh;
        _input = Config.LastPlaceId > 0 ? Config.LastPlaceId.ToString() : "";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static bool Eq<T>(T a, T b) => EqualityComparer<T>.Default.Equals(a, b);

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Eq(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    // ── status pill ──────────────────────────────────────────────────────────

    private static readonly Brush ReadyBrush = Frozen("#A3FF12");
    private static readonly Brush WarnBrush = Frozen("#FFB020");
    private static readonly Brush BadBrush = Frozen("#FF4B3E");

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private string _statusText = "READY";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private Brush _statusBrush = ReadyBrush;
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }

    public void SetStatus(string text, string kind)
    {
        StatusText = text;
        StatusBrush = kind switch { "warn" => WarnBrush, "bad" => BadBrush, _ => ReadyBrush };
    }

    private bool _busy;
    public bool IsBusy { get => _busy; set => Set(ref _busy, value); }

    // ── input + game card ────────────────────────────────────────────────────

    private string _input = "";
    public string InputText { get => _input; set => Set(ref _input, value); }

    private bool _hasGame;
    public bool HasGame
    {
        get => _hasGame;
        set
        {
            if (Set(ref _hasGame, value)) Raise(nameof(PlaceholderText));
        }
    }

    private string _gameName = "";
    public string GameName { get => _gameName; set => Set(ref _gameName, value); }

    private string _gameMeta = "";
    public string GameMeta { get => _gameMeta; set => Set(ref _gameMeta, value); }

    private ImageSource? _gameIcon;
    public ImageSource? GameIcon { get => _gameIcon; set => Set(ref _gameIcon, value); }

    // ── sorting (persisted) ──────────────────────────────────────────────────

    private int _sort;
    public int SortMode
    {
        get => _sort;
        set
        {
            var c = Math.Clamp(value, 0, 1);
            if (c == _sort) return;
            _sort = c;
            Config.SortMode = c;

            // sensible default direction per key: ping low-first, players high-first
            var wantAsc = c == 0;
            if (_asc != wantAsc)
            {
                _asc = wantAsc;
                Config.Ascending = wantAsc;
                Raise(nameof(Ascending));
                Raise(nameof(SortDirText));
            }
            Config.Save();
            Raise();
            Resort();
        }
    }

    private bool _asc;
    public bool Ascending
    {
        get => _asc;
        set
        {
            if (!Set(ref _asc, value)) return;
            Config.Ascending = value;
            Config.Save();
            Raise(nameof(SortDirText));
            Resort();
        }
    }

    public string SortDirText => _asc ? "↑ ASC" : "↓ DESC";

    // ── auto refresh (persisted) ─────────────────────────────────────────────

    private bool _auto;
    public bool AutoRefresh
    {
        get => _auto;
        set
        {
            if (!Set(ref _auto, value)) return;
            Config.AutoRefresh = value;
            Config.Save();
        }
    }

    // ── server list state ────────────────────────────────────────────────────

    private bool _hasMore;
    public bool HasMore { get => _hasMore; set => Set(ref _hasMore, value); }

    public bool HasServers => _raw.Count > 0;

    public string ServerCountText => _raw.Count == 0 ? "NO SERVERS" : $"{_raw.Count} SERVERS";

    public string PlaceholderText => HasGame
        ? "NO SERVERS RETURNED — TRY REFRESH"
        : "PASTE A ROBLOX GAME LINK — THEN PRESS LOAD";

    public bool ShowPlaceholder => _raw.Count == 0;

    public bool CanJoinBest => _raw.Count > 0;

    /// <summary>Replaces or appends raw servers, then re-sorts and updates derived state.</summary>
    public void SetServers(IReadOnlyList<ServerInfo> servers, bool append)
    {
        if (append) _raw.AddRange(servers);
        else
        {
            _raw.Clear();
            _raw.AddRange(servers);
        }

        Resort();
        Raise(nameof(ServerCountText));
        Raise(nameof(HasServers));
        Raise(nameof(ShowPlaceholder));
        Raise(nameof(CanJoinBest));
    }

    /// <summary>Lowest-ping server that is not full (falls back to emptiest).</summary>
    public ServerInfo? BestPing()
    {
        var open = _raw.Where(s => s.MaxPlayers <= 0 || s.Playing < s.MaxPlayers).ToList();
        if (open.Count == 0) return null;
        var withPing = open.Where(s => s.Ping > 0).ToList();
        return withPing.Count > 0 ? withPing.OrderBy(s => s.Ping).First()
                                  : open.OrderBy(s => s.Playing).First();
    }

    /// <summary>Fewest players first (tie-break: lower ping).</summary>
    public ServerInfo? Emptiest()
    {
        if (_raw.Count == 0) return null;
        return _raw
            .OrderBy(s => s.Playing)
            .ThenBy(s => s.Ping <= 0 ? int.MaxValue : s.Ping)
            .First();
    }

    private void Resort()
    {
        IEnumerable<ServerInfo> q = _sort switch
        {
            1 => _asc ? _raw.OrderBy(s => s.Playing) : _raw.OrderByDescending(s => s.Playing),
            _ => PingOrdered()
        };

        Rows.Clear();
        foreach (var s in q)
            Rows.Add(new ServerRow(s));
    }

    /// <summary>ping asc by default; unknown pings (0) always sink to the bottom.</summary>
    private IOrderedEnumerable<ServerInfo> PingOrdered()
        => _asc
            ? _raw.OrderBy(s => s.Ping <= 0 ? int.MaxValue : s.Ping)
            : _raw.OrderByDescending(s => s.Ping <= 0 ? -1 : s.Ping);

    // ── hydration ────────────────────────────────────────────────────────────

    public void ApplyGame(GameInfo game)
    {
        HasGame = true;
        GameName = game.Name;
        GameMeta = $"{game.Playing:N0} PLAYING   ·   PLACE {game.PlaceId}   ·   UNIVERSE {game.UniverseId}";

        Config.LastPlaceId = game.PlaceId;
        Config.LastGameName = game.Name;
        Config.Save();
    }
}
