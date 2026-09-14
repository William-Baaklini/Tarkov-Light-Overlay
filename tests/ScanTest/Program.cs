using System.Diagnostics;
using System.Drawing.Imaging;

namespace TarkovOverlay;

/// <summary>
/// Renders synthetic Tarkov inventories and checks the scanner reads them back.
///
/// The scanner cannot be unit tested against the real game from here, so this
/// reproduces the parts of the rendering it actually depends on: a 63px grid at
/// an arbitrary phase, items merged into borderless rectangles, rarity-tinted
/// slot backgrounds, and the durability bar and stack count the game paints over
/// the icon. What it deliberately does not fake is the game's exact colours -
/// the scanner is not allowed to know those.
/// </summary>
internal static class Program
{
    private const int Cell = 63;

    private static readonly Color[] Rarities =
    {
        Color.FromArgb(10, 10, 10), Color.FromArgb(26, 28, 32), Color.FromArgb(38, 32, 52),
        Color.FromArgb(28, 38, 52), Color.FromArgb(52, 46, 24), Color.FromArgb(52, 34, 24),
        Color.FromArgb(24, 44, 28), Color.FromArgb(52, 24, 24), Color.FromArgb(44, 44, 44),
    };

    private static readonly Color GridLine = Color.FromArgb(58, 60, 64);
    private static readonly Color Panel = Color.FromArgb(20, 21, 24);

    private sealed record Icon(string Id, string Path, int W, int H);

