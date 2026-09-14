using System.Drawing.Imaging;

namespace TarkovOverlay;

/// <summary>What a single scan found, best candidate first.</summary>
public sealed class ScanResult
{
    public List<IconMatch> Candidates { get; init; } = new();

    /// <summary>The slot rectangle on screen, in physical pixels.</summary>
    public Rectangle SlotRect { get; init; }

    /// <summary>The crop we matched, kept so the popup can show it without any icon files.</summary>
    public Bitmap? Thumbnail { get; init; }

    public IconMatch? Best => Candidates.Count > 0 ? Candidates[0] : null;

    /// <summary>
    /// Candidates statistically tied with the best one. Roughly an eighth of
    /// Tarkov items share their grid image with another item, so a tie is a real
    /// answer ("it is one of these") rather than a failure.
    /// </summary>
    public List<IconMatch> Tied => TiedUpTo(8);

    /// <summary>
    /// Everything scoring level with the best match. Ties run large - one piece of
    /// Tarkov artwork can be shared by dozens of ids - so callers take a slice and
    /// report <see cref="TiedCount"/> for the rest.
    /// </summary>
    public List<IconMatch> TiedUpTo(int max)
    {
        var tied = new List<IconMatch>();
        if (Candidates.Count == 0) return tied;
        float cut = Candidates[0].Residual * 1.02f + 0.10f;
        var seen = new HashSet<string>();
        foreach (var c in Candidates)
        {
            if (c.Residual > cut || tied.Count >= max) break;
            if (seen.Add(c.ItemId)) tied.Add(c);
        }
        return tied;
    }

    /// <summary>How many candidates are tied for best, up to the ranking we kept.</summary>
    public int TiedCount => TiedUpTo(int.MaxValue).Count;
}

/// <summary>
/// Reads the item under the cursor straight off the screen.
///
/// Nothing here touches the game: it is a desktop screen grab plus arithmetic.
/// No process handle is opened, nothing is injected, and no game memory is read,
/// which is the same footing the rest of this overlay stands on.
///
/// The tricky part is finding the item's slot without knowing any of Tarkov's
/// colours (they change with rarity, patch, and UI theme). Two colour-free
/// observations carry the whole thing:
///
///   1. The inventory grid has a fixed 63px pitch, so the grid's phase can be
///      recovered by asking which offset mod 63 collects the most edge energy.
///   2. Tarkov draws an item's slot as one merged rectangle, so the grid lines
///      *inside* an item are absent. Walking outward while the boundary line is
///      missing gives the item's extent.
/// </summary>
public sealed class ScreenScan : IDisposable
{
    private const int Cell = IconIndex.Cell;   // 63
    private const int Thumb = IconIndex.Thumb; // 16

    // Enough room for the largest item we will hypothesise plus context either
    // side for the phase vote. 7 cells each way covers every stash item.
    private const int CaptureCells = 9;
    private const int CaptureSize = Cell * CaptureCells + 2;   // 569

    /// <summary>Largest item shape we will consider, in cells.</summary>
    private const int MaxCells = 6;

    /// <summary>
    /// Above this residual the shortlist is treated as suspect and we widen to
    /// every shape. Measured on synthetic stashes: correct slots score under ~5,
    /// while a mis-read shape lands at 7-10. Set between the two. Crossing it
    /// only costs time - the wider search still contains the correct slot.
    /// </summary>
    private const float FallbackResidual = 6f;

    /// <summary>
    /// Below this the first reading of the grid is taken at face value. Correct
    /// matches on a clean frame sit near 1-3; anything worse is worth a second
    /// opinion on where the grid actually is.
    /// </summary>
    private const float ConfidentResidual = 3.5f;

    /// <summary>
    /// How much better an alternative grid alignment must score to be believed.
    /// Four times better, which sounds severe and is not: a correct alignment
    /// lands near a residual of 1 where a misaligned one sits at 4 or worse, so
    /// the real thing clears this comfortably while noise never does. Measured
    /// across the sweep, looser values traded away up to 2.8 points of accuracy.
    /// </summary>
    private const float AlternativePhaseMargin = 0.25f;

    private Bitmap? _buffer;
    private Graphics? _gfx;
    private float[] _gray = Array.Empty<float>();
    private int _grayW, _grayH;
    private readonly float[] _obs = new float[IconIndex.Cells];
    private readonly List<IconMatch> _scratch = new(4096);

