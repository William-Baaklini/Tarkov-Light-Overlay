namespace TarkovOverlay;

/// <summary>One candidate the matcher considered, best (lowest) residual first.</summary>
public readonly record struct IconMatch(string ItemId, int GridW, int GridH, bool Rotated, float Residual);

/// <summary>
/// The icon-matching index built by <c>tools/build_icon_index.py</c>.
///
/// Every Tarkov inventory icon is <c>63*n + 1</c> pixels on a side, because 63px
/// is the game's cell pitch and the +1 is the shared grid border. At 1080p/100%
/// the game blits those icons 1:1, so a crop taken off the screen is essentially
/// the reference image with the slot background showing through wherever the
/// icon is transparent:
///
///     observed = premultiplied + (1 - alpha) * background
///
/// The index therefore stores premultiplied luminance *and* alpha per icon, and
/// the matcher solves for <c>background</c> in closed form per candidate. That
/// makes matching invariant to Tarkov's rarity background tints without us ever
/// having to know what those colours are.
/// </summary>
public sealed class IconIndex
{
    public const int Thumb = 16;
    public const int Cells = Thumb * Thumb;
    public const int Cell = 63;

    /// <summary>
    /// Largest squared error a single pixel may contribute, i.e. differences
    /// beyond this many grey levels stop counting for more.
    /// </summary>
    private const float OutlierCap = 55f * 55f;

    /// <summary>Weight still given to a fully transparent pixel of a candidate.</summary>
    private const float AlphaFloor = 0.15f;

    // Flat parallel arrays rather than an object per icon: ~3900 icons would
    // otherwise cost more in headers and references than in actual pixels.
    private byte[] _ids = Array.Empty<byte>();      // 12 bytes per icon
    private byte[] _grid = Array.Empty<byte>();     // gw, gh per icon
    private byte[] _premult = Array.Empty<byte>();  // Cells per icon
    private byte[] _alpha = Array.Empty<byte>();    // Cells per icon

    private readonly Dictionary<int, int[]> _buckets = new();

    public int Count { get; private set; }
    public bool Loaded => Count > 0;

    /// <summary>Grid sizes present in the index, so the scanner only tries shapes that exist.</summary>
    public IReadOnlyList<(int W, int H)> Shapes { get; private set; } = Array.Empty<(int, int)>();

    /// <summary>
    /// Pixels the game paints *over* the icon. Taken from real captures rather
    /// than guessed: Tarkov writes the item's name across the top of the cell,
    /// the durability or resource count across the bottom - in large red digits,
    /// not the thin bar assumed at first - and a found-in-raid tick and stack
    /// count in the bottom-right. Down-weighting beats masking outright, since
    /// those rows still carry some of the icon.
    /// </summary>
    private static readonly float[] Weights = BuildWeights();
    private static readonly float WeightSum;

    static IconIndex()
    {
        float s = 0;
        foreach (var w in Weights) s += w;
        WeightSum = s;
    }

    private static float[] BuildWeights()
    {
        var w = new float[Cells];
        for (int y = 0; y < Thumb; y++)
            for (int x = 0; x < Thumb; x++)
            {
                float v = 1f;
                if (y < 3) v = 0.30f;                                // item name label
                if (y >= Thumb - 3) v = 0.25f;                       // durability / count
                if (y >= Thumb - 4 && x >= Thumb - 5) v = 0.10f;     // tick, stack count
                w[y * Thumb + x] = v;
            }
        return w;
    }

