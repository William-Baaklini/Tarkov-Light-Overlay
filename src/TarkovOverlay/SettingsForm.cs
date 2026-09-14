namespace TarkovOverlay;

public sealed class SettingsForm : Form
{
    private readonly Config _cfg;

    private readonly HotkeyBox _toggle = new();
    private readonly HotkeyBox _secondMonitor = new();
    private readonly HotkeyBox _dev = new();
    private readonly HotkeyBox _wiki = new();
    private readonly HotkeyBox _ammo = new();
    private readonly HotkeyBox _maps = new();
    private readonly TrackBar _opacity = new();
    private readonly Label _opacityValue = new();
    private readonly CheckBox _blockAds = new();
    private readonly CheckBox _unload = new();
    private readonly CheckBox _strictAds = new();
    private readonly CheckBox _lowGpu = new();
    private readonly NumericUpDown _tileCache = new();
    private readonly Label _hint = new();
    private readonly CheckBox _scanEnabled = new();
    private readonly HotkeyBox _scanKey = new();
    private readonly CheckBox _scanHover = new();
    private readonly CheckBox _scanOnlyTarkov = new();
    private readonly NumericUpDown _scanPopupMs = new();

    public SettingsForm(Config cfg)
    {
        _cfg = cfg;

        Text = "TLO settings";
        Icon = AppIcon.Load();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(430, 700);
        AutoScroll = true;
        Theme.StyleDialog(this);

        int y = 14;

        AddSection("Shortcuts", ref y);
        AddHotkey("Show / hide overlay", _toggle, cfg.ToggleOverlay, ref y);
        AddHotkey("Second monitor / restore", _secondMonitor, cfg.ToggleSecondMonitor, ref y);
        AddHotkey("Open on Tarkov.dev", _dev, cfg.OpenTarkovDev, ref y);
        AddHotkey("Open on Wiki", _wiki, cfg.OpenWiki, ref y);
        AddHotkey("Open on Ammo", _ammo, cfg.OpenAmmo, ref y);
        AddHotkey("Open on Maps", _maps, cfg.OpenMaps, ref y);

        _hint.SetBounds(16, y, 398, 32);
        _hint.ForeColor = Theme.Muted;
        _hint.Text = "Click a field, then press the combination you want.\r\n"
                   + "Backspace clears · Esc cancels · Ctrl/Alt/Shift/Win, or an F-key.";
        Controls.Add(_hint);
        y += 42;

        AddSection("Appearance", ref y);

        var opLabel = new Label { Text = "Opacity", ForeColor = Theme.Text };
        opLabel.SetBounds(16, y + 4, 130, 20);
        Controls.Add(opLabel);

        _opacity.SetBounds(150, y, 210, 30);
        _opacity.Minimum = 30;
        _opacity.Maximum = 100;
        _opacity.TickFrequency = 10;
        _opacity.Value = Math.Clamp((int)Math.Round(cfg.Opacity * 100), 30, 100);
        _opacity.ValueChanged += (_, _) =>
        {
            _opacityValue.Text = _opacity.Value + "%";
            OpacityPreview?.Invoke(_opacity.Value / 100.0);
        };
        Controls.Add(_opacity);

        _opacityValue.SetBounds(366, y + 4, 48, 20);
        _opacityValue.ForeColor = Theme.Muted;
        _opacityValue.Text = _opacity.Value + "%";
        Controls.Add(_opacityValue);
        y += 40;

        AddSection("Item scanner", ref y);

        _scanEnabled.SetBounds(16, y, 400, 22);
        _scanEnabled.ForeColor = Theme.Text;
        _scanEnabled.Checked = cfg.ScanEnabled;
        _scanEnabled.Text = "Read the item under the cursor and price it";
        Controls.Add(_scanEnabled);
        y += 26;

        AddHotkey("Scan item", _scanKey, cfg.ScanItem, ref y);

        var scanHint = new Label
        {
            Text = "Press once for the price card, again to open it on tarkov.dev.",
            ForeColor = Theme.Muted,
        };
        scanHint.SetBounds(16, y, 398, 18);
        Controls.Add(scanHint);
        y += 24;

        _scanHover.SetBounds(16, y, 400, 22);
        _scanHover.ForeColor = Theme.Text;
        _scanHover.Checked = cfg.ScanHoverMode;
        _scanHover.Text = "Price on hover, without pressing anything";
        Controls.Add(_scanHover);
        y += 26;

        _scanOnlyTarkov.SetBounds(34, y, 390, 22);
        _scanOnlyTarkov.ForeColor = Theme.Muted;
        _scanOnlyTarkov.Checked = cfg.ScanOnlyInTarkov;
        _scanOnlyTarkov.Text = "...only while Tarkov is the active window";
        Controls.Add(_scanOnlyTarkov);
        y += 30;

        var popupLabel = new Label { Text = "Card stays up", ForeColor = Theme.Text };
        popupLabel.SetBounds(16, y + 3, 130, 20);
        Controls.Add(popupLabel);

        _scanPopupMs.SetBounds(150, y, 70, 24);
        _scanPopupMs.Minimum = 500;
        _scanPopupMs.Maximum = 10000;
        _scanPopupMs.Increment = 250;
        _scanPopupMs.Value = Math.Clamp(cfg.ScanPopupMs, 500, 10000);
        _scanPopupMs.BackColor = Theme.Input;
        _scanPopupMs.ForeColor = Theme.Text;
        _scanPopupMs.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_scanPopupMs);

