namespace TarkovOverlay;

internal sealed record DisplayInfo(string Name, Rectangle Bounds, Rectangle WorkingArea, bool Primary);

internal interface IPlacementWindow
{
    Rectangle Bounds { get; set; }
    Rectangle RestoreBounds { get; }
    FormWindowState WindowState { get; set; }
    Padding Padding { get; set; }
    Size MinimumSize { get; set; }
}

internal sealed class FormPlacementWindow(Form form) : IPlacementWindow
{
    public Rectangle Bounds { get => form.Bounds; set => form.Bounds = value; }
    public Rectangle RestoreBounds => form.RestoreBounds;
    public FormWindowState WindowState { get => form.WindowState; set => form.WindowState = value; }
    public Padding Padding { get => form.Padding; set => form.Padding = value; }
    public Size MinimumSize { get => form.MinimumSize; set => form.MinimumSize = value; }
}

/// <summary>Keeps fullscreen placement separate from the user's normal window geometry.</summary>
internal sealed class SecondMonitorMode
{
    private readonly IPlacementWindow _window;
    private readonly Func<DisplayInfo[]> _displays;
    private Rectangle? _savedBounds;
    private FormWindowState _savedState;
    private Padding _savedPadding;
    private Size _savedMinimum;
    private string? _target;

    public bool IsActive => _savedBounds.HasValue;
    public bool IsAdjusting { get; private set; }
    public Rectangle? SavedBounds => _savedBounds;

    public SecondMonitorMode(Form window, Func<DisplayInfo[]>? displays = null)
        : this(new FormPlacementWindow(window), displays) { }

    internal SecondMonitorMode(IPlacementWindow window, Func<DisplayInfo[]>? displays = null)
    {
        _window = window;
        _displays = displays ?? (() => Screen.AllScreens.Select(s =>
            new DisplayInfo(s.DeviceName, s.Bounds, s.WorkingArea, s.Primary)).ToArray());
    }

    public bool Toggle()
    {
        if (IsActive) { Restore(); return true; }
        var displays = _displays();
        if (displays.Length < 2) return false;
        var current = Nearest(_window.Bounds, displays);
        var target = displays.Where(s => s.Name != current.Name)
            .OrderBy(s => s.Primary).ThenBy(s => s.Name, StringComparer.Ordinal).First();

        _savedBounds = _window.WindowState == FormWindowState.Normal
            ? _window.Bounds : _window.RestoreBounds;
        _savedState = _window.WindowState == FormWindowState.Minimized
            ? FormWindowState.Normal : _window.WindowState;
        _savedPadding = _window.Padding;
        _savedMinimum = _window.MinimumSize;
        _target = target.Name;
        ApplyFullscreen(target.Bounds);
        return true;
    }

    public void Refresh()
    {
        if (!IsActive || IsAdjusting) return;
        var target = _displays().FirstOrDefault(s => s.Name == _target);
        if (target is null) Restore();
        else ApplyFullscreen(target.Bounds);
    }

    public void Restore()
    {
        if (_savedBounds is not { } saved) return;
        IsAdjusting = true;
        try
        {
            _window.WindowState = FormWindowState.Normal;
            _window.MinimumSize = Size.Empty;
            // Moving across DPI boundaries can synchronously rescale the form.
            // Apply the physical rectangle again after that transition completes.
            var area = Nearest(saved, _displays()).WorkingArea;
            var bounds = Fit(saved, area);
            _window.Bounds = bounds;
            _window.Padding = _savedPadding;
            _window.MinimumSize = new Size(Math.Min(_savedMinimum.Width, area.Width),
                Math.Min(_savedMinimum.Height, area.Height));
            _window.Bounds = bounds;
            _window.WindowState = _savedState;
            _savedBounds = null;
            _target = null;
        }
        finally { IsAdjusting = false; }
    }

    private void ApplyFullscreen(Rectangle bounds)
    {
        IsAdjusting = true;
        try
        {
            _window.WindowState = FormWindowState.Normal;
            _window.MinimumSize = Size.Empty;
            _window.Padding = Padding.Empty;
            _window.Bounds = bounds;
            _window.Bounds = bounds;
        }
        finally { IsAdjusting = false; }
    }

    internal static DisplayInfo Nearest(Rectangle bounds, DisplayInfo[] displays) => displays
        .OrderByDescending(s => { var r = Rectangle.Intersect(bounds, s.Bounds); return (long)r.Width * r.Height; })
        .ThenBy(s => Distance(bounds, s.Bounds)).First();

    private static long Distance(Rectangle a, Rectangle b)
    {
        long dx = Math.Max(0, Math.Max(b.Left - a.Right, a.Left - b.Right));
        long dy = Math.Max(0, Math.Max(b.Top - a.Bottom, a.Top - b.Bottom));
        return dx * dx + dy * dy;
    }

    internal static Rectangle Fit(Rectangle bounds, Rectangle area)
    {
        int width = Math.Min(bounds.Width, area.Width), height = Math.Min(bounds.Height, area.Height);
        return new Rectangle(Math.Clamp(bounds.X, area.Left, area.Right - width),
            Math.Clamp(bounds.Y, area.Top, area.Bottom - height), width, height);
    }
}
