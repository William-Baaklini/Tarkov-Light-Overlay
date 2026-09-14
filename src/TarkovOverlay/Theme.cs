namespace TarkovOverlay;

/// <summary>Flat dark palette, sized to read at a glance over a dark game.</summary>
internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(18, 19, 22);
    public static readonly Color Chrome = Color.FromArgb(28, 30, 35);
    public static readonly Color ChromeHover = Color.FromArgb(44, 47, 54);
    public static readonly Color Input = Color.FromArgb(38, 41, 47);
    public static readonly Color Text = Color.FromArgb(226, 229, 234);
    public static readonly Color Muted = Color.FromArgb(140, 148, 160);
    public static readonly Color Accent = Color.FromArgb(197, 160, 89);   // Tarkov-ish tan
    public static readonly Color Danger = Color.FromArgb(196, 84, 74);

    public static readonly Font UiFont = new("Segoe UI", 9f);
    public static readonly Font UiFontBold = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font TabFont = new("Segoe UI", 9.5f);

    public static Button FlatButton(string text, int width, int height = 0)
    {
        var b = new Button
        {
            Text = text,
            Width = width,
            Height = height > 0 ? height : 26,
            FlatStyle = FlatStyle.Flat,
            BackColor = Chrome,
            ForeColor = Text,
            Font = UiFont,
            TabStop = false,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = ChromeHover;
        b.FlatAppearance.MouseDownBackColor = ChromeHover;
        return b;
    }

    public static void StyleDialog(Form f)
    {
        f.BackColor = Background;
        f.ForeColor = Text;
        f.Font = UiFont;
    }
}
