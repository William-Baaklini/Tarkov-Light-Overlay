using System.Diagnostics;

namespace TarkovOverlay;

/// <summary>
/// Ties the pieces together: read the slot under the cursor, look the item up,
/// show the card.
///
/// Two ways in, because they cost very different things:
///
///   * A hotkey. Free until pressed - nothing runs while you play.
///   * Hover mode. Polls only the cursor position at 15 Hz, which is a bare
///     GetCursorPos and costs nothing measurable. A capture happens only once
///     the cursor has settled AND has crossed into a different 63px cell than
///     the last scan, so sweeping a stash costs a couple of dozen scans rather
///     than one per frame. When the grid detector says "this is not an
///     inventory" the whole thing bails in well under a millisecond.
///
/// Scans run on a worker thread; the UI thread only ever receives the result.
/// </summary>
public sealed class ItemScanner : IDisposable
{
    /// <summary>How often we look at the cursor. Cheap enough to be invisible.</summary>
    private const int PollMs = 66;

    /// <summary>How long the cursor must sit still before hover mode looks.</summary>
    private const int DwellMs = 120;

    private readonly Config _cfg;
    private readonly Control _uiThread;
    private readonly ScanPopup _popup = new();
    private readonly PriceBook _prices = new();
    private readonly ScreenScan _scan = new();
    private readonly System.Windows.Forms.Timer _poll = new();

    private IconIndex? _index;
    private readonly object _scanLock = new();
    private volatile bool _busy;

    private Point _lastCursor = new(int.MinValue, int.MinValue);
    private DateTime _stillSince = DateTime.MinValue;
    private Point _lastScannedCell = new(int.MinValue, int.MinValue);
    private DateTime _lastRefresh = DateTime.MinValue;

    /// <summary>Raised on the UI thread when the user asks to open an item on tarkov.dev.</summary>
    public event Action<string>? OpenItemRequested;

    public ItemScanner(Config cfg, Control uiThread)
    {
        _cfg = cfg;
        _uiThread = uiThread;

        ScreenScan.Diagnostics = DumpScans;
        _prices.LoadCache();
        _poll.Interval = PollMs;
        _poll.Tick += (_, _) => OnPoll();
    }

    public bool IndexAvailable => (_index ??= IconIndex.Load()).Loaded;
    public int PriceCount => _prices.Count;
    public string? PriceError => _prices.LastError;

    public void ApplySettings()
    {
        if (_cfg.ScanHoverMode && _cfg.ScanEnabled) _poll.Start();
        else { _poll.Stop(); _popup.HidePopup(); }

        if (_cfg.ScanEnabled) _ = EnsurePricesAsync();
    }

    /// <summary>
    /// The scan hotkey. Pressing it again while a card is up opens that item on
    /// tarkov.dev, which saves binding a second shortcut for the common follow-up.
    /// </summary>
    public void HotkeyScan()
    {
        if (!_cfg.ScanEnabled) return;

        if (_popup.Visible && _popup.CurrentItemId is { } showing)
        {
            _popup.HidePopup();
            OpenItemRequested?.Invoke(showing);
            return;
        }

        Native.GetCursorPos(out var p);
        RunScan(new Point(p.X, p.Y));
    }

    public void HidePopup() => _popup.HidePopup();

    /// <summary>Called when the overlay hides, to hand memory back to the game.</summary>
    public void ReleaseMemory()
    {
        _popup.HidePopup();
        if (!_cfg.ScanHoverMode) _scan.ReleaseMemory();
    }

    private void OnPoll()
    {
        if (!_cfg.ScanEnabled || !_cfg.ScanHoverMode) return;

        Native.GetCursorPos(out var raw);
        var p = new Point(raw.X, raw.Y);

        if (p != _lastCursor)
        {
            _lastCursor = p;
            _stillSince = DateTime.UtcNow;
            return;
        }

        if (_stillSince == DateTime.MinValue) return;
        if ((DateTime.UtcNow - _stillSince).TotalMilliseconds < DwellMs) return;

        // Only rescan once the cursor has actually moved to a different cell.
        // Without this, resting on one item would rescan forever.
        var cell = new Point(p.X / IconIndex.Cell, p.Y / IconIndex.Cell);
        if (cell == _lastScannedCell) return;

        if (_cfg.ScanOnlyInTarkov && !TarkovIsForeground()) return;

        _lastScannedCell = cell;
        RunScan(p);
    }

