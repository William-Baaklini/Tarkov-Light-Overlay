using System.Drawing.Drawing2D;

namespace TarkovOverlay;

/// <summary>
/// The little card that appears next to the cursor with what the item is worth.
///
/// It must never take focus. Tarkov is the foreground window while this is up,
/// and stealing activation would eat the player's next keypress or drop them out
/// of the game, so the window is created WS_EX_NOACTIVATE and refuses activation
/// outright.
/// </summary>
public sealed class ScanPopup : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;

    private const int Pad = 10;
    private const int ThumbMax = 64;

    private readonly System.Windows.Forms.Timer _hide = new();

    private Bitmap? _thumb;
    private string _title = "";
    private readonly List<(string Label, string Value, Color Colour)> _rows = new();
    private string _footer = "";

    public ScanPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Background;
        DoubleBuffered = true;
        Font = Theme.UiFont;

        _hide.Tick += (_, _) => HidePopup();
    }

    /// <summary>Never activate: the click-through-free version of staying out of the way.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    /// <summary>Item currently on show, so a second hotkey press can act on it.</summary>
    public string? CurrentItemId { get; private set; }

    public void HidePopup()
    {
        _hide.Stop();
        if (Visible) Hide();
        _thumb?.Dispose();
        _thumb = null;
        CurrentItemId = null;
    }

    /// <summary>Put the card next to the slot we just read.</summary>
    public void ShowFor(ScanResult scan, PriceBook prices, Point cursor, int durationMs)
    {
        var tied = scan.TiedUpTo(4);
        if (tied.Count == 0) { HidePopup(); return; }

        _thumb?.Dispose();
        _thumb = scan.Thumbnail is null ? null : Scale(scan.Thumbnail, ThumbMax);
        _rows.Clear();
        CurrentItemId = tied[0].ItemId;

        var top = prices.Get(tied[0].ItemId);
        int cells = Math.Max(1, tied[0].GridW * tied[0].GridH);

        if (top is null)
        {
            // Matched the picture but have no price row for it: usually the price
            // table has not been fetched yet.
            _title = prices.Count == 0 ? "Prices not loaded yet" : "Unknown item";
            _footer = prices.Count == 0 ? "tarkov.dev has not answered yet" : tied[0].ItemId;
        }
        else
        {
            _title = top.Name;

            int flea = top.FleaLow > 0 ? top.FleaLow : top.Flea;
            if (flea > 0)
            {
                _rows.Add(("Flea", Money(flea), Theme.Text));
                _rows.Add(("Per slot", Money(flea / cells), Theme.Muted));
            }
            else
            {
                _rows.Add(("Flea", "not tradeable", Theme.Muted));
            }

            if (top.TraderPrice > 0)
                _rows.Add((top.TraderName, Money(top.TraderPrice), Theme.Muted));

            if (Math.Abs(top.Change48h) >= 0.05)
                _rows.Add(("48h", $"{top.Change48h:+0.0;-0.0}%",
                           top.Change48h >= 0 ? Color.FromArgb(120, 190, 120) : Theme.Danger));

            int extra = scan.TiedCount - 1;
            _footer = extra > 0
                ? $"or {extra} other item{(extra == 1 ? "" : "s")} with this icon"
                : "";
        }

        Layout_();
        Place(scan.SlotRect, cursor);

        if (!Visible) Show();
        Invalidate();

        _hide.Stop();
        if (durationMs > 0)
        {
            _hide.Interval = durationMs;
            _hide.Start();
        }
    }

    private static Bitmap Scale(Bitmap src, int max)
    {
        double k = Math.Min(1.0, Math.Min(max / (double)src.Width, max / (double)src.Height));
        int w = Math.Max(1, (int)(src.Width * k)), h = Math.Max(1, (int)(src.Height * k));
        var dst = new Bitmap(w, h);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, w, h);
        return dst;
    }

    private void Layout_()
    {
        int textLeft = Pad + (_thumb?.Width ?? 0) + (_thumb is null ? 0 : Pad);

        int w = TextRenderer.MeasureText(_title, Theme.UiFontBold).Width;
        foreach (var (label, value, _) in _rows)
            w = Math.Max(w, TextRenderer.MeasureText(label, Theme.UiFont).Width + 14 +
                            TextRenderer.MeasureText(value, Theme.UiFontBold).Width);
        if (_footer.Length > 0)
            w = Math.Max(w, TextRenderer.MeasureText(_footer, Theme.UiFont).Width);

        int textH = 18 + _rows.Count * 17 + (_footer.Length > 0 ? 17 : 0);
        int h = Math.Max(textH, _thumb?.Height ?? 0) + Pad * 2;

        Size = new Size(textLeft + w + Pad, h);
    }

    private void Place(Rectangle slot, Point cursor)
    {
        var screen = Screen.FromPoint(cursor).WorkingArea;
        // Prefer to the right of the slot so the item itself stays visible.
        int x = slot.Right + 8;
        int y = slot.Top;
        if (x + Width > screen.Right) x = slot.Left - Width - 8;
        if (x < screen.Left) x = Math.Min(cursor.X + 16, screen.Right - Width);
        y = Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - Height));
        Location = new Point(x, y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Chrome);

        using (var border = new Pen(Theme.Accent))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        int x = Pad;
        if (_thumb is not null)
        {
            g.DrawImage(_thumb, x, Pad);
            x += _thumb.Width + Pad;
        }

        int y = Pad;
        TextRenderer.DrawText(g, _title, Theme.UiFontBold, new Point(x, y), Theme.Text);
        y += 20;

        foreach (var (label, value, colour) in _rows)
        {
            TextRenderer.DrawText(g, label, Theme.UiFont, new Point(x, y), Theme.Muted);
            int vw = TextRenderer.MeasureText(value, Theme.UiFontBold).Width;
            TextRenderer.DrawText(g, value, Theme.UiFontBold, new Point(Width - Pad - vw, y), colour);
            y += 17;
        }

        if (_footer.Length > 0)
            TextRenderer.DrawText(g, _footer, Theme.UiFont, new Point(x, y), Theme.Muted);
    }

    /// <summary>Roubles, grouped, e.g. 1,234,567.</summary>
    private static string Money(int v) => "₽ " + v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hide.Dispose();
            _thumb?.Dispose();
        }
        base.Dispose(disposing);
    }
}