    public void Dispose()
    {
        _gfx?.Dispose();
        _buffer?.Dispose();
        _gfx = null;
        _buffer = null;
    }

    /// <summary>The last frame grabbed, and where on the desktop it came from.</summary>
    public Bitmap? LastCapture => _buffer;
    public Point LastCaptureOrigin { get; private set; }

    /// <summary>Drop the capture buffer. The overlay calls this when it hides.</summary>
    public void ReleaseMemory()
    {
        Dispose();
        _gray = Array.Empty<float>();
        _grayW = _grayH = 0;
    }

    /// <summary>
    /// Scan the item under <paramref name="cursor"/> (physical screen pixels).
    /// Returns null when the cursor is not over anything grid-like.
    /// </summary>
    public ScanResult? Scan(Point cursor, IconIndex index)
    {
        if (!index.Loaded) return null;

        var screen = Screen.FromPoint(cursor).Bounds;
        int half = CaptureSize / 2;
        int cx = Math.Clamp(cursor.X - half, screen.Left, Math.Max(screen.Left, screen.Right - CaptureSize));
        int cy = Math.Clamp(cursor.Y - half, screen.Top, Math.Max(screen.Top, screen.Bottom - CaptureSize));
        int cw = Math.Min(CaptureSize, screen.Right - cx);
        int ch = Math.Min(CaptureSize, screen.Bottom - cy);
        if (cw < Cell * 2 || ch < Cell * 2) return null;

        if (!Capture(cx, cy, cw, ch)) return null;
        LastCaptureOrigin = new Point(cx, cy);

        var found = Analyse(cursor.X - cx, cursor.Y - cy, index);
        if (found is null) return null;

        return new ScanResult
        {
            Candidates = found.Candidates,
            SlotRect = new Rectangle(cx + found.SlotRect.X, cy + found.SlotRect.Y,
                                     found.SlotRect.Width, found.SlotRect.Height),
            Thumbnail = found.Thumbnail,
        };
    }

    /// <summary>
    /// Same analysis against an image already in hand rather than the live screen.
    /// This is the seam the offline tests drive, so the shipped scan path and the
    /// tested scan path are the same code.
    /// </summary>
    public ScanResult? ScanImage(Bitmap image, Point cursor, IconIndex index)
    {
        if (!index.Loaded) return null;
        if (!LoadFrom(image)) return null;
        return Analyse(cursor.X, cursor.Y, index);
    }

    /// <summary>
    /// Score one slot whose position and shape are already known, skipping grid
    /// detection entirely. Used by the offline tests to tell a shape-detection
    /// failure apart from a matching failure.
    /// </summary>
    public List<IconMatch> ScoreKnownSlot(Bitmap image, Rectangle slot, IconIndex index, bool reload = true)
    {
        var outp = new List<IconMatch>();
        if (!index.Loaded) return outp;
        if (reload && !LoadFrom(image)) return outp;
        int gw = (slot.Width - 1) / Cell, gh = (slot.Height - 1) / Cell;
        if (gw < 1 || gh < 1) return outp;
        if (!Sample(slot.X, slot.Y, gw, gh)) return outp;
        index.Score(_obs, gw, gh, outp);
        outp.Sort(static (a, b) => a.Residual.CompareTo(b.Residual));
        return outp;
    }

