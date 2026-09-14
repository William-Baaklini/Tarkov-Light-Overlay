using System.Threading;

namespace TarkovOverlay;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // A second copy would fight the first one for the global hotkeys.
        using var single = new Mutex(true, "TarkovLightOverlay.SingleInstance", out bool isFirst);
        if (!isFirst) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool firstRun = !File.Exists(Config.ConfigPath);
        var cfg = Config.Load();

        // Browser flags are baked in when the shared environment is created,
        // so this has to happen before the first tab is ever shown.
        WebTab.LowGpuMode = cfg.LowGpuMode;

        using var form = new OverlayForm(cfg, showOnStart: firstRun);
        Application.Run(form);
    }
}
