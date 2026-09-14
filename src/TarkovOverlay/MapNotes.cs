using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovOverlay;

/// <summary>
/// A freehand line drawn on a map. Points are stored in full-resolution map
/// pixels, so a route drawn at one zoom lands on exactly the same terrain at
/// any other. Width is in screen pixels: a route stays readable when you zoom
/// out to see the whole map, and stays crisp when you zoom in.
/// </summary>
public sealed class NoteStroke
{
    public string Color { get; set; } = "#E84A3C";
    public float Width { get; set; } = 4.5f;

    /// <summary>Flat x0, y0, x1, y1, ... in map pixels.</summary>
    public List<double> Points { get; set; } = new();

    private RectangleF? _bounds;

    [JsonIgnore]
    public RectangleF Bounds
    {
        get
        {
            if (_bounds is { } b) return b;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i + 1 < Points.Count; i += 2)
            {
                minX = Math.Min(minX, Points[i]); maxX = Math.Max(maxX, Points[i]);
                minY = Math.Min(minY, Points[i + 1]); maxY = Math.Max(maxY, Points[i + 1]);
            }
            var r = Points.Count < 2
                ? RectangleF.Empty
                : new RectangleF((float)minX, (float)minY, (float)(maxX - minX), (float)(maxY - minY));
            _bounds = r;
            return r;
        }
    }

    public void InvalidateBounds() => _bounds = null;
}

/// <summary>A pinned text label. X/Y is the pin, in map pixels.</summary>
public sealed class NoteText
{
    public double X { get; set; }
    public double Y { get; set; }
    public string Text { get; set; } = "";
    public string Color { get; set; } = "#E84A3C";
}

/// <summary>Everything the user has written on one map.</summary>
public sealed class MapNotes
{
    public List<NoteStroke> Strokes { get; set; } = new();
    public List<NoteText> Texts { get; set; } = new();

    [JsonIgnore]
    public bool IsEmpty => Strokes.Count == 0 && Texts.Count == 0;
}

/// <summary>
/// One JSON file per map under <c>%APPDATA%\TarkovOverlay\notes</c>, kept apart
/// from config.json so a corrupt or huge notes file can never stop the app
/// starting, and so notes survive a config reset.
/// </summary>
public static class NoteStore
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public static string Dir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TarkovOverlay", "notes");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string PathFor(string mapId)
    {
        var safe = string.Concat(mapId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(Dir, safe + ".json");
    }

    public static MapNotes Load(string mapId)
    {
        try
        {
            var path = PathFor(mapId);
            if (!File.Exists(path)) return new MapNotes();
            var notes = JsonSerializer.Deserialize<MapNotes>(File.ReadAllText(path), Opts) ?? new MapNotes();
            // Drop anything malformed rather than letting one bad entry break the map.
            notes.Strokes.RemoveAll(s => s.Points.Count < 2 || s.Points.Count % 2 != 0);
            notes.Texts.RemoveAll(t => string.IsNullOrWhiteSpace(t.Text));
            return notes;
        }
        catch
        {
            return new MapNotes();
        }
    }

    public static void Save(string mapId, MapNotes notes)
    {
        try
        {
            var path = PathFor(mapId);
            if (notes.IsEmpty)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            // Write beside, then swap in: a crash mid-write cannot lose the old notes.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(notes, Opts));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Losing one autosave is not worth interrupting the user over.
        }
    }
}
