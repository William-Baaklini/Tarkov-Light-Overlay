using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace TarkovOverlay;

public enum NoteTool { Pan, Draw, Text, Erase }

/// <summary>
/// Tiled pan/zoom viewer. Only ever draws tiles from the pyramid level closest
/// to the current zoom, so RAM stays flat regardless of how large the map is.
///
/// Also hosts the user's notes: freehand strokes and pinned text labels, kept
/// in map pixels so they stay glued to the terrain through every zoom and pan.
/// </summary>
public sealed class MapView : Control
{
    private const double MaxScale = 3.0;      // 300% of native pixels
    private const double ZoomStep = 1.18;

    private readonly TileCache _cache;
    private readonly System.Windows.Forms.Timer _repaint;
    private MapMeta? _map;

    private double _scale = 1.0;              // screen px per full-resolution image px
    private double _offX, _offY;              // full-res image coord at the viewport origin

    private bool _dragging;
    private Point _dragOrigin;
    private double _dragOffX, _dragOffY;
    private volatile bool _dirty;

    // True while the view is tracking the window rather than a view the user
    // chose. It survives resizes, so a map opened fitted stays fitted while you
    // drag the overlay bigger, and is only cleared once you zoom or pan.
    private bool _fitTracking;

    // ---- notes state -------------------------------------------------------

    private static readonly Font LabelFont = new("Segoe UI", 9f, FontStyle.Bold);
    private const int LabelMaxWidth = 240;
    private const int LabelPad = 6;
    private const int PinRadius = 5;
    private const int LabelGap = 9;           // pin edge to label box

    private MapNotes _notes = new();
    private string _notesMapId = "";
    private NoteTool _tool = NoteTool.Pan;
    private Color _penColor = Color.FromArgb(232, 74, 60);
    private float _penWidth = 4.5f;
    private bool _notesVisible = true;
    private readonly System.Windows.Forms.Timer _saveTimer;

    private NoteStroke? _activeStroke;        // being drawn right now
    private Point _lastStrokePt;
    private bool _erasing;                    // left button held in Erase mode
    private object? _hover;                   // NoteStroke or NoteText under the cursor

    private NoteText? _dragNote;
    private Point _dragNoteMouse;
    private double _dragNoteX, _dragNoteY;
    private bool _dragNoteMoved;

    private TextBox? _editor;
    private NoteText? _editing;
    private bool _editingIsNew;
    private bool _closingEditor;

    private readonly List<(Action Undo, Action Redo)> _undo = new();
    private readonly List<(Action Undo, Action Redo)> _redo = new();

    public MapMeta? Map => _map;
    public event Action? ViewChanged;

    /// <summary>Fires whenever notes, the tool, or undo/redo availability change.</summary>
    public event Action? NotesChanged;

    public MapView(int cacheTiles)
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = true;
        BackColor = Color.FromArgb(18, 19, 22);
        SetStyle(ControlStyles.Selectable | ControlStyles.OptimizedDoubleBuffer, true);

        _cache = new TileCache(cacheTiles);
        _cache.TileLoaded += () => _dirty = true;

        // Tiles arrive on a worker thread; coalesce repaints to ~25 fps rather
        // than invalidating once per tile.
        _repaint = new System.Windows.Forms.Timer { Interval = 40 };
        _repaint.Tick += (_, _) =>
        {
            if (!_dirty) return;
            _dirty = false;
            Invalidate();
        };
        _repaint.Start();

