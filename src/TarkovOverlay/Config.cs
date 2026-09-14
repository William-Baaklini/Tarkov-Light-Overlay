using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovOverlay;

public sealed class Hotkey
{
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }

    /// <summary>Virtual key code. 0 means "unassigned".</summary>
    public int Key { get; set; }

    [JsonIgnore]
    public bool IsSet => Key != 0;

    [JsonIgnore]
    public Native.HotkeyModifiers Modifiers
    {
        get
        {
            var m = Native.HotkeyModifiers.NoRepeat;
            if (Ctrl) m |= Native.HotkeyModifiers.Control;
            if (Alt) m |= Native.HotkeyModifiers.Alt;
            if (Shift) m |= Native.HotkeyModifiers.Shift;
            if (Win) m |= Native.HotkeyModifiers.Win;
            return m;
        }
    }

    public static Hotkey FromKeyData(Keys keyData) => new()
    {
        Ctrl = (keyData & Keys.Control) != 0,
        Alt = (keyData & Keys.Alt) != 0,
        Shift = (keyData & Keys.Shift) != 0,
        Key = (int)(keyData & Keys.KeyCode),
    };

    public override string ToString()
    {
        if (!IsSet) return "(none)";
        var parts = new List<string>(5);
        // Ordered the way Windows names its own shortcuts: Win + Shift + S.
        if (Win) parts.Add("Win");
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        parts.Add(KeyName((Keys)Key));
        return string.Join(" + ", parts);
    }

    private static string KeyName(Keys k) => k switch
    {
        Keys.Oemtilde => "`",
        Keys.OemMinus => "-",
        Keys.Oemplus => "=",
        Keys.OemOpenBrackets => "[",
        Keys.Oem6 => "]",
        Keys.Oem5 => "\\",
        Keys.Oem1 => ";",
        Keys.Oem7 => "'",
        Keys.Oemcomma => ",",
        Keys.OemPeriod => ".",
        Keys.OemQuestion => "/",
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (k - Keys.D0))).ToString(),
        _ => k.ToString(),
    };
}

public sealed class Config
{
    public Hotkey ToggleOverlay { get; set; } = new() { Ctrl = true, Shift = true, Key = (int)Keys.T };
    public Hotkey ToggleSecondMonitor { get; set; } = new() { Ctrl = true, Shift = true, Key = (int)Keys.M };
    /// <summary>Windows display device name; empty chooses a non-primary display automatically.</summary>
    public string SecondMonitorDeviceName { get; set; } = "";
    public Hotkey OpenTarkovDev { get; set; } = new();
    public Hotkey OpenWiki { get; set; } = new();
    public Hotkey OpenAmmo { get; set; } = new();
    public Hotkey OpenMaps { get; set; } = new();

    public string LastTab { get; set; } = "maps";
    public string LastMapId { get; set; } = "";

    /// <summary>Per-map saved view, so reopening a map lands where you left it.</summary>
    public Dictionary<string, SavedView> MapViews { get; set; } = new();

    // ---- map notes ---------------------------------------------------------
    // The notes themselves live in %APPDATA%\TarkovOverlay\notes\<map>.json;
    // only the pen you last picked is remembered here.

    /// <summary>Last pen colour, as #RRGGBB.</summary>
    public string NotePenColor { get; set; } = "#E84A3C";

    /// <summary>Last pen width, in screen pixels.</summary>
    public float NotePenWidth { get; set; } = 4.5f;

    /// <summary>Whether notes are drawn over the map at all.</summary>
    public bool NotesVisible { get; set; } = true;

    public string TarkovDevUrl { get; set; } = "https://tarkov.dev/";
    public string WikiUrl { get; set; } = "https://escapefromtarkov.fandom.com/wiki/Escape_from_Tarkov_Wiki";
    public string AmmoUrl { get; set; } = "https://www.eft-ammo.com/";

    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 820;

    /// <summary>0.30 - 1.00. Lets you keep half an eye on the game underneath.</summary>
    public double Opacity { get; set; } = 1.0;

    public bool BlockAds { get; set; } = true;

    /// <summary>
    /// Also cut off anti-adblock circumvention CDNs. Saves more memory, but
    /// some sites (Fandom in particular) refuse to render without them.
    /// </summary>
    public bool BlockAdsStrict { get; set; } = false;

    /// <summary>
    /// Render pages in software. Drops the GPU helper process and stops the
    /// overlay competing with the game for the GPU. Applied at startup.
    /// </summary>
    public bool LowGpuMode { get; set; } = true;

    /// <summary>Tear down browser renderers when hidden. Costs a reload, frees ~150 MB.</summary>
    public bool UnloadWebOnHide { get; set; } = true;

    /// <summary>Max decoded map tiles kept in RAM. Each 512x512 tile is ~1 MB.</summary>
    public int TileCacheSize { get; set; } = 48;

    // ---- item scanner ------------------------------------------------------

    /// <summary>Read the item under the cursor off the screen and price it.</summary>
    public bool ScanEnabled { get; set; } = true;

    /// <summary>Scan hotkey. Pressing it again while the card is up opens the item on tarkov.dev.</summary>
    public Hotkey ScanItem { get; set; } = new() { Alt = true, Key = (int)Keys.F };

    /// <summary>
    /// Price the item under the cursor automatically, without a keypress. Only
    /// costs a GetCursorPos per tick until the cursor settles on a new cell.
    /// </summary>
    public bool ScanHoverMode { get; set; } = false;

    /// <summary>Restrict hover scanning to when Tarkov is the foreground window.</summary>
    public bool ScanOnlyInTarkov { get; set; } = true;

    /// <summary>How long the hover card stays up, ms. Hotkey scans stay until dismissed.</summary>
    public int ScanPopupMs { get; set; } = 2500;

    // ---- persistence -------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ConfigPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TarkovOverlay");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "config.json");
        }
    }

    public static Config Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), JsonOpts) ?? new Config();
        }
        catch { /* a corrupt config shouldn't stop the app starting */ }
        return new Config();
    }

    public void Save()
    {
        try { File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts)); }
        catch { /* not worth interrupting the user over */ }
    }
}

public sealed class SavedView
{
    public double Scale { get; set; }
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
}