    private static int Main(string[] args)
    {
        string root = FindRoot();
        string iconDir = Path.Combine(root, "Examples", "RatScanner", "Data", "icons");
        if (!Directory.Exists(iconDir))
        {
            Console.Error.WriteLine($"icons not found at {iconDir}");
            return 1;
        }

        var index = IconIndex.Load();
        if (!index.Loaded)
        {
            Console.Error.WriteLine("icon index not found - run tools/build_icon_index.py");
            return 1;
        }
        Console.WriteLine($"index: {index.Count} icons, {index.Shapes.Count} shapes");

        var icons = LoadIconList(iconDir);
        Console.WriteLine($"icons: {icons.Count} usable on disk");

        var byId = icons.ToDictionary(i => i.Id, i => i.Path);

        var rng = new Random(7);

        // --dump writes one synthetic stash to disk so the real app can be driven
        // against it on screen, end to end.
        if (args.Contains("--dump"))
        {
            var t0 = icons.First(i => i.W == 2 && i.H == 1);
            using var one = RenderInventory(icons, t0, rng, out var cur, out var slot);
            var outPath = Path.Combine(Path.GetTempPath(), "tlo_inventory.png");
            one.Save(outPath, ImageFormat.Png);
            Console.WriteLine($"image={outPath}");
            Console.WriteLine($"size={one.Width}x{one.Height}");
            Console.WriteLine($"target={t0.Id}");
            Console.WriteLine($"cursor={cur.X},{cur.Y}");
            Console.WriteLine($"slot={slot.X},{slot.Y},{slot.Width},{slot.Height}");
            return 0;
        }

        // --file <png> <x> <y>: scan a real capture, to compare what the app sees
        // on screen against what the offline harness sees.
        if (args.Length >= 4 && args[0] == "--file")
        {
            using var img = new Bitmap(args[1]);
            var pt = new Point(int.Parse(args[2]), int.Parse(args[3]));
            ScreenScan.Diagnostics = true;
            using var sc = new ScreenScan();
            var r = sc.ScanImage(img, pt, index);
            if (r?.Best is null) { Console.WriteLine("no result"); return 1; }
            Console.WriteLine($"phases sumX={sc.LastPhases.SumX} medX={sc.LastPhases.MedX} " +
                              $"sumY={sc.LastPhases.SumY} medY={sc.LastPhases.MedY}");
            Console.WriteLine($"local phase x={sc.LastLocalPhase.X} y={sc.LastLocalPhase.Y}");
            foreach (var t2 in sc.LastTrace) Console.WriteLine("  try " + t2);
            Console.WriteLine($"slot   {r.SlotRect}  ({r.SlotRect.Width / Cell}x{r.SlotRect.Height / Cell})");
            foreach (var c in r.TiedUpTo(5))
                Console.WriteLine($"  {c.ItemId}  {c.GridW}x{c.GridH}{(c.Rotated ? " rot" : "")}  r={c.Residual:F2}");
            return 0;
        }

        // --sweep <png> <x> <y>: try every possible grid alignment for a 1x1 slot
        // under the cursor and report which one actually matches an icon. Ground
        // truth for "where is the grid, really" on a capture from the live game.
        if (args.Length >= 4 && args[0] == "--sweep")
        {
            using var img = new Bitmap(args[1]);
            int cxp = int.Parse(args[2]), cyp = int.Parse(args[3]);
            using var sc = new ScreenScan();
            var results = new List<(float R, int X, int Y, string Id, int W, int H)>();
            bool first = true;
            foreach (var (gw, gh) in new[] { (1, 1), (2, 1), (1, 2), (2, 2) })
                for (int oy = cyp - Cell * gh + 1; oy <= cyp; oy++)
                    for (int ox = cxp - Cell * gw + 1; ox <= cxp; ox++)
                    {
                        var rect = new Rectangle(ox, oy, Cell * gw + 1, Cell * gh + 1);
                        if (ox < 0 || oy < 0 || rect.Right > img.Width || rect.Bottom > img.Height) continue;
                        var r = sc.ScoreKnownSlot(img, rect, index, reload: first);
                        first = false;
                        if (r.Count > 0) results.Add((r[0].Residual, ox, oy, r[0].ItemId, gw, gh));
                    }
            results.Sort((a, b) => a.R.CompareTo(b.R));
            Console.WriteLine($"swept {results.Count} alignments; best 12:");
            foreach (var r in results.Take(12))
                Console.WriteLine($"  r={r.R,6:F2}  slot=({r.X},{r.Y}) {r.W}x{r.H}  {r.Id}");
            return 0;
        }

        int trials = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 300;

        int ok = 0, tieOk = 0, missed = 0, wrongShape = 0, tested = 0;
        int failSameShape = 0, failWrongShape = 0, diagShown = 0, inShown = 0;
        var shownSizes = new List<int>();
        bool diag = args.Contains("--diag");
        var times = new List<double>();
        var sw = new Stopwatch();
        using var scanner = new ScreenScan();

        for (int t = 0; t < trials; t++)
        {
            var target = icons[rng.Next(icons.Count)];
            if (target.W > 5 || target.H > 5) continue;

            using var inv = RenderInventory(icons, target, rng, out Point cursor, out Rectangle expect);

            sw.Restart();
            var res = scanner.ScanImage(inv, cursor, index);
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
            tested++;

            if (res?.Best is null) { missed++; continue; }

            var best = res.Best.Value;
            bool shapeOk = res.SlotRect == expect;
            if (!shapeOk) wrongShape++;

            if (best.ItemId == target.Id) ok++;
            bool pictureOk = best.ItemId == target.Id
                             || (byId.TryGetValue(best.ItemId, out var bp) && SameArtwork(target.Path, bp));
            if (pictureOk) tieOk++;

            // What the user actually sees: the popup lists the tied candidates,
            // so the question that matters is whether the real item is in there.
            var shown = res.Tied;
            if (shown.Any(m => m.ItemId == target.Id)) inShown++;
            shownSizes.Add(shown.Count);

            if (!pictureOk)
            {
                // Was the true item even findable at the correct slot? That
                // separates "we looked in the wrong place" from "we compared
                // badly".
                var atTruth = scanner.ScoreKnownSlot(inv, expect, index);
                int rank = atTruth.FindIndex(m => m.ItemId == target.Id);
                float truthResid = rank >= 0 ? atTruth[rank].Residual : float.NaN;
                if (shapeOk) failSameShape++; else failWrongShape++;
                if (diag && diagShown++ < 12)
                    Console.WriteLine(
                        $"  miss {target.Id} {target.W}x{target.H} " +
                        $"| detected {res.SlotRect.Width / Cell}x{res.SlotRect.Height / Cell}" +
                        $"{(shapeOk ? " (right slot)" : " (WRONG SLOT)")} " +
                        $"| best {best.ItemId} r={best.Residual:F2} " +
                        $"| truth rank {rank} r={truthResid:F2}");
            }
        }

        times.Sort();
        Console.WriteLine();
        Console.WriteLine($"trials              {tested}");
        Console.WriteLine($"exact id            {ok / (double)tested:P1}");
        Console.WriteLine($"correct picture     {tieOk / (double)tested:P1}   <- ceiling, ids sharing artwork are indistinguishable");
        Console.WriteLine($"no result at all    {missed}");
        Console.WriteLine($"wrong slot shape    {wrongShape}");
        Console.WriteLine($"misses w/ right slot {failSameShape}   misses w/ wrong slot {failWrongShape}");
        Console.WriteLine($"true item listed in popup {inShown / (double)tested:P1}");
        shownSizes.Sort();
        Console.WriteLine($"candidates listed: median {shownSizes[shownSizes.Count / 2]}, max {shownSizes[^1]}");
        Console.WriteLine($"scan time  median {times[times.Count / 2]:F1} ms   p95 {times[(int)(times.Count * 0.95)]:F1} ms   max {times[^1]:F1} ms");

        // The picture-level number is the one that has to hold up.
        bool pass = tieOk / (double)tested >= 0.97 && missed == 0 && wrongShape <= tested * 0.02;
        Console.WriteLine();
        Console.WriteLine(pass ? "PASS" : "FAIL");
        return pass ? 0 : 1;
    }