        // Notes autosave shortly after the last change, and always on hide.
        _saveTimer = new System.Windows.Forms.Timer { Interval = 700 };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); FlushNotes(); };
    }

    public void SetCacheSize(int tiles) => _cache.SetCapacity(tiles);

    /// <summary>Drops every decoded tile. Used when the overlay is hidden.</summary>
    public void ReleaseMemory() => _cache.Purge();

    public void ShowMap(MapMeta? map, SavedView? restore)
    {
        // Finish whatever was happening on the previous map before swapping.
        CloseEditor(commit: true);
        EndStroke();
        FlushNotes();

        _map = map;
        _cache.SetMap(map);

        _undo.Clear();
        _redo.Clear();
        _hover = null;
        _notesMapId = map?.Id ?? "";
        _notes = map is null ? new MapNotes() : NoteStore.Load(map.Id);
        NotesChanged?.Invoke();

        if (map is null) { Invalidate(); return; }

        if (restore is not null && restore.Scale > 0)
        {
            _fitTracking = false;
            _scale = restore.Scale;
            _offX = restore.OffsetX;
            _offY = restore.OffsetY;
            ClampView();
        }
        else
        {
            FitToWindow();
        }
        Invalidate();
    }

    public SavedView CurrentView() => new() { Scale = _scale, OffsetX = _offX, OffsetY = _offY };

    public void FitToWindow()
    {
        _fitTracking = true;
        if (!ApplyFit()) return;
        ViewChanged?.Invoke();
    }

    /// <summary>
    /// Recomputes the fit. Returns false while the control has no meaningful
    /// size yet, which is normal: a map is often selected before first layout.
    /// </summary>
    private bool ApplyFit()
    {
        if (_map is null || ClientSize.Width < 2 || ClientSize.Height < 2) return false;
        _scale = Math.Min(
            ClientSize.Width / (double)_map.Width,
            ClientSize.Height / (double)_map.Height);
        ClampView();
        PositionEditor();
        Invalidate();
        return true;
    }

    public void ZoomTo(double factor) =>
        ZoomAt(factor, new Point(ClientSize.Width / 2, ClientSize.Height / 2));

    private double FitScale
    {
        get
        {
            if (_map is null || ClientSize.Width < 2 || ClientSize.Height < 2) return 0.01;
            return Math.Min(
                ClientSize.Width / (double)_map.Width,
                ClientSize.Height / (double)_map.Height);
        }
    }

    // A little breathing room past fit-to-window, but no further.
    private double MinScale => FitScale * 0.9;

    private void ZoomAt(double factor, Point anchor)
    {
        if (_map is null) return;
        var target = Math.Clamp(_scale * factor, MinScale, MaxScale);
        if (Math.Abs(target - _scale) < 1e-9) return;

        // Keep the image point under the cursor pinned to the cursor.
        double imgX = _offX + anchor.X / _scale;
        double imgY = _offY + anchor.Y / _scale;
        _scale = target;
        _offX = imgX - anchor.X / _scale;
        _offY = imgY - anchor.Y / _scale;

        _fitTracking = false;   // the user has chosen a zoom; stop tracking the window
        ClampView();
        PositionEditor();
        _cache.ClearPending();
        Invalidate();
        ViewChanged?.Invoke();
    }

    /// <summary>Keeps the map from being dragged off into empty space.</summary>
    private void ClampView()
    {
        if (_map is null) return;
        double viewW = ClientSize.Width / _scale;
        double viewH = ClientSize.Height / _scale;
        _offX = viewW >= _map.Width
            ? (_map.Width - viewW) / 2
            : Math.Clamp(_offX, 0, _map.Width - viewW);
        _offY = viewH >= _map.Height
            ? (_map.Height - viewH) / 2
            : Math.Clamp(_offY, 0, _map.Height - viewH);
    }

    // ---- coordinate helpers ------------------------------------------------

    private PointF ToScreen(double ix, double iy) =>
        new((float)((ix - _offX) * _scale), (float)((iy - _offY) * _scale));

    private (double X, double Y) ToImage(Point p) =>
        (_offX + p.X / _scale, _offY + p.Y / _scale);

    // ---- notes: public surface ---------------------------------------------

    public NoteTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            CloseEditor(commit: true);
            EndStroke();
            _erasing = false;
            _tool = value;
            // Picking a note tool while notes are hidden would be baffling.
            if (value != NoteTool.Pan) _notesVisible = true;
            _hover = null;
            UpdateCursor();
            Invalidate();
            NotesChanged?.Invoke();
        }
    }

    public Color PenColor { get => _penColor; set => _penColor = value; }
    public float PenWidth { get => _penWidth; set => _penWidth = Math.Clamp(value, 1f, 24f); }

    public bool NotesVisible
    {
        get => _notesVisible;
        set
        {
            if (_notesVisible == value) return;
            _notesVisible = value;
            if (!value) { CloseEditor(commit: true); EndStroke(); _hover = null; }
            Invalidate();
            NotesChanged?.Invoke();
        }
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool HasNotes => !_notes.IsEmpty;
    public bool IsEditingText => _editor is not null;

    public void Undo()
    {
        CloseEditor(commit: true);
        if (_undo.Count == 0) return;
        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        entry.Undo();
        _redo.Add(entry);
        _hover = null;
        Changed();
    }

    public void Redo()
    {
        CloseEditor(commit: true);
        if (_redo.Count == 0) return;
        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        entry.Redo();
        _undo.Add(entry);
        _hover = null;
        Changed();
    }

    /// <summary>Removes everything on the current map. Undoable; the caller confirms.</summary>
    public void ClearNotes()
    {
        CloseEditor(commit: false);
        EndStroke();
        if (_notes.IsEmpty) return;
        var strokes = _notes.Strokes.ToList();
        var texts = _notes.Texts.ToList();
        _notes.Strokes.Clear();
        _notes.Texts.Clear();
        _hover = null;
        Push(
            undo: () => { _notes.Strokes.AddRange(strokes); _notes.Texts.AddRange(texts); },
            redo: () => { _notes.Strokes.Clear(); _notes.Texts.Clear(); });
    }

    /// <summary>Cancels an open text editor. Returns true if there was one to cancel.</summary>
    public bool CancelEditing()
    {
        if (_editor is null) return false;
        CloseEditor(commit: false);
        Focus();
        return true;
    }

    /// <summary>Commits an open text editor, if any.</summary>
    public void CommitEditing() => CloseEditor(commit: true);

    /// <summary>Writes the current map's notes to disk now.</summary>
    public void FlushNotes()
    {
        _saveTimer.Stop();
        if (_notesMapId.Length == 0) return;
        NoteStore.Save(_notesMapId, _notes);
    }

    // ---- notes: change tracking --------------------------------------------

    private void Push(Action undo, Action redo)
    {
        _undo.Add((undo, redo));
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
        Changed();
    }

    private void Changed()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
        Invalidate();
        NotesChanged?.Invoke();
    }

    private void RemoveItem(object item)
    {
        switch (item)
        {
            case NoteStroke s:
            {
                int idx = _notes.Strokes.IndexOf(s);
                if (idx < 0) return;
                _notes.Strokes.RemoveAt(idx);
                Push(
                    undo: () => _notes.Strokes.Insert(Math.Min(idx, _notes.Strokes.Count), s),
                    redo: () => _notes.Strokes.Remove(s));
                break;
            }
            case NoteText t:
            {
                int idx = _notes.Texts.IndexOf(t);
                if (idx < 0) return;
                _notes.Texts.RemoveAt(idx);
                Push(
                    undo: () => _notes.Texts.Insert(Math.Min(idx, _notes.Texts.Count), t),
                    redo: () => _notes.Texts.Remove(t));
                break;
            }
        }
        if (ReferenceEquals(_hover, item)) _hover = null;
    }

    // ---- notes: strokes ----------------------------------------------------

    private void BeginStroke(Point p)
    {
        CloseEditor(commit: true);
        _activeStroke = new NoteStroke { Color = Hex(_penColor), Width = _penWidth };
        AddStrokePoint(p, force: true);
        Capture = true;
    }

    private void AddStrokePoint(Point p, bool force)
    {
        if (_activeStroke is null) return;
        if (!force)
        {
            int dx = p.X - _lastStrokePt.X, dy = p.Y - _lastStrokePt.Y;
            if (dx * dx + dy * dy < 4) return;     // under 2 px: ignore jitter
        }
        var (ix, iy) = ToImage(p);
        _activeStroke.Points.Add(Math.Round(ix, 1));
        _activeStroke.Points.Add(Math.Round(iy, 1));
        _activeStroke.InvalidateBounds();
        _lastStrokePt = p;
        Invalidate();
    }

    private void EndStroke()
    {
        var s = _activeStroke;
        if (s is null) return;
        _activeStroke = null;
        Capture = false;
        if (s.Points.Count < 2) { Invalidate(); return; }
        _notes.Strokes.Add(s);
        Push(undo: () => _notes.Strokes.Remove(s), redo: () => _notes.Strokes.Add(s));
    }

    // ---- notes: text -------------------------------------------------------

    private void CreateTextNote(Point p)
    {
        var (ix, iy) = ToImage(p);
        var t = new NoteText { X = Math.Round(ix, 1), Y = Math.Round(iy, 1), Color = Hex(_penColor) };
        // Added straight away so the pin shows while you type; removed again if
        // the editor is cancelled or left empty.
        _notes.Texts.Add(t);
        OpenEditor(t, isNew: true);
    }

    private void OpenEditor(NoteText t, bool isNew)
    {
        CloseEditor(commit: true);
        _editing = t;
        _editingIsNew = isNew;

        var box = new TextBox
        {
            Multiline = true,
            WordWrap = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Input,
            ForeColor = Theme.Text,
            Font = LabelFont,
            Text = t.Text,
            Size = new Size(LabelMaxWidth + LabelPad * 2, LabelFont.Height * 3 + 10),
        };
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.Handled = true; e.SuppressKeyPress = true;
                CloseEditor(commit: true);
                Focus();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true; e.SuppressKeyPress = true;
                CloseEditor(commit: false);
                Focus();
            }
        };
        box.LostFocus += (_, _) => { if (!_closingEditor) CloseEditor(commit: true); };

        _editor = box;
        Controls.Add(box);
        box.BringToFront();
        PositionEditor();
        box.Focus();
        box.SelectAll();
        Invalidate();
    }

    /// <summary>Keeps the editor next to its pin as the view moves under it.</summary>
    private void PositionEditor()
    {
        if (_editor is null || _editing is null) return;
        var r = LabelRect(_editing, _editor.Width);
        int x = Math.Clamp(r.X, 2, Math.Max(2, ClientSize.Width - _editor.Width - 2));
        int y = Math.Clamp(r.Y, 2, Math.Max(2, ClientSize.Height - _editor.Height - 2));
        _editor.Location = new Point(x, y);
    }

    private void CloseEditor(bool commit)
    {
        if (_editor is null || _editing is null) return;
        _closingEditor = true;
        var box = _editor;
        var t = _editing;
        bool isNew = _editingIsNew;
        _editor = null;
        _editing = null;

        var text = commit ? box.Text.Trim() : t.Text;
        Controls.Remove(box);
        // Disposing from inside the box's own LostFocus handler is asking for
        // trouble; let the message loop finish with it first.
        if (IsHandleCreated) BeginInvoke(() => box.Dispose());
        else box.Dispose();

        if (isNew)
        {
            if (commit && text.Length > 0)
            {
                t.Text = text;
                Push(undo: () => _notes.Texts.Remove(t), redo: () => _notes.Texts.Add(t));
            }
            else
            {
                _notes.Texts.Remove(t);
                Invalidate();
            }
        }
        else if (commit)
        {
            if (text.Length == 0)
            {
                RemoveItem(t);
            }
            else if (text != t.Text)
            {
                var old = t.Text;
                t.Text = text;
                Push(undo: () => t.Text = old, redo: () => t.Text = text);
            }
        }

        _closingEditor = false;
        Invalidate();
    }

    // ---- notes: hit testing ------------------------------------------------

    private object? HitAny(Point p)
    {
        if (!_notesVisible) return null;
        for (int i = _notes.Texts.Count - 1; i >= 0; i--)
            if (HitText(_notes.Texts[i], p)) return _notes.Texts[i];
        for (int i = _notes.Strokes.Count - 1; i >= 0; i--)
            if (HitStroke(_notes.Strokes[i], p)) return _notes.Strokes[i];
        return null;
    }

    private NoteText? HitTextNote(Point p)
    {
        if (!_notesVisible) return null;
        for (int i = _notes.Texts.Count - 1; i >= 0; i--)
            if (HitText(_notes.Texts[i], p)) return _notes.Texts[i];
        return null;
    }

    private bool HitText(NoteText t, Point p)
    {
        if (ReferenceEquals(t, _editing)) return false;
        var a = ToScreen(t.X, t.Y);
        float dx = p.X - a.X, dy = p.Y - a.Y;
        if (dx * dx + dy * dy <= (PinRadius + 4) * (PinRadius + 4)) return true;
        return LabelRect(t).Contains(p);
    }

    private bool HitStroke(NoteStroke s, Point p)
    {
        int n = s.Points.Count / 2;
        if (n == 0) return false;
        float tol = Math.Max(6f, s.Width / 2 + 4);

        var b = s.Bounds;
        var tl = ToScreen(b.Left, b.Top);
        var br = ToScreen(b.Right, b.Bottom);
        var box = RectangleF.FromLTRB(tl.X - tol, tl.Y - tol, br.X + tol, br.Y + tol);
        if (!box.Contains(p)) return false;

        var prev = ToScreen(s.Points[0], s.Points[1]);
        if (n == 1) return Dist2(prev, p) <= tol * tol;
        for (int i = 1; i < n; i++)
        {
            var cur = ToScreen(s.Points[2 * i], s.Points[2 * i + 1]);
            if (DistToSegment2(p, prev, cur) <= tol * tol) return true;
            prev = cur;
        }
        return false;
    }

    private static float Dist2(PointF a, Point p)
    {
        float dx = p.X - a.X, dy = p.Y - a.Y;
        return dx * dx + dy * dy;
    }

    private static float DistToSegment2(Point p, PointF a, PointF b)
    {
        float vx = b.X - a.X, vy = b.Y - a.Y;
        float wx = p.X - a.X, wy = p.Y - a.Y;
        float len2 = vx * vx + vy * vy;
        float t = len2 <= 1e-6f ? 0 : Math.Clamp((wx * vx + wy * vy) / len2, 0, 1);
        float cx = a.X + t * vx - p.X, cy = a.Y + t * vy - p.Y;
        return cx * cx + cy * cy;
    }

    private void UpdateHover(Point p)
    {
        object? hit = _tool switch
        {
            NoteTool.Erase => HitAny(p),
            NoteTool.Text => HitTextNote(p),
            _ => null,
        };
        if (!ReferenceEquals(hit, _hover))
        {
            _hover = hit;
            Invalidate();
        }
        UpdateCursor();
    }

    private void UpdateCursor()
    {
        if (_dragging) { Cursor = Cursors.SizeAll; return; }
        Cursor = _tool switch
        {
            NoteTool.Draw => Cursors.Cross,
            NoteTool.Text => _hover is not null ? Cursors.Hand : Cursors.IBeam,
            NoteTool.Erase => _hover is not null ? Cursors.Hand : Cursors.Cross,
            _ => Cursors.Default,
        };
    }

    // ---- input -------------------------------------------------------------

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int notches = e.Delta / 120;
        if (notches == 0) return;
        ZoomAt(Math.Pow(ZoomStep, notches), e.Location);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_map is null) return;

        // Clicking anywhere outside an open editor just closes it. It should
        // not also start a new note or a stroke under the same click.
        if (_editor is not null)
        {
            CloseEditor(commit: true);
            return;
        }

        bool ctrl = (ModifierKeys & Keys.Control) != 0;
        bool wantsPan = e.Button is MouseButtons.Middle or MouseButtons.Right
                        || (e.Button == MouseButtons.Left && (_tool == NoteTool.Pan || ctrl));

        if (e.Button == MouseButtons.Left && !wantsPan)
        {
            switch (_tool)
            {
                case NoteTool.Draw:
                    BeginStroke(e.Location);
                    return;

                case NoteTool.Text:
                    if (HitTextNote(e.Location) is { } note)
                    {
                        _dragNote = note;
                        _dragNoteMouse = e.Location;
                        _dragNoteX = note.X;
                        _dragNoteY = note.Y;
                        _dragNoteMoved = false;
                        Capture = true;
                    }
                    else
                    {
                        CreateTextNote(e.Location);
                    }
                    return;

                case NoteTool.Erase:
                    _erasing = true;
                    Capture = true;
                    if (HitAny(e.Location) is { } item) RemoveItem(item);
                    return;
            }
        }

        if (wantsPan)
        {
            _dragging = true;
            _dragOrigin = e.Location;
            _dragOffX = _offX;
            _dragOffY = _offY;
            Cursor = Cursors.SizeAll;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_activeStroke is not null)
        {
            AddStrokePoint(e.Location, force: false);
            return;
        }

        if (_dragNote is not null)
        {
            int dx = e.X - _dragNoteMouse.X, dy = e.Y - _dragNoteMouse.Y;
            if (!_dragNoteMoved && dx * dx + dy * dy < 16) return;   // click, not a drag, so far
            _dragNoteMoved = true;
            _dragNote.X = Math.Round(_dragNoteX + dx / _scale, 1);
            _dragNote.Y = Math.Round(_dragNoteY + dy / _scale, 1);
            Invalidate();
            return;
        }

        if (_erasing)
        {
            if (HitAny(e.Location) is { } item) RemoveItem(item);
            else UpdateHover(e.Location);
            return;
        }

        if (_dragging)
        {
            _fitTracking = false;
            _offX = _dragOffX - (e.X - _dragOrigin.X) / _scale;
            _offY = _dragOffY - (e.Y - _dragOrigin.Y) / _scale;
            ClampView();
            Invalidate();
            return;
        }

        UpdateHover(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (_activeStroke is not null)
        {
            AddStrokePoint(e.Location, force: false);
            EndStroke();
            return;
        }

        if (_dragNote is { } note)
        {
            _dragNote = null;
            Capture = false;
            if (_dragNoteMoved)
            {
                double ox = _dragNoteX, oy = _dragNoteY, nx = note.X, ny = note.Y;
                Push(undo: () => { note.X = ox; note.Y = oy; },
                     redo: () => { note.X = nx; note.Y = ny; });
            }
            else
            {
                OpenEditor(note, isNew: false);
            }
            return;
        }

        if (_erasing)
        {
            _erasing = false;
            Capture = false;
            UpdateHover(e.Location);
            return;
        }

        if (!_dragging) return;
        _dragging = false;
        UpdateCursor();
        ViewChanged?.Invoke();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is not null) { _hover = null; Invalidate(); }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (_map is null) return;
        // Only while panning: a double-click mid-drawing should not fling the zoom.
        if (e.Button == MouseButtons.Left && _tool != NoteTool.Pan) return;
        // Toggle between whole-map and native pixels under the cursor.
        if (_scale > FitScale * 1.05) FitToWindow();
        else ZoomAt(1.0 / _scale, e.Location);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is
        Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Add or Keys.Subtract
        ? true
        : base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Control && !e.Alt)
        {
            if (e.KeyCode == Keys.Z) { Undo(); e.Handled = true; return; }
            if (e.KeyCode == Keys.Y) { Redo(); e.Handled = true; return; }
        }
        else if (!e.Control && !e.Alt)
        {
            switch (e.KeyCode)
            {
                case Keys.V: Tool = NoteTool.Pan; e.Handled = true; return;
                case Keys.D:
                case Keys.P: Tool = NoteTool.Draw; e.Handled = true; return;
                case Keys.T: Tool = NoteTool.Text; e.Handled = true; return;
                case Keys.E: Tool = NoteTool.Erase; e.Handled = true; return;
                case Keys.H: NotesVisible = !NotesVisible; e.Handled = true; return;
            }
        }

        const int step = 90;
        switch (e.KeyCode)
        {
            case Keys.Left: _offX -= step / _scale; break;
            case Keys.Right: _offX += step / _scale; break;
            case Keys.Up: _offY -= step / _scale; break;
            case Keys.Down: _offY += step / _scale; break;
            case Keys.Add:
            case Keys.Oemplus: ZoomTo(ZoomStep); return;
            case Keys.Subtract:
            case Keys.OemMinus: ZoomTo(1 / ZoomStep); return;
            case Keys.D0: FitToWindow(); return;
            default: return;
        }
        _fitTracking = false;
        ClampView();
        PositionEditor();
        Invalidate();
        e.Handled = true;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_fitTracking) ApplyFit();
        else ClampView();
        PositionEditor();
    }

    // ---- rendering ---------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        if (_map is null)
        {
            DrawCentredMessage(g, "No maps found.\n\nDrop PNGs into the maps folder and run:\npython tools\\tile_maps.py");
            return;
        }

        if (_fitTracking) ApplyFit();

        int z = TargetLevel();
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighSpeed;

        // Coarse levels first: they are already resident and give an instant
        // (blurry) image while the sharp tiles for this level stream in.
        g.InterpolationMode = InterpolationMode.Bilinear;
        foreach (var zb in BackdropLevels(z))
            DrawLevel(g, zb, cachedOnly: true);

        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        DrawLevel(g, z, cachedOnly: false);

        DrawNotes(g);
        DrawBadges(g);
    }

    private static IEnumerable<int> BackdropLevels(int z)
    {
        if (z <= 0) yield break;
        yield return 0;                       // always a single tile, always cheap
        if (z - 2 > 0) yield return z - 2;
        if (z - 1 > 0) yield return z - 1;
    }

    private int TargetLevel()
    {
        if (_map is null) return 0;
        int z = _map.MaxLevel + (int)Math.Ceiling(Math.Log2(_scale));
        return Math.Clamp(z, 0, _map.MaxLevel);
    }

    private void DrawLevel(Graphics g, int z, bool cachedOnly)
    {
        if (_map is null || !_map.Levels.ContainsKey(z.ToString())) return;
        var lvl = _map.Level(z);
        int T = _map.TileSize;

        // Levels come from repeated integer halving, so derive the exact ratio
        // rather than assuming a clean power of two.
        double fx = lvl.Width / (double)_map.Width;
        double fy = lvl.Height / (double)_map.Height;
        double dsx = _scale / fx;
        double dsy = _scale / fy;

        double originX = -_offX * _scale;
        double originY = -_offY * _scale;

        int x0 = Math.Max(0, (int)Math.Floor(_offX * fx / T));
        int y0 = Math.Max(0, (int)Math.Floor(_offY * fy / T));
        int x1 = Math.Min(lvl.Cols - 1, (int)Math.Floor(((_offX + ClientSize.Width / _scale) * fx - 0.001) / T));
        int y1 = Math.Min(lvl.Rows - 1, (int)Math.Floor(((_offY + ClientSize.Height / _scale) * fy - 0.001) / T));

        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY);   // stops bilinear bleeding past tile edges

        for (int ty = y0; ty <= y1; ty++)
        {
            for (int tx = x0; tx <= x1; tx++)
            {
                var bmp = _cache.TryGet(z, tx, ty);
                if (bmp is null)
                {
                    if (!cachedOnly) _cache.Request(z, tx, ty);
                    continue;
                }

                // Round both edges from the same expression so neighbouring
                // tiles land on an identical boundary: no seams, no overlap.
                int left = (int)Math.Round(originX + tx * T * dsx);
                int top = (int)Math.Round(originY + ty * T * dsy);
                int right = (int)Math.Round(originX + (tx * T + bmp.Width) * dsx);
                int bottom = (int)Math.Round(originY + (ty * T + bmp.Height) * dsy);
                if (right <= left || bottom <= top) continue;

                g.DrawImage(bmp,
                    new Rectangle(left, top, right - left, bottom - top),
                    0, 0, bmp.Width, bmp.Height,
                    GraphicsUnit.Pixel, attrs);
            }
        }
    }

    // ---- rendering: notes --------------------------------------------------

    private void DrawNotes(Graphics g)
    {
        if (!_notesVisible && _activeStroke is null) return;

        var view = new RectangleF(0, 0, ClientSize.Width, ClientSize.Height);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_notesVisible)
            foreach (var s in _notes.Strokes)
                DrawStroke(g, s, view, ReferenceEquals(s, _hover));

        if (_activeStroke is not null)
            DrawStroke(g, _activeStroke, view, hover: false);

        if (_notesVisible)
            foreach (var t in _notes.Texts)
                DrawTextNote(g, t, view, ReferenceEquals(t, _hover), ReferenceEquals(t, _editing));

        g.SmoothingMode = SmoothingMode.None;
    }

    private Color HoverColor => _tool == NoteTool.Erase ? Theme.Danger : Color.White;

    private void DrawStroke(Graphics g, NoteStroke s, RectangleF view, bool hover)
    {
        int n = s.Points.Count / 2;
        if (n == 0) return;

        var b = s.Bounds;
        var tl = ToScreen(b.Left, b.Top);
        var br = ToScreen(b.Right, b.Bottom);
        float pad = s.Width + 8;
        if (!view.IntersectsWith(RectangleF.FromLTRB(tl.X - pad, tl.Y - pad, br.X + pad, br.Y + pad)))
            return;

        var pts = new PointF[Math.Max(n, 2)];
        for (int i = 0; i < n; i++)
            pts[i] = ToScreen(s.Points[2 * i], s.Points[2 * i + 1]);
        if (n == 1) pts[1] = new PointF(pts[0].X + 0.5f, pts[0].Y);   // a tap becomes a dot

        if (hover)
        {
            using var halo = RoundPen(Color.FromArgb(180, HoverColor), s.Width + 9);
            g.DrawLines(halo, pts);
        }

        // A dark rim under every stroke so a yellow route reads on snow and a
        // red one reads on the Customs rust just the same.
        using var rim = RoundPen(Color.FromArgb(150, 0, 0, 0), s.Width + 2.5f);
        g.DrawLines(rim, pts);
        using var pen = RoundPen(ParseColor(s.Color), s.Width);
        g.DrawLines(pen, pts);
    }

    private static Pen RoundPen(Color c, float width) => new(c, width)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round,
    };

    private Rectangle LabelRect(NoteText t, int? fixedWidth = null)
    {
        var a = ToScreen(t.X, t.Y);
        var size = TextRenderer.MeasureText(t.Text.Length == 0 ? " " : t.Text, LabelFont,
            new Size(LabelMaxWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        int w = fixedWidth ?? size.Width + LabelPad * 2;
        int h = size.Height + LabelPad * 2;
        return new Rectangle((int)a.X + PinRadius + LabelGap, (int)a.Y - h / 2, w, h);
    }

    private void DrawTextNote(Graphics g, NoteText t, RectangleF view, bool hover, bool editing)
    {
        var a = ToScreen(t.X, t.Y);
        // Labels hang to the right of the pin, so the pin can sit a label's
        // width off the left edge and still have something on screen.
        var reach = RectangleF.Inflate(view, LabelMaxWidth + 40, 80);
        if (!reach.Contains(a)) return;

        var color = ParseColor(t.Color);

        if (!editing)
        {
            var r = LabelRect(t);
            using var path = RoundedRect(r, 5);
            using var bg = new SolidBrush(Color.FromArgb(215, 20, 21, 25));
            g.FillPath(bg, path);
            using var border = new Pen(hover ? HoverColor : color, hover ? 2.5f : 1.5f);
            g.DrawPath(border, path);

            // Short leader from pin to box so the pair reads as one thing.
            using var leader = new Pen(color, 1.5f);
            g.DrawLine(leader, a.X + PinRadius, a.Y, r.Left, a.Y);

            TextRenderer.DrawText(g, t.Text, LabelFont,
                new Rectangle(r.X + LabelPad, r.Y + LabelPad, r.Width - LabelPad * 2, r.Height - LabelPad * 2),
                Color.FromArgb(236, 238, 242),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }

        // Pin: colour disc with a dark rim, same idea as the strokes.
        using var rim = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        g.FillEllipse(rim, a.X - PinRadius - 1.5f, a.Y - PinRadius - 1.5f, (PinRadius + 1.5f) * 2, (PinRadius + 1.5f) * 2);
        using var disc = new SolidBrush(hover ? HoverColor : color);
        g.FillEllipse(disc, a.X - PinRadius, a.Y - PinRadius, PinRadius * 2, PinRadius * 2);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color ParseColor(string s)
    {
        try { return ColorTranslator.FromHtml(s); }
        catch { return Color.FromArgb(232, 74, 60); }
    }

    // ---- rendering: chrome -------------------------------------------------

    private void DrawBadges(Graphics g)
    {
        using var f = new Font("Segoe UI", 8.5f);
        using var bg = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        using var fg = new SolidBrush(Color.FromArgb(190, 235, 235, 235));

        void Badge(string text, float x)
        {
            var size = g.MeasureString(text, f);
            var rect = new RectangleF(x, ClientSize.Height - size.Height - 10, size.Width + 12, size.Height + 4);
            g.FillRectangle(bg, rect);
            g.DrawString(text, f, fg, rect.X + 6, rect.Y + 2);
        }

        var scale = $"{_scale * 100:0}%";
        Badge(scale, 8);

        string? hint = _tool switch
        {
            NoteTool.Draw => "Draw  ·  drag to draw  ·  Ctrl+drag or middle-drag to pan  ·  Ctrl+Z undo",
            NoteTool.Text => "Text  ·  click to place a note  ·  click a note to edit, drag to move  ·  Enter saves",
            NoteTool.Erase => "Erase  ·  click or drag across a drawing or note to delete it",
            _ when !_notesVisible && HasNotes => "Notes hidden  ·  H to show",
            _ => null,
        };
        if (hint is not null)
            Badge(hint, 8 + g.MeasureString(scale, f).Width + 18);
    }

    private void DrawCentredMessage(Graphics g, string msg)
    {
        using var f = new Font("Segoe UI", 10f);
        using var b = new SolidBrush(Color.FromArgb(150, 160, 170));
        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(msg, f, b, new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), sf);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CloseEditor(commit: true);
            FlushNotes();
            _saveTimer.Dispose();
            _repaint.Stop();
            _repaint.Dispose();
            _cache.Dispose();
        }
        base.Dispose(disposing);
    }
}
