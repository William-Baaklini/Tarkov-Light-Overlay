namespace TarkovOverlay;

/// <summary>
/// Wraps RegisterHotKey. These are system-wide, work while Tarkov has focus,
/// and require no hooking or injection into the game.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    public const int IdToggle = 1;
    public const int IdTarkovDev = 2;
    public const int IdWiki = 3;
    public const int IdAmmo = 4;
    public const int IdMaps = 5;
    public const int IdScanItem = 6;
    public const int IdSecondMonitor = 7;

    private readonly IntPtr _hwnd;
    private readonly List<int> _registered = new();

    public HotkeyManager(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>Names of hotkeys Windows refused (usually taken by another app).</summary>
    public List<string> Failures { get; } = new();

    public void Rebind(Config cfg)
    {
        UnregisterAll();
        Failures.Clear();
        Try(IdToggle, cfg.ToggleOverlay, "Toggle overlay");
        Try(IdSecondMonitor, cfg.ToggleSecondMonitor, "Second monitor / restore");
        Try(IdTarkovDev, cfg.OpenTarkovDev, "Open Tarkov.dev");
        Try(IdWiki, cfg.OpenWiki, "Open Wiki");
        Try(IdAmmo, cfg.OpenAmmo, "Open Ammo");
        Try(IdMaps, cfg.OpenMaps, "Open Maps");
        if (cfg.ScanEnabled) Try(IdScanItem, cfg.ScanItem, "Scan item");
    }

    private void Try(int id, Hotkey hk, string label)
    {
        if (!hk.IsSet) return;
        if (Native.RegisterHotKey(_hwnd, id, (uint)hk.Modifiers, (uint)hk.Key))
            _registered.Add(id);
        else
            Failures.Add($"{label}  ({hk})");
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered) Native.UnregisterHotKey(_hwnd, id);
        _registered.Clear();
    }

    public void Dispose() => UnregisterAll();
}
