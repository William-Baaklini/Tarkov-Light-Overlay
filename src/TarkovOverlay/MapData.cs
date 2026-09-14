using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovOverlay;

public sealed class LevelMeta
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int Cols { get; set; }
    public int Rows { get; set; }
}

public sealed class MapMeta
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int TileSize { get; set; } = 512;
    public int MaxLevel { get; set; }
    public Dictionary<string, LevelMeta> Levels { get; set; } = new();

    [JsonIgnore]
    public string TilesDir { get; set; } = "";

    public LevelMeta Level(int z) => Levels[z.ToString()];
}

public sealed class MapCatalog
{
    public int TileSize { get; set; } = 512;
    public List<MapMeta> Maps { get; set; } = new();

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Walks up from the executable looking for the generated <c>tiles</c> folder,
    /// so the app works both from bin/Debug and from a published folder.
    /// </summary>
    public static string? FindTilesRoot()
    {
        var envRoot = Environment.GetEnvironmentVariable("TARKOV_OVERLAY_TILES");
        if (!string.IsNullOrEmpty(envRoot) && File.Exists(Path.Combine(envRoot, "maps.json")))
            return envRoot;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tiles");
            if (File.Exists(Path.Combine(candidate, "maps.json")))
                return candidate;
        }
        return null;
    }

    public static MapCatalog Load()
    {
        var root = FindTilesRoot();
        if (root is null) return new MapCatalog();

        try
        {
            var cat = JsonSerializer.Deserialize<MapCatalog>(
                File.ReadAllText(Path.Combine(root, "maps.json")), Opts) ?? new MapCatalog();
            foreach (var m in cat.Maps)
                m.TilesDir = Path.Combine(root, m.Id);
            return cat;
        }
        catch
        {
            return new MapCatalog();
        }
    }
}