    private static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && d is not null; i++, d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "Examples")))
                return d.FullName;
        return Directory.GetCurrentDirectory();
    }

    private static List<Icon> LoadIconList(string dir)
    {
        var list = new List<Icon>();
        foreach (var p in Directory.EnumerateFiles(dir, "*.png"))
        {
            var id = Path.GetFileNameWithoutExtension(p);
            if (id.Length != 24) continue;
            var (w, h) = PngSize(p);
            if ((w - 1) % Cell != 0 || (h - 1) % Cell != 0) continue;
            list.Add(new Icon(id, p, (w - 1) / Cell, (h - 1) / Cell));
        }
        return list;
    }

    /// <summary>Read the IHDR only - decoding 4000 PNGs just for their size is wasteful.</summary>
    private static (int W, int H) PngSize(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> b = stackalloc byte[24];
        if (fs.Read(b) != 24) return (0, 0);
        int w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
        int h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
        return (w, h);
    }

    /// <summary>
    /// Are two icons the same picture? Several Tarkov ids carry identical
    /// artwork re-encoded, differing by a level or two out of 255. Comparing the
    /// two directly is exact, unlike hashing, where a single pixel straddling a
    /// quantisation boundary splits an otherwise identical pair.
    /// </summary>
    private static bool SameArtwork(string pathA, string pathB)
    {
        using var a = new Bitmap(pathA);
        using var b = new Bitmap(pathB);
        if (a.Width != b.Width || a.Height != b.Height) return false;

        var ra = new Rectangle(0, 0, a.Width, a.Height);
        var da = a.LockBits(ra, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = b.LockBits(ra, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            long total = 0;
            int max = 0, n = da.Stride * da.Height;
            var ba = new byte[n];
            var bb = new byte[n];
            System.Runtime.InteropServices.Marshal.Copy(da.Scan0, ba, 0, n);
            System.Runtime.InteropServices.Marshal.Copy(db.Scan0, bb, 0, n);
            for (int i = 0; i < n; i++)
            {
                int d = Math.Abs(ba[i] - bb[i]);
                total += d;
                if (d > max) max = d;
            }
            return max <= 4 && total / (double)n <= 0.5;
        }
        finally { a.UnlockBits(da); b.UnlockBits(db); }
    }

    /// <summary>
    /// Draw a stash page with the target item somewhere in it, and hand back a
    /// cursor position inside that item plus the slot rect we expect to be found.
    /// </summary>
    private static Bitmap RenderInventory(List<Icon> icons, Icon target, Random rng,
                                          out Point cursor, out Rectangle expect)
    {
        const int cols = 9, rows = 9;
        int ox = rng.Next(Cell), oy = rng.Next(Cell);
        int w = ox + cols * Cell + 2, h = oy + rows * Cell + 2;

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Panel);

        var owner = new int[cols, rows];        // 0 = empty, else placement index
        var placed = new List<(Icon Ic, int Cx, int Cy)>();

        // Target first, so it always fits.
        int tx = rng.Next(Math.Max(1, cols - target.W + 1));
        int ty = rng.Next(Math.Max(1, rows - target.H + 1));
        placed.Add((target, tx, ty));
        Occupy(owner, tx, ty, target.W, target.H, 1);

        // Fill some neighbours so the grid has company and merged edges.
        for (int i = 0; i < 14; i++)
        {
            var ic = icons[rng.Next(icons.Count)];
            if (ic.W > 4 || ic.H > 4) continue;
            int x = rng.Next(cols), y = rng.Next(rows);
            if (x + ic.W > cols || y + ic.H > rows) continue;
            if (!Free(owner, x, y, ic.W, ic.H)) continue;
            placed.Add((ic, x, y));
            Occupy(owner, x, y, ic.W, ic.H, placed.Count);
        }

        // Backgrounds and icons.
        foreach (var (ic, gx, gy) in placed)
        {
            var r = new Rectangle(ox + gx * Cell, oy + gy * Cell, ic.W * Cell + 1, ic.H * Cell + 1);
            using (var b = new SolidBrush(Rarities[rng.Next(Rarities.Length)]))
                g.FillRectangle(b, r);
            using var img = new Bitmap(ic.Path);
            g.DrawImageUnscaled(img, r.X, r.Y);

            // Overlays the game paints on top of the artwork.
            if (rng.NextDouble() < 0.5)
                using (var b = new SolidBrush(Color.FromArgb(120, 160, 90)))
                    g.FillRectangle(b, r.X + 2, r.Bottom - 4, (int)((r.Width - 4) * rng.NextDouble()), 2);
            if (rng.NextDouble() < 0.5)
                using (var b = new SolidBrush(Color.FromArgb(220, 220, 210)))
                using (var f = new Font("Arial", 9))
                    g.DrawString(rng.Next(2, 99).ToString(), f, b, r.Right - 20, r.Bottom - 16);
        }

        // Grid lines only where two different owners meet - inside an item the
        // game merges the cells and draws nothing.
        using (var pen = new Pen(GridLine))
        {
            for (int y = 0; y <= rows; y++)
                for (int x = 0; x < cols; x++)
                {
                    int above = y > 0 ? owner[x, y - 1] : -1;
                    int below = y < rows ? owner[x, y] : -1;
                    if (above != below)
                        g.DrawLine(pen, ox + x * Cell, oy + y * Cell, ox + (x + 1) * Cell, oy + y * Cell);
                }
            for (int x = 0; x <= cols; x++)
                for (int y = 0; y < rows; y++)
                {
                    int left = x > 0 ? owner[x - 1, y] : -1;
                    int right = x < cols ? owner[x, y] : -1;
                    if (left != right)
                        g.DrawLine(pen, ox + x * Cell, oy + y * Cell, ox + x * Cell, oy + (y + 1) * Cell);
                }
        }

        expect = new Rectangle(ox + tx * Cell, oy + ty * Cell, target.W * Cell + 1, target.H * Cell + 1);
        cursor = new Point(
            expect.X + Cell / 2 + Cell * rng.Next(target.W),
            expect.Y + Cell / 2 + Cell * rng.Next(target.H));
        return bmp;
    }

    private static bool Free(int[,] o, int x, int y, int w, int h)
    {
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
                if (o[x + i, y + j] != 0) return false;
        return true;
    }

    private static void Occupy(int[,] o, int x, int y, int w, int h, int id)
    {
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
                o[x + i, y + j] = id;
    }
}
