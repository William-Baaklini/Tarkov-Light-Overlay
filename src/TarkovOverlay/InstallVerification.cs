using Microsoft.Web.WebView2.Core;
using System.Text.Json;

namespace TarkovOverlay;

/// <summary>A noninteractive check of the actual published application, isolated from user settings.</summary>
internal static class InstallVerification
{
    public static void Run(string reportPath)
    {
        try
        {
            using var window = new Form { Icon = AppIcon.Load() };
            _ = window.Handle;
            var catalog = MapCatalog.Load();
            if (catalog.Maps.Count == 0) throw new InvalidOperationException("No maps found.");
            var index = IconIndex.Load();
            if (!index.Loaded) throw new InvalidOperationException("Scanner index missing.");
            // Also exercises native DLL resolution from the single-file bundle.
            var browser = CoreWebView2Environment.GetAvailableBrowserVersionString();
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                success = true, maps = catalog.Maps.Count, scannerItems = index.Count,
                webView2 = browser, iconWidth = window.Icon.Width,
            }));
        }
        catch (Exception error)
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { success = false, error = error.ToString() }));
            Environment.ExitCode = 1;
        }
    }
}