        var popupHint = new Label { Text = "ms  (hover mode only)", ForeColor = Theme.Muted };
        popupHint.SetBounds(226, y + 3, 190, 20);
        Controls.Add(popupHint);
        y += 40;

        AddSection("Memory", ref y);

        _unload.SetBounds(16, y, 400, 22);
        _unload.ForeColor = Theme.Text;
        _unload.Checked = cfg.UnloadWebOnHide;
        _unload.Text = "Unload web pages when hidden (lowest RAM, reloads on reopen)";
        Controls.Add(_unload);
        y += 26;

        _blockAds.SetBounds(16, y, 400, 22);
        _blockAds.ForeColor = Theme.Text;
        _blockAds.Checked = cfg.BlockAds;
        _blockAds.Text = "Block ads and trackers (big saving on the wiki)";
        Controls.Add(_blockAds);
        y += 26;

        _strictAds.SetBounds(34, y, 390, 22);
        _strictAds.ForeColor = Theme.Muted;
        _strictAds.Checked = cfg.BlockAdsStrict;
        _strictAds.Text = "...strictly (more savings, but the wiki may not load)";
        Controls.Add(_strictAds);
        y += 26;

        _lowGpu.SetBounds(16, y, 400, 22);
        _lowGpu.ForeColor = Theme.Text;
        _lowGpu.Checked = cfg.LowGpuMode;
        _lowGpu.Text = "Low GPU mode — don't compete with the game (restart to apply)";
        Controls.Add(_lowGpu);
        y += 30;

        var cacheLabel = new Label { Text = "Map tile cache", ForeColor = Theme.Text };
        cacheLabel.SetBounds(16, y + 3, 130, 20);
        Controls.Add(cacheLabel);

        _tileCache.SetBounds(150, y, 70, 24);
        _tileCache.Minimum = 12;
        _tileCache.Maximum = 256;
        _tileCache.Increment = 4;
        _tileCache.Value = Math.Clamp(cfg.TileCacheSize, 12, 256);
        _tileCache.BackColor = Theme.Input;
        _tileCache.ForeColor = Theme.Text;
        _tileCache.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_tileCache);

        var cacheHint = new Label { Text = "tiles  (~0.8 MB each)", ForeColor = Theme.Muted };
        cacheHint.SetBounds(226, y + 3, 190, 20);
        Controls.Add(cacheHint);
        y += 40;

        var ok = Theme.FlatButton("Save", 90, 30);
        ok.SetBounds(232, y, 90, 30);
        ok.BackColor = Theme.Accent;
        ok.ForeColor = Color.Black;
        ok.Font = Theme.UiFontBold;
        ok.Click += (_, _) => { Commit(); DialogResult = DialogResult.OK; Close(); };
        Controls.Add(ok);

        var cancel = Theme.FlatButton("Cancel", 90, 30);
        cancel.SetBounds(328, y, 90, 30);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(cancel);

        AcceptButton = null;   // Enter must reach the hotkey boxes
        CancelButton = cancel;
        AutoScrollMinSize = new Size(0, y + 44);
        ClientSize = new Size(450, Math.Min(y + 44, Screen.FromPoint(Cursor.Position).WorkingArea.Height - 80));

        _toggle.Value = cfg.ToggleOverlay;
        _dev.Value = cfg.OpenTarkovDev;
        _wiki.Value = cfg.OpenWiki;
        _ammo.Value = cfg.OpenAmmo;
        _maps.Value = cfg.OpenMaps;
        _scanKey.Value = cfg.ScanItem;
    }

    /// <summary>Fires as the slider moves so the overlay updates live behind the dialog.</summary>
    public event Action<double>? OpacityPreview;

    private void AddSection(string title, ref int y)
    {
        var l = new Label
        {
            Text = title.ToUpperInvariant(),
            ForeColor = Theme.Accent,
            Font = Theme.UiFontBold,
        };
        l.SetBounds(16, y, 300, 18);
        Controls.Add(l);
        y += 24;
    }

    private void AddHotkey(string label, HotkeyBox box, Hotkey value, ref int y)
    {
        var l = new Label { Text = label, ForeColor = Theme.Text };
        l.SetBounds(16, y + 3, 170, 20);
        Controls.Add(l);

        box.SetBounds(190, y, 224, 24);
        box.Value = value;
        Controls.Add(box);
        y += 30;
    }

    private void Commit()
    {
        _cfg.ToggleOverlay = _toggle.Value;
        _cfg.ToggleSecondMonitor = _secondMonitor.Value;
        _cfg.OpenTarkovDev = _dev.Value;
        _cfg.OpenWiki = _wiki.Value;
        _cfg.OpenAmmo = _ammo.Value;
        _cfg.OpenMaps = _maps.Value;
        _cfg.Opacity = _opacity.Value / 100.0;
        _cfg.BlockAds = _blockAds.Checked;
        _cfg.BlockAdsStrict = _strictAds.Checked;
        _cfg.UnloadWebOnHide = _unload.Checked;
        _cfg.LowGpuMode = _lowGpu.Checked;
        _cfg.TileCacheSize = (int)_tileCache.Value;
        _cfg.ScanEnabled = _scanEnabled.Checked;
        _cfg.ScanItem = _scanKey.Value;
        _cfg.ScanHoverMode = _scanHover.Checked;
        _cfg.ScanOnlyInTarkov = _scanOnlyTarkov.Checked;
        _cfg.ScanPopupMs = (int)_scanPopupMs.Value;
        _cfg.Save();
    }
}
