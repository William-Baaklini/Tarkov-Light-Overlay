using TarkovOverlay;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        var primary = new DisplayInfo("primary", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), true);
        var secondary = new DisplayInfo("secondary", new(-2560, -180, 2560, 1440), new(-2560, -180, 2560, 1400), false);
        DisplayInfo[] displays = [primary];
        using var window = new Form { FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual, Bounds = new(100, 120, 850, 600),
            Padding = new Padding(5), MinimumSize = new(560, 360) };
        _ = window.Handle;
        var initial = window.Bounds;
        var mode = new SecondMonitorMode(window, () => displays);
        Check(!mode.Toggle() && window.Bounds == initial && !mode.IsActive, "one display leaves geometry untouched");
        displays = [primary, secondary];
        Check(mode.Toggle() && mode.IsActive, "enters second-monitor mode");
        Check(window.Bounds == secondary.Bounds && window.Padding == Padding.Empty, "fills complete display, including taskbar area");
        Check(mode.SavedBounds == initial, "preserves normal geometry for persistence");
        window.Hide();
        mode.Refresh();
        Check(window.Bounds == secondary.Bounds, "refresh while hidden preserves fullscreen");
        Check(mode.Toggle() && !mode.IsActive && window.Bounds == initial, "repeat restores exact position and size");
        Check(window.Padding == new Padding(5) && window.MinimumSize == new Size(560, 360), "restores resize frame and minimum size");
        mode.Toggle();
        displays = [primary];
        mode.Refresh();
        Check(!mode.IsActive && window.Bounds == initial, "disconnecting target restores original window");
        displays = [primary, secondary];
        window.Bounds = new Rectangle(-2200, 60, 900, 640);
        initial = window.Bounds;
        mode.Toggle();
        Check(window.Bounds == primary.Bounds, "starting on secondary moves to the other display");
        mode.Toggle();
        Check(window.Bounds == initial, "negative-coordinate original position restores");
        mode.Toggle();
        displays = [primary];
        mode.Toggle();
        Check(primary.WorkingArea.Contains(window.Bounds), "missing original monitor restores on a reachable display");
        displays = [primary, secondary];
        window.Bounds = new Rectangle(100, 120, 850, 600);
        window.WindowState = FormWindowState.Maximized;
        var normalBounds = window.RestoreBounds;
        mode.Toggle();
        Check(mode.SavedBounds == normalBounds, "maximized entry preserves normal restore geometry");
        mode.Toggle();
        Check(window.WindowState == FormWindowState.Maximized, "restores prior maximized state");
        window.WindowState = FormWindowState.Normal;

        // Use the real display geometry/DPI too, without opening the production app
        // or reading/writing its configuration, notes, hotkeys, or scanner state.
        var physical = Screen.AllScreens;
        if (physical.Length > 1)
        {
            var area = Screen.PrimaryScreen!.WorkingArea;
            window.Bounds = new Rectangle(area.X + 50, area.Y + 50, 800, 500);
            initial = window.Bounds;
            var actual = new SecondMonitorMode(window);
            actual.Toggle();
            Application.DoEvents();
            actual.Refresh();
            Check(Screen.AllScreens.Any(s => !s.Primary && window.Bounds == s.Bounds), "real second display uses exact physical bounds");
            actual.Toggle();
            Application.DoEvents();
            Check(window.Bounds == initial, "real mixed-DPI round trip restores exact physical bounds");
        }
        Console.WriteLine("All window placement checks passed.");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine("PASS: " + description);
    }
}