    /// <summary>Everything after the pixels are in <c>_gray</c>. Rect is buffer-relative.</summary>
    private ScanResult? Analyse(int px, int py, IconIndex index)
    {
        // Two ways of scoring the phase vote, because they fail in opposite
        // conditions. The mean is sharper when the frame is all inventory, which
        // is the normal case. The median is what survives the capture spilling
        // onto other UI, where a few contaminated lines drag a mean-based vote
        // several pixels off.
        GridPhases(horizontal: true, py, out int sumX, out int medX);
        GridPhases(horizontal: false, px, out int sumY, out int medY);
        if (sumX < 0) sumX = medX;
        if (sumY < 0) sumY = medY;
        if (sumX < 0 || sumY < 0) return null;

        LastTrace.Clear();
        LastPhases = (sumX, medX, sumY, medY);
        if (Diagnostics) LastLocalPhase = (LocalPhase(true, px, py), LocalPhase(false, py, px));

        // The mean vote is the primary reading and is right almost always, so it
        // is taken at face value when its match is convincing. Only a poor score
        // buys a second opinion: the median vote, and a locally refined phase.
        // Applying those unconditionally measured *worse* - around a large merged
        // item, whose interior grid lines Tarkov does not draw, refinement can
        // pull a correct phase off by one.
        int cellX = Origin(px, sumX), cellY = Origin(py, sumY);
        var best = Evaluate(cellX, cellY, index);
        if (Diagnostics) LastTrace.Add($"phase x={sumX} y={sumY} -> {best.GW}x{best.GH} r={best.Resid:F2}");

        // Second-guess the phase only on actual evidence that it is shaky: the two
        // votes disagreeing, or a match this poor. When mean and median agree,
        // there is nothing to arbitrate, and trying anyway measured worse - a
        // wrong alignment can always find *some* 1x1 out of two thousand that
        // fits, and letting it compete cost 2.8 points of accuracy for nothing.
        bool disagree = (medX >= 0 && medX != sumX) || (medY >= 0 && medY != sumY);
        if (disagree || best.Resid > ConfidentResidual)
        {
            Span<int> xs = stackalloc int[5];
            Span<int> ys = stackalloc int[5];
            int nx = Collect(xs, sumX, medX, RefinePhase(sumX, true, py, px),
                             RefinePhase(medX, true, py, px), LocalPhase(true, px, py));
            int ny = Collect(ys, sumY, medY, RefinePhase(sumY, false, px, py),
                             RefinePhase(medY, false, px, py), LocalPhase(false, py, px));

            for (int a = 0; a < nx; a++)
                for (int b = 0; b < ny; b++)
                {
                    if (xs[a] == sumX && ys[b] == sumY) continue;
                    int cx2 = Origin(px, xs[a]), cy2 = Origin(py, ys[b]);

                    // Each alternative gets the *same* full evaluation, shape
                    // search included. Judging one on the cheap pass and another
                    // on the thorough one is not a comparison: a correct phase
                    // whose shortlist happened to miss the shape would lose to a
                    // wrong phase's plausible-looking 1x1.
                    var hit = Evaluate(cx2, cy2, index);
                    if (Diagnostics) LastTrace.Add($"phase x={xs[a]} y={ys[b]} -> {hit.GW}x{hit.GH} r={hit.Resid:F2}");

                    // Must win decisively, not merely edge ahead. A misaligned
                    // crop can always find *some* icon out of two thousand that
                    // fits it tolerably, so a narrow win is noise; a genuinely
                    // correct alignment scores around a 1 against the incumbent's
                    // 4 or worse, and clears this easily.
                    if (hit.Resid < best.Resid * AlternativePhaseMargin)
                    { best = hit; cellX = cx2; cellY = cy2; }
                }
        }

        if (best.Rect.IsEmpty) return null;

        // Re-score the winning slot on its own, so the candidate list is a ranking
        // within one shape rather than a mix of shapes that cannot be compared.
        _scratch.Clear();
        if (!Sample(best.Rect.X, best.Rect.Y, best.GW, best.GH)) return null;
        index.Score(_obs, best.GW, best.GH, _scratch);
        if (_scratch.Count == 0) return null;
        _scratch.Sort(static (a, b) => a.Residual.CompareTo(b.Residual));

        return new ScanResult
        {
            Candidates = _scratch.GetRange(0, Math.Min(40, _scratch.Count)),
            SlotRect = best.Rect,
            Thumbnail = CropThumbnail(best.Rect),
        };
    }

    private readonly List<(int X, int Y, int W, int H)> _wide = new(160);

    /// <summary>
    /// Best slot for one grid alignment: the shapes the grid lines suggest, then
    /// every shape if that reading looks wrong.
    /// </summary>
    private (Rectangle Rect, int GW, int GH, float Resid) Evaluate(int cellX, int cellY, IconIndex index)
    {
        var best = BestOver(Shortlist(cellX, cellY, index), index);

        // The line reading can be wrong - a faint border, or artwork with a
        // straight edge landing on a cell boundary. A mediocre score is the tell,
        // so widen to every shape and anchor around this cell.
        if (best.Resid > FallbackResidual)
        {
            _wide.Clear();
            foreach (var (w, h) in index.Shapes)
            {
                if (w > MaxCells || h > MaxCells) continue;
                for (int dy = 0; dy < h; dy++)
                    for (int dx = 0; dx < w; dx++)
                        _wide.Add((cellX - dx * Cell, cellY - dy * Cell, w, h));
            }
            var wide = BestOver(_wide, index);

            // Only overrule the grid lines on a decisive improvement. A large,
            // mostly-transparent icon can drape over almost anything once the
            // background term absorbs the difference, so a hair's-breadth win
            // from a bigger shape is not evidence.
            if (wide.Resid < best.Resid * 0.7f) best = wide;
        }
        return best;
    }