    public static string? FindIndexFile()
    {
        var env = Environment.GetEnvironmentVariable("TARKOV_OVERLAY_ICON_INDEX");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var c = Path.Combine(dir.FullName, "data", "icons.idx");
            if (File.Exists(c)) return c;
        }
        return null;
    }

    public static IconIndex Load()
    {
        var idx = new IconIndex();
        var path = FindIndexFile();
        if (path is null) return idx;
        try { idx.Read(path); }
        catch { idx.Count = 0; }
        return idx;
    }

    private void Read(string path)
    {
        var blob = File.ReadAllBytes(path);
        if (blob.Length < 16 || blob[0] != 'T' || blob[1] != 'L' || blob[2] != 'O' || blob[3] != 'I')
            throw new InvalidDataException("not an icon index");

        int version = BitConverter.ToInt32(blob, 4);
        int count = BitConverter.ToInt32(blob, 8);
        int thumb = BitConverter.ToInt32(blob, 12);
        if (version != 1 || thumb != Thumb) throw new InvalidDataException("index version mismatch");

        const int header = 16;
        int rec = 12 + 2 + Cells * 2;
        if (blob.Length < header + (long)count * rec) throw new InvalidDataException("index truncated");

        _ids = new byte[count * 12];
        _grid = new byte[count * 2];
        _premult = new byte[count * Cells];
        _alpha = new byte[count * Cells];

        var byShape = new Dictionary<int, List<int>>();
        for (int i = 0; i < count; i++)
        {
            int o = header + i * rec;
            Buffer.BlockCopy(blob, o, _ids, i * 12, 12);
            byte gw = blob[o + 12], gh = blob[o + 13];
            _grid[i * 2] = gw;
            _grid[i * 2 + 1] = gh;
            Buffer.BlockCopy(blob, o + 14, _premult, i * Cells, Cells);
            Buffer.BlockCopy(blob, o + 14 + Cells, _alpha, i * Cells, Cells);

            int key = Key(gw, gh);
            if (!byShape.TryGetValue(key, out var list)) byShape[key] = list = new List<int>();
            list.Add(i);
        }

        foreach (var (k, v) in byShape) _buckets[k] = v.ToArray();
        Shapes = byShape.Keys.Select(k => (k >> 8, k & 0xFF)).ToList();
        Count = count;
    }

    private static int Key(int w, int h) => (w << 8) | h;

    public string IdAt(int i) => Convert.ToHexString(_ids, i * 12, 12).ToLowerInvariant();

    /// <summary>Does the index contain any icon of this slot shape, in either orientation?</summary>
    public bool HasShape(int w, int h) =>
        _buckets.ContainsKey(Key(w, h)) || _buckets.ContainsKey(Key(h, w));

    /// <summary>
    /// Score every icon that could occupy a <paramref name="gw"/>x<paramref name="gh"/>
    /// slot against an observed 16x16 luminance thumbnail, appending results to
    /// <paramref name="into"/>. Icons whose native shape is the transpose are
    /// scored rotated, which is how the game draws a rotated item.
    /// </summary>
    public void Score(float[] observed, int gw, int gh, List<IconMatch> into)
    {
        if (_buckets.TryGetValue(Key(gw, gh), out var upright))
            ScoreBucket(observed, upright, false, into);

        // A 2x1 item turned sideways occupies a 1x2 slot. Thumbnails are square,
        // and squashing to a square commutes with a 90 degree rotation, so we can
        // rotate the stored thumbnail rather than store a second copy.
        if (gw != gh && _buckets.TryGetValue(Key(gh, gw), out var rotated))
            ScoreBucket(observed, rotated, true, into);
    }

    private void ScoreBucket(float[] obs, int[] bucket, bool rotate, List<IconMatch> into)
    {
        foreach (int i in bucket)
        {
            int baseOff = i * Cells;

            // Fit both unknowns at once:
            //
            //     observed  ~=  gain * premult  +  (1 - alpha) * background
            //
            // The background term alone is not enough. Tarkov draws item art
            // dimmer than the reference image - measured against a real capture
            // of a correctly identified, pixel-aligned item, the icon body came
            // out at about 0.87 of the reference while the exposed background
            // matched exactly. With only an additive term to absorb that, the
            // clean middle of a correct icon carried as much error as the parts
            // covered by text, and correct matches bottomed out around 8 instead
            // of near 1. Two-parameter weighted least squares, solved in closed
            // form per candidate.
            float spp = 0f, spu = 0f, suu = 0f, spo = 0f, suo = 0f, wsum = 0f;
            for (int k = 0; k < Cells; k++)
            {
                int src = rotate ? Rotate(k) : k;
                float p = _premult[baseOff + src];
                float alpha = _alpha[baseOff + src] * (1f / 255f);
                float u = 1f - alpha;
                // Lean on the pixels where this candidate says there is artwork.
                // Tarkov's empty slot is not a flat colour - it carries a diagonal
                // crosshatch - so fully transparent pixels mostly measure that
                // texture, and on a real capture they carried more error than the
                // icon itself. They are not discarded: a candidate claiming icon
                // where the screen shows background is still penalised there.
                float w = Weights[k] * (AlphaFloor + (1f - AlphaFloor) * alpha);
                wsum += w;
                float o = obs[k];
                spp += w * p * p;
                spu += w * p * u;
                suu += w * u * u;
                spo += w * p * o;
                suo += w * u * o;
            }

            float det = spp * suu - spu * spu;
            float gain, bg;
            if (MathF.Abs(det) > 1e-3f)
            {
                gain = (suu * spo - spu * suo) / det;
                bg = (spp * suo - spu * spo) / det;
            }
            else if (spp > 1e-3f) { gain = spo / spp; bg = 0f; }
            else if (suu > 1e-3f) { gain = 1f; bg = suo / suu; }
            else { gain = 1f; bg = 0f; }

            // Keep the fit honest: a free gain would otherwise let a candidate
            // shrink a mismatched icon towards nothing and call it a match.
            if (gain < 0.55f) gain = 0.55f; else if (gain > 1.45f) gain = 1.45f;
            if (bg < 0f) bg = 0f; else if (bg > 255f) bg = 255f;

            // Clipped loss, not plain squared error. The game writes the item's
            // name across the top of the cell and its durability across the
            // bottom, so a handful of pixels wrong by 200 levels would otherwise
            // swamp the thousands that are right. Capping each pixel's
            // contribution bounds what any occluder can do - name label,
            // durability count, tooltip or mouse pointer - without needing to
            // know where any of them sit.
            // Weighted error over the cell.
            //
            // Discarding the worst 4x4 blocks was tried here, to shrug off the
            // name tooltip Tarkov draws on top of the very item being pointed at.
            // Measured against a real capture it lowered a correct match's score
            // (5.1 to 4.0) but never changed which item won, and it cost real
            // discrimination: three to five slot shapes per four hundred started
            // coming out wrong. Not worth it until there is a body of real
            // captures to tune against.
            float acc = 0f;
            for (int k = 0; k < Cells; k++)
            {
                int src = rotate ? Rotate(k) : k;
                float p = _premult[baseOff + src];
                float alpha = _alpha[baseOff + src] * (1f / 255f);
                float u = 1f - alpha;
                float w = Weights[k] * (AlphaFloor + (1f - AlphaFloor) * alpha);
                float d = gain * p + u * bg - obs[k];
                float e = d * d;
                acc += w * (e < OutlierCap ? e : OutlierCap);
            }

            into.Add(new IconMatch(
                IdAt(i),
                _grid[i * 2], _grid[i * 2 + 1],
                rotate,
                MathF.Sqrt(acc / MathF.Max(wsum, 1e-3f))));
        }
    }

    /// <summary>Index of the pixel that lands on <paramref name="k"/> after a 90 degree turn.</summary>
    private static int Rotate(int k)
    {
        int y = k / Thumb, x = k % Thumb;
        // destination (x,y) reads from source (y, Thumb-1-x)
        return x * Thumb + (Thumb - 1 - y);
    }
}
