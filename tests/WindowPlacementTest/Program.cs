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
        var window = new FakeWindow { Bounds = new(100, 120, 850, 600),
            Padding = new Padding(5), MinimumSize = new(560, 360) };
        var initial = window.Bounds;
        var mode = new SecondMonitorMode(window, () => displays);
        Check(!mode.Toggle() && window.Bounds == initial && !mode.IsActive, "one display leaves geometry untouched");
        displays = [primary, secondary];
        Check(mode.Toggle() && mode.IsActive, "enters second-monitor mode");
        Check(window.Bounds == secondary.Bounds && window.Padding == Padding.Empty, "fills complete display, including taskbar area");
        Check(mode.SavedBounds == initial, "preserves normal geometry for persistence");
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
        Check(window.Bounds == secondary.Bounds, "automatic mode stays on a non-primary display even when already there");
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

        var third = new DisplayInfo("third", new(1920, 0, 1920, 1200), new(1920, 0, 1920, 1160), false);
        displays = [third, secondary, primary];
        window.Bounds = new Rectangle(100, 120, 850, 600);
        initial = window.Bounds;
        Check(mode.Toggle("third") && window.Bounds == third.Bounds, "explicit choice selects the requested display on a three-monitor desktop");
        Check(mode.Toggle("primary") && window.Bounds == initial, "changing the preference while fullscreen still restores the original window first");
        Check(mode.Toggle("primary") && window.Bounds == primary.Bounds, "explicit choice can fill the main/current display");
        mode.Toggle();
        Check(window.Bounds == initial, "main-display fullscreen restores exact geometry");
        Check(!mode.Toggle("disconnected") && !mode.IsActive && window.Bounds == initial,
            "missing selected monitor does not move the window or enter fullscreen");
        displays = [primary];
        Check(mode.Toggle("primary") && window.Bounds == primary.Bounds, "explicit choice also works with one attached display");
        mode.Toggle();
        Check(!mode.Toggle() && window.Bounds == initial, "automatic mode needs a non-primary display");
        displays = [third, primary, secondary];
        mode.Toggle();
        Check(window.Bounds == secondary.Bounds, "automatic choice is independent of display enumeration order");
        mode.Toggle();
        mode.Toggle("THIRD");
        Check(window.Bounds == third.Bounds, "saved display names match without case sensitivity");
        displays = [primary, secondary];
        mode.Refresh();
        Check(!mode.IsActive && window.Bounds == initial, "disconnecting the explicitly selected display restores original geometry");

        // Use the real display geometry/DPI too, without opening the production app
        // or reading/writing its configuration, notes, hotkeys, or scanner state.
        var physical = Screen.AllScreens;
        if (physical.Length > 1)
        {
            using var actualWindow = new Form { FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual, Padding = new Padding(5) };
            _ = actualWindow.Handle;
            var area = Screen.PrimaryScreen!.WorkingArea;
            actualWindow.Bounds = new Rectangle(area.X + 50, area.Y + 50, 800, 500);
            initial = actualWindow.Bounds;
            var actual = new SecondMonitorMode(actualWindow);
            actual.Toggle();
            Application.DoEvents();
            actual.Refresh();
            Check(Screen.AllScreens.Any(s => !s.Primary && actualWindow.Bounds == s.Bounds), "real second display uses exact physical bounds");
            actual.Toggle();
            Application.DoEvents();
            Check(actualWindow.Bounds == initial, "real mixed-DPI round trip restores exact physical bounds");
        }
        Console.WriteLine("All window placement checks passed.");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine("PASS: " + description);
    }

    // Synthetic layouts must not be constrained by the CI runner's physical
    // desktop size. The real Form adapter is exercised separately when possible.
    private sealed class FakeWindow : IPlacementWindow
    {
        public Rectangle Bounds { get; set; }
        public Rectangle RestoreBounds => Bounds;
        public FormWindowState WindowState { get; set; }
        public Padding Padding { get; set; }
        public Size MinimumSize { get; set; }
    }
}