    private void RunScan(Point cursor)
    {
        if (_busy) return;
        if (!IndexAvailable) return;
        _busy = true;

        _ = EnsurePricesAsync();

        ThreadPool.QueueUserWorkItem(_ =>
        {
            ScanResult? result = null;
            try
            {
                lock (_scanLock) result = _scan.Scan(cursor, _index!);
            }
            catch { /* a scan is never worth taking the app down */ }
            finally
            {
                try
                {
                    if (!_uiThread.IsDisposed)
                        _uiThread.BeginInvoke(() => Present(result, cursor));
                }
                catch { /* form went away mid-scan */ }
                _busy = false;
            }
        });
    }

    /// <summary>
    /// Set TARKOV_OVERLAY_SCAN_DUMP=1 to write every scan to
    /// %LOCALAPPDATA%\TarkovOverlay\scan-dump.png plus a .txt of what it decided.
    /// For working out why a scan read the wrong thing on a real screen.
    /// </summary>
    private static readonly bool DumpScans =
        Environment.GetEnvironmentVariable("TARKOV_OVERLAY_SCAN_DUMP") == "1";

    private void DumpScan(ScanResult? result, Point cursor)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TarkovOverlay");
            Directory.CreateDirectory(dir);

            lock (_scanLock)
            {
                // One file per scan, so a batch of real examples can be collected
                // in a session and labelled afterwards.
                string stamp = DateTime.Now.ToString("HHmmss");
                if (_scan.LastCapture is { } bmp)
                    bmp.Save(Path.Combine(dir, $"scan-{stamp}.png"),
                             System.Drawing.Imaging.ImageFormat.Png);

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"cursor (desktop)   {cursor.X},{cursor.Y}");
                sb.AppendLine($"capture origin     {_scan.LastCaptureOrigin.X},{_scan.LastCaptureOrigin.Y}");
                sb.AppendLine($"capture size       {_scan.LastCapture?.Width}x{_scan.LastCapture?.Height}");
                sb.AppendLine($"cursor in capture  {cursor.X - _scan.LastCaptureOrigin.X},{cursor.Y - _scan.LastCaptureOrigin.Y}");
                foreach (var sc in Screen.AllScreens)
                    sb.AppendLine($"screen             {sc.DeviceName} {sc.Bounds} primary={sc.Primary}");
                sb.AppendLine($"phases (sumX,medX,sumY,medY) {_scan.LastPhases}");
                foreach (var line in _scan.LastTrace) sb.AppendLine("try                " + line);
                if (result is null) sb.AppendLine("result             none");
                else
                {
                    sb.AppendLine($"slot (desktop)     {result.SlotRect}");
                    foreach (var c in result.TiedUpTo(6))
                        sb.AppendLine($"candidate          {c.ItemId} {c.GridW}x{c.GridH}" +
                                      $"{(c.Rotated ? " rot" : "")} r={c.Residual:F2}");
                }
                File.WriteAllText(Path.Combine(dir, $"scan-{stamp}.txt"), sb.ToString());
            }
        }
        catch { /* diagnostics must never break a scan */ }
    }

    private void Present(ScanResult? result, Point cursor)
    {
        if (DumpScans) DumpScan(result, cursor);

        if (result?.Best is null)
        {
            // A miss is the normal case in hover mode - the cursor is simply not
            // over an inventory - so it is silent either way.
            _popup.HidePopup();
            result?.Thumbnail?.Dispose();
            return;
        }

        _popup.ShowFor(result, _prices, cursor,
                       _cfg.ScanHoverMode ? _cfg.ScanPopupMs : 0);
        result.Thumbnail?.Dispose();
    }

    private async Task EnsurePricesAsync()
    {
        if (DateTime.UtcNow - _lastRefresh < TimeSpan.FromMinutes(5)) return;
        if (!_prices.IsStale(TimeSpan.FromMinutes(20))) return;
        _lastRefresh = DateTime.UtcNow;
        await _prices.RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Is Tarkov the window in front? Asked of the window manager, not of the game
    /// - no process is opened and no memory is read.
    /// </summary>
    private static bool TarkovIsForeground()
    {
        try
        {
            var hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return false;
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName.StartsWith("EscapeFromTarkov", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _poll.Stop();
        _poll.Dispose();
        _popup.Dispose();
        _scan.Dispose();
    }
}