    private readonly List<IconMatch> _probe = new(4096);

    /// <summary>Cheapest-residual hypothesis out of a set of candidate slots.</summary>
    private (Rectangle Rect, int GW, int GH, float Resid) BestOver(
        List<(int X, int Y, int W, int H)> hypotheses, IconIndex index)
    {
        Rectangle rect = Rectangle.Empty;
        int bw = 0, bh = 0;
        float bestResid = float.MaxValue;

        foreach (var (rx, ry, gw, gh) in hypotheses)
        {
            if (!Sample(rx, ry, gw, gh)) continue;
            _probe.Clear();
            index.Score(_obs, gw, gh, _probe);
            for (int i = 0; i < _probe.Count; i++)
                if (_probe[i].Residual < bestResid)
                {
                    bestResid = _probe[i].Residual;
                    rect = new Rectangle(rx, ry, Cell * gw + 1, Cell * gh + 1);
                    bw = gw; bh = gh;
                }
        }
        return (rect, bw, bh, bestResid);
    }

    /// <summary>Pull pixels from a bitmap instead of the screen (tests, and stills).</summary>
    private bool LoadFrom(Bitmap image)
    {
        int w = image.Width, h = image.Height;
        if (w < Cell * 3 || h < Cell * 3) return false;

        if (_buffer is null || _buffer.Width != w || _buffer.Height != h)
        {
            _gfx?.Dispose();
            _buffer?.Dispose();
            _buffer = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            _gfx = Graphics.FromImage(_buffer);
        }
        _gfx!.DrawImage(image, 0, 0, w, h);
        return ReadGray(w, h);
    }

    private bool Capture(int x, int y, int w, int h)
    {
        try
        {
            if (_buffer is null || _buffer.Width != w || _buffer.Height != h)
            {
                _gfx?.Dispose();
                _buffer?.Dispose();
                _buffer = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                _gfx = Graphics.FromImage(_buffer);
            }
            _gfx!.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        }
        catch
        {
            // A locked desktop or a protected window refuses the blit. Not fatal.
            return false;
        }

        return ReadGray(w, h);
    }

