using System.Runtime;

namespace TarkovOverlay;

public sealed class OverlayForm : Form
{
    private const int BorderGrip = 5;

    private readonly Config _cfg;
    private readonly MapCatalog _catalog;
    private readonly HotkeyManager _hotkeys;
    private readonly NotifyIcon _tray;
    private readonly SecondMonitorMode _secondMonitor;
    private Button? _monitorButton;
    private ToolStripMenuItem? _monitorMenuItem;

    private readonly Panel _titleBar = new();
    private readonly Panel _content = new();
    private readonly Panel _mapsPanel = new();
    private readonly FlowLayoutPanel _mapStrip = new();
    private readonly MapView _mapView;
    private readonly WebTab _tabDev;
    private readonly WebTab _tabWiki;
    private readonly WebTab _tabAmmo;
    private readonly Dictionary<string, WebTab> _webTabs;

    private readonly Dictionary<string, Button> _tabButtons = new();
    private readonly Dictionary<string, Button> _mapButtons = new();

    // Notes toolbar under the map strip.
    private readonly FlowLayoutPanel _noteBar = new();
    private readonly Dictionary<NoteTool, Button> _toolButtons = new();
    private readonly List<(Color Color, Button Button)> _swatches = new();
    private readonly List<(float Width, Button Button)> _widthButtons = new();
    private Button? _undoButton, _redoButton, _visibilityButton, _clearButton;

    private static readonly Color[] NoteColors =
    {
        Color.FromArgb(232, 74, 60),     // red
        Color.FromArgb(247, 144, 44),    // orange
        Color.FromArgb(246, 214, 66),    // yellow
        Color.FromArgb(96, 204, 102),    // green
        Color.FromArgb(66, 190, 235),    // cyan
        Color.FromArgb(242, 242, 242),   // white
    };

    private string _activeTab = "maps";
    private IntPtr _prevForeground;
    private bool _allowVisible;
    private readonly ItemScanner _scanner;

    public OverlayForm(Config cfg, bool showOnStart)
    {
        _cfg = cfg;
        _catalog = MapCatalog.Load();

        // --- window ---------------------------------------------------------
        Text = "TLO — Tarkov Light Overlay";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(52, 56, 64);      // shows through as the 5px frame
        Padding = new Padding(BorderGrip);
        MinimumSize = new Size(560, 360);
        KeyPreview = true;
        DoubleBuffered = true;
        Icon = AppIcon.Load();
        Opacity = Math.Clamp(_cfg.Opacity, 0.3, 1.0);
        RestoreBounds_();
        _secondMonitor = new SecondMonitorMode(this);

        _mapView = new MapView(_cfg.TileCacheSize) { Dock = DockStyle.Fill };
        _mapView.ViewChanged += SaveMapView;
        _mapView.PenColor = ParseNoteColor(_cfg.NotePenColor);
        _mapView.PenWidth = _cfg.NotePenWidth;
        _mapView.NotesVisible = _cfg.NotesVisible;
        _mapView.NotesChanged += RefreshNoteBar;

        _scanner = new ItemScanner(_cfg, this);
        _scanner.OpenItemRequested += OpenItemOnTarkovDev;

        _tabDev = new WebTab(_cfg, _cfg.TarkovDevUrl);
        _tabWiki = new WebTab(_cfg, _cfg.WikiUrl);
        _tabAmmo = new WebTab(_cfg, _cfg.AmmoUrl);
        _webTabs = new Dictionary<string, WebTab>
        {
            ["dev"] = _tabDev, ["wiki"] = _tabWiki, ["ammo"] = _tabAmmo,
        };
        foreach (var t in _webTabs.Values) t.EscapePressed += HideOverlay;

        BuildTitleBar();
        BuildMapsPanel();

        _content.Dock = DockStyle.Fill;
        _content.BackColor = Theme.Background;
        foreach (var t in _webTabs.Values) _content.Controls.Add(t);
        _content.Controls.Add(_mapsPanel);

        Controls.Add(_content);
        Controls.Add(_titleBar);

        // Forces handle creation so global hotkeys can bind while still hidden.
        _hotkeys = new HotkeyManager(Handle);
        _hotkeys.Rebind(_cfg);

        _tray = BuildTray();

        SelectTab(_cfg.LastTab, focus: false);
        _scanner.ApplySettings();
        if (showOnStart) ShowOverlay(null);
        else WarnIfHotkeysFailed();
    }

