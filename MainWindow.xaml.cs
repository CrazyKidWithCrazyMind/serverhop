using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using ServerHop.Core;

namespace ServerHop;

public partial class MainWindow : Window
{
    private readonly Vm _vm = new();

    private long _placeId;
    private long _universeId;
    private string? _cursor;
    private readonly DispatcherTimer _autoTimer;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        LogService.OnEntry += OnLogEntry;

        // conservative cadence: one servers page every 15s while the toggle is on
        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _autoTimer.Tick += async (_, _) => await AutoTickAsync();
        _autoTimer.Start();
    }

    // ═══════════════════ lifecycle ═══════════════════

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _vm.SetStatus("READY", "ready");

        if (_vm.Config.LastPlaceId > 0)
        {
            var name = string.IsNullOrEmpty(_vm.Config.LastGameName)
                ? _vm.Config.LastPlaceId.ToString()
                : _vm.Config.LastGameName;
            LogService.Info($"restoring last game — {name}");
            _ = LoadGameAsync(_vm.Config.LastPlaceId);
        }
        else
        {
            LogService.Info("paste a roblox game link, then press LOAD");
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _autoTimer.Stop();
        _vm.Config.Save();
        LogService.OnEntry -= OnLogEntry;
    }

    private void OnLogEntry(LogEntry entry)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnLogEntry(entry));
            return;
        }

        _vm.Logs.Add(entry);
        while (_vm.Logs.Count > 400)
            _vm.Logs.RemoveAt(0);

        LogScroll.ScrollToEnd();
    }

    // ═══════════════════ load / refresh ═══════════════════

    private async void Load_Click(object sender, RoutedEventArgs e) => await LoadFromInputAsync();

    private async void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await LoadFromInputAsync();
    }

    private async Task LoadFromInputAsync()
    {
        var placeId = RobloxApi.ParsePlaceId(_vm.InputText ?? "");
        if (placeId is null)
        {
            _vm.SetStatus("BAD INPUT", "bad");
            LogService.Warn("could not find a place id — paste the game url or the bare id");
            return;
        }

        await LoadGameAsync(placeId.Value);
    }

    private async Task LoadGameAsync(long placeId)
    {
        if (_vm.IsBusy) return;

        _vm.IsBusy = true;
        _vm.SetStatus("SCANNING…", "warn");
        try
        {
            LogService.Info($"resolving place {placeId}…");
            var universe = await RobloxApi.ResolveUniverseAsync(placeId);
            var game = await RobloxApi.GetGameAsync(universe, placeId);

            _placeId = placeId;
            _universeId = universe;
            _vm.GameIcon = null;
            _vm.ApplyGame(game);
            _ = LoadIconAsync(game.IconUrl);

            LogService.Ok($"\"{game.Name}\" — {game.Playing:N0} playing now");

            // fresh page one
            _cursor = null;
            _vm.HasMore = false;
            _vm.SetServers(new List<ServerInfo>(), false);

            var page = await RobloxApi.GetServersAsync(placeId, null);
            _cursor = page.NextCursor;
            _vm.HasMore = _cursor is not null;
            _vm.SetServers(page.Servers, false);
            _vm.SetStatus(_vm.ServerCountText, "ready");
            LogService.Info(_cursor is not null
                ? $"{page.Servers.Count} servers listed — more available (LOAD MORE)"
                : $"{page.Servers.Count} servers listed — full list");
        }
        catch (Exception ex)
        {
            LogFail(ex);
        }
        finally
        {
            _vm.IsBusy = false;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy || _placeId <= 0) return;

        _vm.IsBusy = true;
        _vm.SetStatus("REFRESHING…", "warn");
        try
        {
            var page = await RobloxApi.GetServersAsync(_placeId, null);
            _cursor = page.NextCursor;
            _vm.HasMore = _cursor is not null;
            _vm.SetServers(page.Servers, false);
            _vm.SetStatus(_vm.ServerCountText, "ready");
            LogService.Info($"refreshed — {page.Servers.Count} servers");

            // live player count is a bonus, never a hard failure
            try
            {
                var game = await RobloxApi.GetGameAsync(_universeId, _placeId, fetchIcon: false);
                _vm.ApplyGame(game);
            }
            catch (Exception ex)
            {
                LogService.Warn("playing count not updated: " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            LogFail(ex);
        }
        finally
        {
            _vm.IsBusy = false;
        }
    }

    private async void LoadMore_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy || _placeId <= 0 || _cursor is null) return;

        _vm.IsBusy = true;
        try
        {
            var page = await RobloxApi.GetServersAsync(_placeId, _cursor);
            _cursor = page.NextCursor;
            _vm.HasMore = _cursor is not null;
            _vm.SetServers(page.Servers, append: true);
            _vm.SetStatus(_vm.ServerCountText, "ready");
            LogService.Info($"loaded {page.Servers.Count} more — {_vm.ServerCountText.ToLowerInvariant()}");
        }
        catch (Exception ex)
        {
            LogFail(ex);
        }
        finally
        {
            _vm.IsBusy = false;
        }
    }

    private async Task AutoTickAsync()
    {
        if (!_vm.AutoRefresh || _vm.IsBusy || _placeId <= 0) return;

        try
        {
            var page = await RobloxApi.GetServersAsync(_placeId, null);
            _cursor = page.NextCursor;
            _vm.HasMore = _cursor is not null;
            _vm.SetServers(page.Servers, false);
            _vm.SetStatus(_vm.ServerCountText, "ready");
            // intentionally quiet: no log line per 15s tick
        }
        catch (Exception ex)
        {
            LogFail(ex);
            if (ex.Message.Contains("rate limited", StringComparison.OrdinalIgnoreCase))
            {
                _vm.AutoRefresh = false;
                LogService.Warn("auto-refresh turned off to let the limit cool down");
            }
        }
    }

    private void LogFail(Exception ex)
    {
        var msg = ex is OperationCanceledException
            ? "request timed out — roblox may be throttling this ip"
            : ex.Message;
        LogService.Err(msg);

        if (msg.Contains("rate limited", StringComparison.OrdinalIgnoreCase))
        {
            _vm.SetStatus("RATE LIMITED", "warn");
            LogService.Warn("wait a few seconds, then press REFRESH");
        }
        else
        {
            _vm.SetStatus("ERROR", "bad");
        }
    }

    private async Task LoadIconAsync(string? url)
    {
        try
        {
            if (string.IsNullOrEmpty(url)) return;

            var bytes = await RobloxApi.DownloadAsync(url);
            if (bytes is null || bytes.Length == 0) return;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            _vm.GameIcon = bmp;
        }
        catch (Exception ex)
        {
            LogService.Warn("icon: " + ex.Message);
        }
    }

    // ═══════════════════ join ═══════════════════

    private void Join_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
            JoinServer(id);
    }

    private void Row_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;                       // single click just hovers
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
            return;                                          // the JOIN button handles itself

        if (sender is Border { DataContext: ServerRow row })
            JoinServer(row.Id);
    }

    private void JoinBest_Click(object sender, RoutedEventArgs e)
    {
        var s = _vm.BestPing();
        if (s is null)
        {
            LogService.Warn("no open server to join — load or refresh first");
            return;
        }
        JoinServer(s.Id);
    }

    private void JoinEmptiest_Click(object sender, RoutedEventArgs e)
    {
        var s = _vm.Emptiest();
        if (s is null)
        {
            LogService.Warn("no server loaded — load or refresh first");
            return;
        }
        JoinServer(s.Id);
    }

    private void JoinServer(string serverId)
    {
        if (_placeId <= 0 || string.IsNullOrEmpty(serverId)) return;

        if (Launcher.Join(_placeId, serverId))
        {
            var shortId = serverId.Length > 8 ? serverId[..8] : serverId;
            LogService.Ok($"launching roblox → server {shortId}");
            _vm.SetStatus("JOINING…", "warn");
            _ = RevertStatusAsync();
        }
    }

    private async Task RevertStatusAsync()
    {
        await Task.Delay(4000);
        if (!_vm.IsBusy)
            _vm.SetStatus(_vm.ServerCountText, "ready");
    }

    // ═══════════════════ sort ═══════════════════

    private void Dir_Click(object sender, RoutedEventArgs e)
        => _vm.Ascending = !_vm.Ascending;

    // ═══════════════════ log autoscroll ═══════════════════

    // (see OnLogEntry above)

    // ═══════════════════ window chrome ═══════════════════

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            MaxGlyph.Data = Geometry.Parse("M0.5,2.5 H4.5 V8.5 H0.5 Z M3.5,0.5 H9.5 V6.5 H3.5 Z");
        else if (WindowState == WindowState.Normal)
            MaxGlyph.Data = Geometry.Parse("M0.5,0.5 H9.5 V9.5 H0.5 Z");
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
            return; // clicks on header buttons must not start a drag

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // maximize to the work area (never cover the taskbar)
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmGetMinMaxInfo = 0x0024;
        if (msg != WmGetMinMaxInfo) return IntPtr.Zero;

        try
        {
            var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (monitor == IntPtr.Zero) return IntPtr.Zero;

            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

            mmi.ptMaxPosition.x = Math.Abs(info.rcWork.left - info.rcMonitor.left);
            mmi.ptMaxPosition.y = Math.Abs(info.rcWork.top - info.rcMonitor.top);
            mmi.ptMaxSize.x = info.rcWork.right - info.rcWork.left;
            mmi.ptMaxSize.y = info.rcWork.bottom - info.rcWork.top;
            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
        }
        catch { /* fall back to default behaviour */ }

        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointApi { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointApi ptReserved;
        public PointApi ptMaxSize;
        public PointApi ptMaxPosition;
        public PointApi ptMinTrackSize;
        public PointApi ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);
}