    private bool ReadGray(int w, int h)
    {
        if (_gray.Length < w * h) _gray = new float[w * h];
        _grayW = w; _grayH = h;

        var data = _buffer!.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            unsafe
            {
                for (int row = 0; row < h; row++)
                {
                    byte* p = (byte*)data.Scan0 + row * data.Stride;
                    int o = row * w;
                    for (int col = 0; col < w; col++, p += 4)
                        _gray[o + col] = p[2] * 0.299f + p[1] * 0.587f + p[0] * 0.114f;
                }
            }
        }
        finally { _buffer.UnlockBits(data); }
        return true;
    }

    /// <summary>
    /// Recover the grid's offset by voting: every column (or row) contributes its
    /// edge energy to the phase bucket it falls in, and grid lines - being the
    /// only thing repeating at exactly 63px - win. Needs no colour constants.
    /// Returns -1 when nothing periodic is there, i.e. we are not on an inventory.
    /// </summary>
    private float[] _resp = Array.Empty<float>();

    /// <summary>Buffer coordinate of the left/top edge of the cell holding <paramref name="at"/>.</summary>
    private static int Origin(int at, int phase) =>
        phase + Cell * (int)Math.Floor((at - phase) / (double)Cell);

    /// <summary>Mean line response along one row/column, within a band.</summary>
    private float LineScore(int at, bool horizontal, int lo, int hi)
    {
        int n = horizontal ? _grayW : _grayH;
        if (at < 1 || at >= n - 1 || hi <= lo) return 0f;
        float sum = 0f;
        if (horizontal)
            for (int j = lo; j < hi; j++)
            {
                int o = j * _grayW;
                sum += MathF.Abs(2f * _gray[o + at] - _gray[o + at - 1] - _gray[o + at + 1]);
            }
        else
            for (int j = lo; j < hi; j++)
                sum += MathF.Abs(2f * _gray[at * _grayW + j]
                                 - _gray[(at - 1) * _grayW + j]
                                 - _gray[(at + 1) * _grayW + j]);
        return sum / (hi - lo);
    }

    /// <summary>
    /// Find the grid alignment from the cursor's own cell rather than the frame
    /// as a whole: try all 63 offsets and keep the one whose cell boundaries are
    /// most line-like, measured in a band one cell wide around the cursor.
    ///
    /// This is what a real stash needs. The frame-wide votes assume the grid is
    /// the dominant repeating thing on screen, which holds for an open stash page
    /// and fails completely once Tarkov is showing what it actually shows: nested
    /// container windows, a search panel, and a name tooltip, each with borders of
    /// their own that have nothing to do with the 63px grid. Measured on a real
    /// capture, the frame-wide vote was 9px out in Y and the match was garbage.
    ///
    /// Scoring takes the best two boundaries rather than all of them, because
    /// Tarkov merges an item's cells and draws no line inside them - so some
    /// boundaries around the cursor genuinely are not there.
    /// </summary>
    private int LocalPhase(bool horizontal, int cursorAlong, int cursorAcross)
    {
        int m = horizontal ? _grayH : _grayW;
        int lo = Math.Max(0, cursorAcross - Cell / 2);
        int hi = Math.Min(m, cursorAcross + Cell / 2);
        if (hi - lo < Cell / 4) return -1;

        int best = -1;
        float bestScore = 0f, total = 0f;

        for (int o = 0; o < Cell; o++)
        {
            int edge = cursorAlong - o;
            float b1 = 0f, b2 = 0f;
            for (int k = -2; k <= 3; k++)
            {
                float v = LineScore(edge + k * Cell, horizontal, lo, hi);
                if (v > b1) { b2 = b1; b1 = v; }
                else if (v > b2) { b2 = v; }
            }
            float score = b1 + b2;
            total += score;
            if (score > bestScore) { bestScore = score; best = ((edge % Cell) + Cell) % Cell; }
        }

        // No confidence gate here on purpose. This is only ever offered as an
        // extra candidate, and an alternative alignment still has to explain an
        // icon four times better before it is believed - so a weak reading costs
        // one more hypothesis and nothing else. Gating it at 25% above the mean
        // rejected it on exactly the cluttered real frames it exists to rescue,
        // where plenty of offsets look line-like.
        return total > 0f ? best : -1;
    }

    /// <summary>Gather the distinct, valid phase candidates into <paramref name="into"/>.</summary>
    private static int Collect(Span<int> into, int a, int b, int c, int d, int e)
    {
        int n = 0;
        foreach (int v in stackalloc int[5] { a, b, c, d, e })
        {
            if (v < 0) continue;
            bool dup = false;
            for (int i = 0; i < n; i++) if (into[i] == v) { dup = true; break; }
            if (!dup) into[n++] = v;
        }
        return n;
    }

    /// <summary>
    /// Nudge a phase estimate by up to two pixels, judged only on the cells
    /// immediately around the cursor.
    ///
    /// The global vote has to look at a wide area to find the period at all, and
    /// that width is exactly what lets far-away UI pull it off by a pixel. But a
    /// single pixel of misalignment is expensive: it pushed the correct match
    /// from a residual of 1.1 to 8.2 and handed the answer to a wrong slot. The
    /// neighbourhood of the cursor, on the other hand, is inventory whenever the
    /// scan is meaningful at all - so resolving the last pixel there is both
    /// cheap and safe.
    /// </summary>
    private int RefinePhase(int phase, bool horizontal, int cursorAcross, int cursorAlong)
    {
        if (phase < 0) return phase;

        int n = horizontal ? _grayW : _grayH;
        int m = horizontal ? _grayH : _grayW;

        int lo = Math.Max(0, cursorAcross - (Cell * 3) / 2);
        int hi = Math.Min(m, cursorAcross + (Cell * 3) / 2);
        if (hi - lo < Cell / 2) return phase;

        int best = phase;
        float bestScore = float.MinValue;

        for (int d = -2; d <= 2; d++)
        {
            int p = ((phase + d) % Cell + Cell) % Cell;
            float score = 0f;
            int lines = 0;

            // Only the two grid lines either side of the cursor.
            int first = p + Cell * (int)Math.Floor((cursorAlong - Cell * 2 - p) / (double)Cell);
            for (int i = first; i <= cursorAlong + Cell * 2; i += Cell)
            {
                if (i < 1 || i >= n - 1) continue;
                float sum = 0f;
                if (horizontal)
                    for (int j = lo; j < hi; j++)
                    {
                        int o = j * _grayW;
                        sum += MathF.Abs(2f * _gray[o + i] - _gray[o + i - 1] - _gray[o + i + 1]);
                    }
                else
                    for (int j = lo; j < hi; j++)
                        sum += MathF.Abs(2f * _gray[i * _grayW + j]
                                         - _gray[(i - 1) * _grayW + j]
                                         - _gray[(i + 1) * _grayW + j]);
                score += sum;
                lines++;
            }

            if (lines == 0) continue;
            score /= lines;
            if (score > bestScore) { bestScore = score; best = p; }
        }
        return best;
    }

    /// <summary>Last phase vote, for diagnostics. (sumX, medX, sumY, medY).</summary>
    public (int SumX, int MedX, int SumY, int MedY) LastPhases { get; private set; }

    /// <summary>Last local cell-boundary reading, for diagnostics. (x, y).</summary>
    public (int X, int Y) LastLocalPhase { get; private set; }

    /// <summary>
    /// Record which alignments were tried. Off by default: hover mode scans often
    /// enough that a few strings per scan is litter the GC does not need.
    /// </summary>
    public static bool Diagnostics { get; set; }

    /// <summary>Residual of each phase pair tried. Only filled when <see cref="Diagnostics"/>.</summary>
    public List<string> LastTrace { get; } = new();

    /// <summary>
    /// Recover the grid's offset mod 63.
    ///
    /// Two things make this hold up. First it is a *line* response, not an edge
    /// response: a plain gradient fires on both sides of a 1px line, leaving the
    /// phase ambiguous by a pixel, while |2*g(i) - g(i-1) - g(i+1)| peaks on the
    /// line itself. Second it scores each candidate phase by the *median* of its
    /// lines rather than the sum. That matters because the capture is larger than
    /// any item, so near a panel edge it spills onto other UI, and anything with
    /// its own regular structure - a wall of text was the case that caught this -
    /// spikes a few rows hard enough to drag a sum-based vote several pixels off.
    /// A real grid is lit up at every one of its lines; an impostor is not, and
    /// the median throws it out.
    ///
    /// Returns -1 when nothing periodic is there, i.e. this is not an inventory.
    /// </summary>
    private void GridPhases(bool horizontal, int cursorAcross, out int bySum, out int byMedian)
    {
        bySum = -1;
        byMedian = -1;

        int n = horizontal ? _grayW : _grayH;
        int m = horizontal ? _grayH : _grayW;
        if (n < Cell * 3) return;

        // Measure across a band around the cursor rather than the whole frame,
        // so distant UI cannot contribute at all.
        int lo = Math.Max(0, cursorAcross - Cell * 2);
        int hi = Math.Min(m, cursorAcross + Cell * 2);
        if (hi - lo < Cell) { lo = 0; hi = m; }
        int span = hi - lo;

        if (_resp.Length < n) _resp = new float[n];
        for (int i = 1; i < n - 1; i++)
        {
            float sum = 0f;
            if (horizontal)
                for (int j = lo; j < hi; j++)
                {
                    int o = j * _grayW;
                    sum += MathF.Abs(2f * _gray[o + i] - _gray[o + i - 1] - _gray[o + i + 1]);
                }
            else
                for (int j = lo; j < hi; j++)
                    sum += MathF.Abs(2f * _gray[i * _grayW + j]
                                     - _gray[(i - 1) * _grayW + j]
                                     - _gray[(i + 1) * _grayW + j]);
            _resp[i] = sum / span;
        }

        Span<float> line = stackalloc float[n / Cell + 2];
        int bestSum = -1, bestMed = -1;
        float topSum = 0f, topMed = 0f, totalSum = 0f, totalMed = 0f;

        for (int p = 0; p < Cell; p++)
        {
            int k = 0;
            float sum = 0f;
            for (int i = p; i < n - 1; i += Cell)
                if (i >= 1) { line[k++] = _resp[i]; sum += _resp[i]; }
            if (k < 3) continue;
            sum /= k;

            // Median of this phase's lines. Insertion sort: k is at most ~10.
            var slice = line[..k];
            for (int a = 1; a < k; a++)
            {
                float v = slice[a];
                int b = a - 1;
                while (b >= 0 && slice[b] > v) { slice[b + 1] = slice[b]; b--; }
                slice[b + 1] = v;
            }
            float median = k % 2 == 1 ? slice[k / 2] : (slice[k / 2 - 1] + slice[k / 2]) * 0.5f;

            totalSum += sum;
            totalMed += median;
            if (sum > topSum) { topSum = sum; bestSum = p; }
            if (median > topMed) { topMed = median; bestMed = p; }
        }

        // A real grid stands well clear of an average offset. Without that margin
        // we are not looking at an inventory at all.
        float meanSum = totalSum / Cell, meanMed = totalMed / Cell;
        if (meanSum > 0f && topSum >= meanSum * 1.6f) bySum = bestSum;
        if (meanMed > 0f && topMed >= meanMed * 1.6f) byMedian = bestMed;
    }

    /// <summary>
    /// Item shapes worth testing, from where the grid lines actually are. Tarkov
    /// merges an item's cells into one rectangle, so a missing boundary line means
    /// the item continues in that direction.
    /// </summary>
    private List<(int X, int Y, int W, int H)> Shortlist(int cellX, int cellY, IconIndex index)
    {
        int left = 0, right = 0, up = 0, down = 0;
        while (left < MaxCells - 1 && !LineAt(cellX - left * Cell, cellY, vertical: true)) left++;
        while (right < MaxCells - 1 && !LineAt(cellX + (right + 1) * Cell, cellY, vertical: true)) right++;
        while (up < MaxCells - 1 && !LineAt(cellY - up * Cell, cellX, vertical: false)) up++;
        while (down < MaxCells - 1 && !LineAt(cellY + (down + 1) * Cell, cellX, vertical: false)) down++;

        var list = new List<(int, int, int, int)>();
        void Add(int dx, int dy, int w, int h)
        {
            if (w < 1 || h < 1 || w > MaxCells || h > MaxCells) return;
            // The cursor must fall inside the slot, or we are scoring pixels the
            // user never pointed at.
            if (dx < 0 || dy < 0 || dx >= w || dy >= h) return;
            if (!index.HasShape(w, h)) return;
            var t = (cellX - dx * Cell, cellY - dy * Cell, w, h);
            if (!list.Contains(t)) list.Add(t);
        }

        // Primary reading, then a few near misses in case a line was faint.
        //
        // The anchor and the extent have to agree: a slot one cell tall must have
        // its top edge in the cursor's own row, so dy is 0 there, not `up`. Left
        // as `up` it anchored hypotheses whole cells away and scored slots that
        // did not contain the cursor at all - which is exactly how a real capture
        // came back matching something 250px above the item being pointed at.
        Add(left, up, left + right + 1, up + down + 1);
        Add(0, 0, 1, 1);
        Add(left, 0, left + right + 1, 1);
        Add(0, up, 1, up + down + 1);
        if (left + right >= 1) Add(Math.Max(0, left - 1), up, left + right, up + down + 1);
        if (up + down >= 1) Add(left, Math.Max(0, up - 1), left + right + 1, up + down);
        return list;
    }

    /// <summary>
    /// Is a grid line drawn at this boundary? Compares the boundary's own gradient
    /// against the two lines just inside the cell, so it is a relative test and
    /// needs no absolute colour.
    /// </summary>
    private bool LineAt(int at, int across, bool vertical)
    {
        int span = Cell - 8;
        int a0 = across + 4;
        if (vertical)
        {
            if (at < 2 || at >= _grayW - 2 || a0 < 0 || a0 + span >= _grayH) return true;
            float line = 0f, inside = 0f;
            for (int j = a0; j < a0 + span; j++)
            {
                line += MathF.Abs(_gray[j * _grayW + at] - _gray[j * _grayW + at - 1]);
                inside += MathF.Abs(_gray[j * _grayW + at + 3] - _gray[j * _grayW + at + 2]);
            }
            return line > inside * 2f + span * 1.5f;
        }
        else
        {
            if (at < 2 || at >= _grayH - 2 || a0 < 0 || a0 + span >= _grayW) return true;
            float line = 0f, inside = 0f;
            for (int j = a0; j < a0 + span; j++)
            {
                line += MathF.Abs(_gray[at * _grayW + j] - _gray[(at - 1) * _grayW + j]);
                inside += MathF.Abs(_gray[(at + 3) * _grayW + j] - _gray[(at + 2) * _grayW + j]);
            }
            return line > inside * 2f + span * 1.5f;
        }
    }

    // Fractional box-filter footprints, rebuilt only when the slot size changes.
    private readonly float[] _wx = new float[Thumb * (Cell * 8 + 2)];
    private readonly int[] _x0 = new int[Thumb];
    private readonly int[] _xn = new int[Thumb];
    private readonly float[] _wy = new float[Thumb * (Cell * 8 + 2)];
    private readonly int[] _y0 = new int[Thumb];
    private readonly int[] _yn = new int[Thumb];
    private int _fpW = -1, _fpH = -1;

    /// <summary>
    /// Area-average the slot rectangle down to the 16x16 the index stores.
    ///
    /// This has to be a *fractional* box filter, matching PIL's BOX resize that
    /// tools/build_icon_index.py uses to build the index. A slot is 63*n+1 px, so
    /// only the 1x1 case divides evenly by 16; truncating to integer pixel
    /// boundaries anywhere else quietly shifts the observation away from every
    /// reference thumbnail and costs a chunk of accuracy.
    /// </summary>
    private bool Sample(int rx, int ry, int gw, int gh)
    {
        // Interior only: skip the 1px grid border on each side, exactly as
        // tools/build_icon_index.py crops the reference icons.
        int w = Cell * gw - 1, h = Cell * gh - 1;
        rx += 1; ry += 1;
        if (rx < 0 || ry < 0 || rx + w > _grayW || ry + h > _grayH) return false;

        if (w != _fpW) { BuildFootprint(w, _wx, _x0, _xn); _fpW = w; }
        if (h != _fpH) { BuildFootprint(h, _wy, _y0, _yn); _fpH = h; }

        int stride = Cell * 8 + 2;
        for (int ty = 0; ty < Thumb; ty++)
        {
            int yb = ty * stride;
            for (int tx = 0; tx < Thumb; tx++)
            {
                int xb = tx * stride;
                float sum = 0f;
                for (int j = 0; j < _yn[ty]; j++)
                {
                    float wyj = _wy[yb + j];
                    if (wyj <= 0f) continue;
                    int row = (ry + _y0[ty] + j) * _grayW + rx + _x0[tx];
                    float rowSum = 0f;
                    for (int i = 0; i < _xn[tx]; i++) rowSum += _wx[xb + i] * _gray[row + i];
                    sum += wyj * rowSum;
                }
                _obs[ty * Thumb + tx] = sum * (Thumb / (float)w) * (Thumb / (float)h);
            }
        }
        return true;
    }

    /// <summary>Per-output-pixel source span and weights for a 1-D box filter.</summary>
    private static void BuildFootprint(int size, float[] weights, int[] starts, int[] counts)
    {
        int stride = Cell * 8 + 2;
        double step = size / (double)Thumb;
        for (int t = 0; t < Thumb; t++)
        {
            double a = t * step, b = (t + 1) * step;
            int i0 = (int)Math.Floor(a), i1 = (int)Math.Ceiling(b);
            if (i1 > size) i1 = size;
            starts[t] = i0;
            counts[t] = i1 - i0;
            int baseOff = t * stride;
            for (int i = i0; i < i1; i++)
                weights[baseOff + i - i0] = (float)(Math.Min(b, i + 1) - Math.Max(a, i));
        }
    }

    private Bitmap? CropThumbnail(Rectangle r)
    {
        if (_buffer is null) return null;
        var clipped = Rectangle.Intersect(r, new Rectangle(0, 0, _buffer.Width, _buffer.Height));
        if (clipped.Width <= 0 || clipped.Height <= 0) return null;
        try { return _buffer.Clone(clipped, PixelFormat.Format32bppRgb); }
        catch { return null; }
    }
}