    // ---- construction ------------------------------------------------------

    private void BuildTitleBar()
    {
        _titleBar.Dock = DockStyle.Top;
        _titleBar.Height = 34;
        _titleBar.BackColor = Theme.Chrome;

        // Drag the window by its bar, using the same code path Windows uses for
        // a real title bar (so snapping and monitor edges behave normally).
        _titleBar.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || _secondMonitor.IsActive) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, new IntPtr(Native.HTCAPTION), IntPtr.Zero);
        };

        var brand = new Panel { Dock = DockStyle.Left, Width = 40, AccessibleName = "TLO" };
        brand.Paint += (_, e) =>
        {
            int size = Math.Min(24 * DeviceDpi / 96, Math.Min(brand.Width, brand.Height) - 8);
            e.Graphics.DrawIcon(AppIcon.Load(), new Rectangle((brand.Width - size) / 2,
                (brand.Height - size) / 2, size, size));
        };
        brand.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || _secondMonitor.IsActive) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, new IntPtr(Native.HTCAPTION), IntPtr.Zero);
        };
        new ToolTip().SetToolTip(brand, "TLO — Tarkov Light Overlay");
        _titleBar.Controls.Add(brand);

        foreach (var (key, label) in new[]
                 { ("dev", "Tarkov.dev"), ("wiki", "Wiki"), ("ammo", "Ammo"), ("maps", "Maps") })
        {
            var b = Theme.FlatButton(label, TextRenderer.MeasureText(label, Theme.TabFont).Width + 28, 34);
            b.Font = Theme.TabFont;
            b.Dock = DockStyle.Left;
            var captured = key;
            b.Click += (_, _) => SelectTab(captured, focus: true);
            _titleBar.Controls.Add(b);
            b.BringToFront();
            _tabButtons[key] = b;
        }

        var close = Theme.FlatButton("✕", 40, 34);
        close.Dock = DockStyle.Right;
        close.FlatAppearance.MouseOverBackColor = Theme.Danger;
        close.Click += (_, _) => HideOverlay();
        new ToolTip().SetToolTip(close, "Hide overlay (Esc)");

        var settings = Theme.FlatButton("⚙", 40, 34);
        settings.Dock = DockStyle.Right;
        settings.Click += (_, _) => OpenSettings();
        new ToolTip().SetToolTip(settings, "Settings");

        // Dock.Right stacks in reverse add order, so add settings first to
        // leave the close button hard against the edge.
        _titleBar.Controls.Add(settings);
        _titleBar.Controls.Add(close);

        _monitorButton = Theme.FlatButton("Monitor", 80, 34);
        _monitorButton.Dock = DockStyle.Right;
        _monitorButton.Click += (_, _) => ToggleSecondMonitor();
        new ToolTip().SetToolTip(_monitorButton, "Second monitor fullscreen / restore (shortcut in Settings)");
        _titleBar.Controls.Add(_monitorButton);
    }

    private void BuildMapsPanel()
    {
        _mapsPanel.Dock = DockStyle.Fill;
        _mapsPanel.BackColor = Theme.Background;

        _mapStrip.Dock = DockStyle.Top;
        _mapStrip.MinimumSize = new Size(0, 32);
        _mapStrip.BackColor = Theme.Chrome;
        _mapStrip.Padding = new Padding(4, 3, 4, 3);
        // Nine maps are ~800px of buttons, wider than the 560px minimum window.
        // Wrapping to a second row keeps every map reachable at any width.
        _mapStrip.WrapContents = true;
        _mapStrip.AutoScroll = false;
        _mapStrip.AutoSize = true;
        _mapStrip.AutoSizeMode = AutoSizeMode.GrowAndShrink;

        foreach (var map in _catalog.Maps)
        {
            // AutoSize rather than a pre-measured width: MeasureText runs at 96 DPI,
            // but the button paints at the DPI of whichever monitor the overlay is
            // on, so fixed widths clip longer names ("Interchange", "Lighthouse")
            // on a scaled display.
            var b = Theme.FlatButton(map.Name, 0);
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(11, 0, 11, 0);
            b.MinimumSize = new Size(0, 26);
            b.Margin = new Padding(0, 0, 4, 0);
            var captured = map;
            b.Click += (_, _) => SelectMap(captured.Id);
            _mapStrip.Controls.Add(b);
            _mapButtons[map.Id] = b;
        }

        if (_catalog.Maps.Count > 0)
        {
            var fit = Theme.FlatButton("Fit", 46);
            fit.Margin = new Padding(12, 0, 0, 0);
            fit.Click += (_, _) => _mapView.FitToWindow();
            new ToolTip().SetToolTip(fit, "Fit map to window (0)");
            _mapStrip.Controls.Add(fit);

            BuildNoteBar();
        }

        // Top-docked controls stack in reverse add order: the map strip goes in
        // last so it sits above the notes bar, and the view fills what is left.
        _mapsPanel.Controls.Add(_mapView);
        _mapsPanel.Controls.Add(_noteBar);
        _mapsPanel.Controls.Add(_mapStrip);
    }

    // ---- notes toolbar -----------------------------------------------------

    private void BuildNoteBar()
    {
        _noteBar.Dock = DockStyle.Top;
        _noteBar.MinimumSize = new Size(0, 30);
        _noteBar.BackColor = Theme.Background;
        _noteBar.Padding = new Padding(4, 2, 4, 2);
        _noteBar.WrapContents = true;
        _noteBar.AutoScroll = false;
        _noteBar.AutoSize = true;
        _noteBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var tips = new ToolTip();

        foreach (var (tool, label, tip) in new[]
                 {
                     (NoteTool.Pan, "Pan", "Move around the map (V)"),
                     (NoteTool.Draw, "Draw", "Draw a route or mark an area (D)"),
                     (NoteTool.Text, "Text", "Place a text note (T)"),
                     (NoteTool.Erase, "Erase", "Click a drawing or note to delete it (E)"),
                 })
        {
            var b = StripButton(label);
            var captured = tool;
            b.Click += (_, _) => _mapView.Tool = captured;
            tips.SetToolTip(b, tip);
            _noteBar.Controls.Add(b);
            _toolButtons[tool] = b;
        }

        _noteBar.Controls.Add(Separator());

        foreach (var color in NoteColors)
        {
            var b = new Button
            {
                Width = 22,
                Height = 22,
                FlatStyle = FlatStyle.Flat,
                BackColor = color,
                Margin = new Padding(0, 2, 4, 0),
                TabStop = false,
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.BorderColor = Color.White;
            b.FlatAppearance.MouseOverBackColor = color;
            b.FlatAppearance.MouseDownBackColor = color;
            var captured = color;
            b.Click += (_, _) =>
            {
                _mapView.PenColor = captured;
                _cfg.NotePenColor = $"#{captured.R:X2}{captured.G:X2}{captured.B:X2}";
                RefreshNoteBar();
            };
            _noteBar.Controls.Add(b);
            _swatches.Add((color, b));
        }

        _noteBar.Controls.Add(Separator());

        foreach (var (width, label) in new[] { (2.5f, "Thin"), (4.5f, "Medium"), (8f, "Thick") })
        {
            var b = StripButton(label);
            var captured = width;
            b.Click += (_, _) =>
            {
                _mapView.PenWidth = captured;
                _cfg.NotePenWidth = captured;
                RefreshNoteBar();
            };
            tips.SetToolTip(b, "Pen width for new drawings");
            _noteBar.Controls.Add(b);
            _widthButtons.Add((width, b));
        }

        _noteBar.Controls.Add(Separator());

        _undoButton = StripButton("Undo");
        _undoButton.Click += (_, _) => _mapView.Undo();
        tips.SetToolTip(_undoButton, "Undo (Ctrl+Z)");
        _noteBar.Controls.Add(_undoButton);

        _redoButton = StripButton("Redo");
        _redoButton.Click += (_, _) => _mapView.Redo();
        tips.SetToolTip(_redoButton, "Redo (Ctrl+Y)");
        _noteBar.Controls.Add(_redoButton);

        _noteBar.Controls.Add(Separator());

        _visibilityButton = StripButton("Hide notes");
        _visibilityButton.Click += (_, _) =>
        {
            _mapView.NotesVisible = !_mapView.NotesVisible;
            _cfg.NotesVisible = _mapView.NotesVisible;
        };
        tips.SetToolTip(_visibilityButton, "Show or hide every note on the map (H)");
        _noteBar.Controls.Add(_visibilityButton);

        _clearButton = StripButton("Clear map");
        _clearButton.FlatAppearance.MouseOverBackColor = Theme.Danger;
        _clearButton.Click += (_, _) => ClearNotesConfirmed();
        tips.SetToolTip(_clearButton, "Delete every drawing and note on this map");
        _noteBar.Controls.Add(_clearButton);

        RefreshNoteBar();
    }

    /// <summary>An auto-sized flat button in the style of the map strip.</summary>
    private static Button StripButton(string text)
    {
        var b = Theme.FlatButton(text, 0);
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.Padding = new Padding(9, 0, 9, 0);
        b.MinimumSize = new Size(0, 26);
        b.Margin = new Padding(0, 0, 4, 0);
        return b;
    }

    private static Control Separator() => new Panel
    {
        Width = 1,
        Height = 18,
        BackColor = Theme.ChromeHover,
        Margin = new Padding(4, 4, 8, 0),
    };

    private void RefreshNoteBar()
    {
        if (_toolButtons.Count == 0) return;

        foreach (var (tool, b) in _toolButtons)
        {
            bool active = tool == _mapView.Tool;
            b.BackColor = active ? Theme.Accent : Theme.Chrome;
            b.ForeColor = active ? Color.Black : Theme.Text;
        }

        foreach (var (color, b) in _swatches)
            b.FlatAppearance.BorderSize = color.ToArgb() == _mapView.PenColor.ToArgb() ? 2 : 0;

        foreach (var (width, b) in _widthButtons)
        {
            bool active = Math.Abs(width - _mapView.PenWidth) < 0.01f;
            b.BackColor = active ? Theme.ChromeHover : Theme.Chrome;
            b.ForeColor = active ? Theme.Accent : Theme.Text;
        }

        if (_undoButton is not null) _undoButton.Enabled = _mapView.CanUndo;
        if (_redoButton is not null) _redoButton.Enabled = _mapView.CanRedo;
        if (_clearButton is not null) _clearButton.Enabled = _mapView.HasNotes;
        if (_visibilityButton is not null)
        {
            _visibilityButton.Text = _mapView.NotesVisible ? "Hide notes" : "Show notes";
            _visibilityButton.ForeColor = _mapView.NotesVisible ? Theme.Text : Theme.Accent;
        }
    }

    private void ClearNotesConfirmed()
    {
        if (!_mapView.HasNotes || _mapView.Map is null) return;
        var answer = MessageBox.Show(this,
            $"Delete every drawing and note on {_mapView.Map.Name}?\n\nYou can still undo this with Ctrl+Z until you switch maps.",
            "Clear map notes",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes) _mapView.ClearNotes();
        _mapView.Focus();
    }

    private static Color ParseNoteColor(string s)
    {
        try { return ColorTranslator.FromHtml(s); }
        catch { return NoteColors[0]; }
    }

    private NotifyIcon BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show overlay", null, (_, _) => ShowOverlay(null));
        _monitorMenuItem = (ToolStripMenuItem)menu.Items.Add("Second monitor fullscreen", null, (_, _) => ToggleSecondMonitor());
        menu.Items.Add("Settings...", null, (_, _) => { ShowOverlay(null); OpenSettings(); });
        menu.Items.Add("Reset window size", null, (_, _) => { ShowOverlay(null); ResetWindow(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { SaveAll(); Application.Exit(); });

        var tray = new NotifyIcon
        {
            Icon = Icon,
            Visible = true,
            Text = $"TLO  —  {_cfg.ToggleOverlay}",
            ContextMenuStrip = menu,
        };
        tray.DoubleClick += (_, _) => ToggleOverlay();
        return tray;
    }

    // ---- tabs / maps -------------------------------------------------------

    private void SelectTab(string key, bool focus)
    {
        if (key != "maps" && !_webTabs.ContainsKey(key)) key = "maps";
        _activeTab = key;
        _cfg.LastTab = key;

        foreach (var (k, t) in _webTabs) t.Visible = k == key;
        _mapsPanel.Visible = key == "maps";

        foreach (var (k, b) in _tabButtons)
        {
            bool active = k == key;
            b.BackColor = active ? Theme.Background : Theme.Chrome;
            b.ForeColor = active ? Theme.Accent : Theme.Muted;
            b.Font = active ? new Font(Theme.TabFont, FontStyle.Bold) : Theme.TabFont;
        }

        if (key == "maps")
        {
            EnsureMapLoaded();
            if (focus) _mapView.Focus();
        }
        else
        {
            var tab = _webTabs[key];
            tab.Resume();
            _ = tab.EnsureLoadedAsync();
            if (focus) tab.FocusContent();
        }

        // Deal with the tabs we just left. In low-RAM mode they go away entirely,
        // so only one browser renderer is ever alive; otherwise they are merely
        // frozen and keep their page state.
        foreach (var (k, t) in _webTabs)
            if (k != key) Retire(t);
    }

    private void Retire(WebTab tab)
    {
        if (_cfg.UnloadWebOnHide) tab.Unload();
        else _ = tab.SuspendAsync();
    }

    private void EnsureMapLoaded()
    {
        if (_catalog.Maps.Count == 0) return;
        if (_mapView.Map is not null) return;

        var id = _cfg.LastMapId;
        if (string.IsNullOrEmpty(id) || _catalog.Maps.All(m => m.Id != id))
            id = _catalog.Maps[0].Id;
        SelectMap(id);
    }

    private void SelectMap(string id)
    {
        var map = _catalog.Maps.FirstOrDefault(m => m.Id == id);
        if (map is null) return;

        if (_mapView.Map is { } previous)
            _cfg.MapViews[previous.Id] = _mapView.CurrentView();

        _cfg.LastMapId = id;
        _cfg.MapViews.TryGetValue(id, out var saved);
        _mapView.ShowMap(map, saved);

        foreach (var (k, b) in _mapButtons)
        {
            bool active = k == id;
            b.BackColor = active ? Theme.Accent : Theme.Chrome;
            b.ForeColor = active ? Color.Black : Theme.Text;
        }

        // Persist the choice right away rather than waiting for the next hide,
        // so it survives the app being killed outright.
        _cfg.Save();
    }

    private void SaveMapView()
    {
        if (_mapView.Map is { } m) _cfg.MapViews[m.Id] = _mapView.CurrentView();
    }

    // ---- show / hide -------------------------------------------------------

    protected override void SetVisibleCore(bool value)
    {
        if (!_allowVisible)
        {
            value = false;
            if (!IsHandleCreated) CreateHandle();
        }
        base.SetVisibleCore(value);
    }

    public void ToggleOverlay()
    {
        if (Visible) HideOverlay();
        else ShowOverlay(null);
    }

    public void ToggleSecondMonitor()
    {
        if (!_secondMonitor.Toggle())
        {
            _tray.ShowBalloonTip(3500, "Second monitor unavailable",
                "Connect another display and extend your desktop to use this shortcut.", ToolTipIcon.Info);
            return;
        }
        UpdateMonitorControls();
        ShowOverlay(null);
        SaveAll();
    }

    private void UpdateMonitorControls()
    {
        if (_monitorButton is not null) _monitorButton.Text = _secondMonitor.IsActive ? "Restore" : "Monitor";
        if (_monitorMenuItem is not null) _monitorMenuItem.Text = _secondMonitor.IsActive
            ? "Restore previous window" : "Second monitor fullscreen";
    }

    public void ShowOverlay(string? tab)
    {
        if (!Visible)
        {
            // Remember what had focus so the game gets it back on hide.
            var fg = Native.GetForegroundWindow();
            if (fg != Handle) _prevForeground = fg;
        }

        _allowVisible = true;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;

        // Monitors may have been unplugged, rearranged or rescaled while we were
        // hidden, so never trust the old rectangle blindly.
        _secondMonitor.Refresh();
        UpdateMonitorControls();
        if (!_secondMonitor.IsActive && WindowState == FormWindowState.Normal)
        {
            var fitted = FitToScreen(Bounds);
            if (fitted != Bounds) Bounds = fitted;
        }

        Show();
        TopMost = true;
        BringToFront();

        // Handling a registered hotkey grants us the right to take foreground,
        // which is exactly why no injection into the game is needed.
        Native.SetForegroundWindow(Handle);
        Activate();

        SelectTab(tab ?? _activeTab, focus: true);
    }

    public void HideOverlay()
    {
        if (!Visible) return;

        // Come back in Pan mode: the first drag after the hotkey should move
        // the map, not scribble a line across it.
        _mapView.Tool = NoteTool.Pan;
        SaveAll();
        Hide();

        if (_cfg.UnloadWebOnHide)
        {
            foreach (var t in _webTabs.Values) t.Unload();
            _mapView.ReleaseMemory();
        }

        _scanner.ReleaseMemory();

        // Going idle for a long stretch: hand the memory back to the game.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        Native.TrimWorkingSet();

        if (_prevForeground != IntPtr.Zero && Native.IsWindow(_prevForeground))
            Native.SetForegroundWindow(_prevForeground);
    }

    private void OpenSettings()
    {
        using var dlg = new SettingsForm(_cfg);
        // The overlay is topmost, so an ordinary dialog would open behind it.
        dlg.TopMost = true;
        var original = Opacity;
        dlg.OpacityPreview += v => Opacity = v;

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            Opacity = Math.Clamp(_cfg.Opacity, 0.3, 1.0);
            _mapView.SetCacheSize(_cfg.TileCacheSize);
            _hotkeys.Rebind(_cfg);
            _scanner.ApplySettings();
            _tray.Text = $"TLO  —  {_cfg.ToggleOverlay}";
            WarnIfHotkeysFailed();
        }
        else
        {
            Opacity = original;
        }
    }

    private void WarnIfHotkeysFailed()
    {
        if (_hotkeys.Failures.Count == 0) return;
        _tray.ShowBalloonTip(6000, "Shortcut unavailable",
            "Windows refused: " + string.Join(", ", _hotkeys.Failures) +
            "\nAnother app already owns it. Pick a different combination.",
            ToolTipIcon.Warning);
    }

    // ---- window plumbing ---------------------------------------------------

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW;   // keeps it out of Alt+Tab
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            OnHotkey(m.WParam.ToInt32());
            return;
        }

        base.WndProc(ref m);

        // Windows has just rescaled us for a monitor with a different DPI. It
        // proposes a new size, and that size can exceed the monitor we landed on.
        if ((m.Msg == Native.WM_DPICHANGED || m.Msg == 0x007E /* WM_DISPLAYCHANGE */) &&
            _secondMonitor is not null && !_secondMonitor.IsAdjusting)
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                _secondMonitor.Refresh();
                UpdateMonitorControls();
                if (!_secondMonitor.IsActive && WindowState == FormWindowState.Normal)
                {
                    var fitted = FitToScreen(Bounds);
                    if (fitted != Bounds) Bounds = fitted;
                }
            });
        }

        // The form is borderless, so resize edges are synthesised from the
        // 5px padding frame left exposed around the docked children.
        if (m.Msg == Native.WM_NCHITTEST && m.Result == new IntPtr(Native.HTCLIENT) &&
            _secondMonitor?.IsActive != true)
        {
            var p = PointToClient(new Point(
                (short)((long)m.LParam & 0xFFFF),
                (short)(((long)m.LParam >> 16) & 0xFFFF)));

            bool left = p.X <= BorderGrip;
            bool right = p.X >= ClientSize.Width - BorderGrip;
            bool top = p.Y <= BorderGrip;
            bool bottom = p.Y >= ClientSize.Height - BorderGrip;

            int hit =
                top && left ? Native.HTTOPLEFT :
                top && right ? Native.HTTOPRIGHT :
                bottom && left ? Native.HTBOTTOMLEFT :
                bottom && right ? Native.HTBOTTOMRIGHT :
                left ? Native.HTLEFT :
                right ? Native.HTRIGHT :
                top ? Native.HTTOP :
                bottom ? Native.HTBOTTOM :
                Native.HTCLIENT;

            m.Result = new IntPtr(hit);
        }
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HotkeyManager.IdToggle: ToggleOverlay(); break;
            case HotkeyManager.IdSecondMonitor: ToggleSecondMonitor(); break;
            case HotkeyManager.IdTarkovDev: ShowTabHotkey("dev"); break;
            case HotkeyManager.IdWiki: ShowTabHotkey("wiki"); break;
            case HotkeyManager.IdAmmo: ShowTabHotkey("ammo"); break;
            case HotkeyManager.IdMaps: ShowTabHotkey("maps"); break;
            case HotkeyManager.IdScanItem: _scanner.HotkeyScan(); break;
        }
    }

    /// <summary>A tab hotkey opens that tab, or hides if it is already showing.</summary>
    private void ShowTabHotkey(string tab)
    {
        if (Visible && _activeTab == tab) HideOverlay();
        else ShowOverlay(tab);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            // A note being typed takes the first Esc; the overlay takes the next.
            if (_activeTab == "maps" && _mapView.CancelEditing()) { e.Handled = true; return; }
            HideOverlay();
            e.Handled = true;
        }
        else if (e.Control && e.KeyCode == Keys.L && _webTabs.TryGetValue(_activeTab, out var web))
        {
            web.FocusAddress();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Force a window rectangle to fit on a monitor that can actually display it.
    ///
    /// Fitting matters, not merely overlapping. On a mixed-DPI desktop Windows
    /// rescales this window when it crosses between monitors, so dragging from a
    /// 125% display to a 100% one leaves it larger than the screen it landed on.
    /// The window is borderless and its resize edges are synthesised from a 5px
    /// frame, so once those edges are off-screen there is no way to grab them:
    /// it saves its oversized bounds and comes back stuck every launch.
    /// </summary>
    private static Rectangle FitToScreen(Rectangle r)
    {
        var area = Screen.FromRectangle(r).WorkingArea;

        int w = Math.Min(r.Width, area.Width);
        int h = Math.Min(r.Height, area.Height);
        // Honour the minimum size, unless the monitor itself is smaller.
        w = Math.Max(w, Math.Min(560, area.Width));
        h = Math.Max(h, Math.Min(360, area.Height));

        int x = Math.Clamp(r.X, area.Left, Math.Max(area.Left, area.Right - w));
        int y = Math.Clamp(r.Y, area.Top, Math.Max(area.Top, area.Bottom - h));
        return new Rectangle(x, y, w, h);
    }

    /// <summary>Default size and position: centred on the monitor holding the cursor.</summary>
    private static Rectangle DefaultBounds()
    {
        Native.GetCursorPos(out var p);
        var area = Screen.FromPoint(new Point(p.X, p.Y)).WorkingArea;
        int w = Math.Min(1280, area.Width);
        int h = Math.Min(820, area.Height);
        return new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    private void RestoreBounds_()
    {
        bool saved = _cfg.WindowWidth > 0 && _cfg.WindowHeight > 0 &&
                     (_cfg.WindowX != -1 || _cfg.WindowY != -1);

        Bounds = saved
            ? FitToScreen(new Rectangle(_cfg.WindowX, _cfg.WindowY, _cfg.WindowWidth, _cfg.WindowHeight))
            : DefaultBounds();
    }

    /// <summary>Put the window back to a sane size on the monitor under the cursor.</summary>
    public void ResetWindow()
    {
        _secondMonitor.Restore();
        UpdateMonitorControls();
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
        Bounds = DefaultBounds();
        SaveAll();
    }

    private void SaveAll()
    {
        if (WindowState == FormWindowState.Normal && Width > 100)
        {
            // Store what actually fits, so a bad rectangle cannot outlive the session.
            var fitted = FitToScreen(_secondMonitor.SavedBounds ?? Bounds);
            _cfg.WindowX = fitted.X;
            _cfg.WindowY = fitted.Y;
            _cfg.WindowWidth = fitted.Width;
            _cfg.WindowHeight = fitted.Height;
        }
        SaveMapView();
        _mapView.CommitEditing();
        _mapView.FlushNotes();
        _cfg.Save();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The X button hides; only the tray Exit really quits.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideOverlay();
            return;
        }
        SaveAll();
        base.OnFormClosing(e);
    }

    /// <summary>
    /// Bring up an item's tarkov.dev page. The scanner gives us the item id, and
    /// tarkov.dev resolves ids directly, so no search step is needed.
    /// </summary>
    private void OpenItemOnTarkovDev(string itemId)
    {
        ShowOverlay("dev");
        _tabDev.Navigate($"https://tarkov.dev/item/{itemId}");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scanner.Dispose();
            _hotkeys.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
