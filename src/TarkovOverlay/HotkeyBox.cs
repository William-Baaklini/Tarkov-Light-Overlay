namespace TarkovOverlay;

/// <summary>
/// Shows the shortcut currently assigned, and rebinds it when you click and
/// press a new combination.
///
/// Focus and capture are deliberately separate. An earlier version armed itself
/// on focus and replaced its own text with a prompt, which meant the first field
/// blanked as soon as the dialog opened (WinForms focuses the first selectable
/// control) and every other field blanked the instant you clicked it - so the
/// assigned shortcuts were never actually visible. The value now stays on screen
/// at all times; arming is shown with a highlight and an accent border instead.
///
/// Owner-drawn rather than a read-only TextBox so the dark theme, the centring
/// and the absence of a caret are all under our control.
/// </summary>
public sealed class HotkeyBox : Control
{
    private Hotkey _value = new();
    private bool _capturing;
    private bool _needsModifier;
    private bool _hover;

    public event Action? Changed;

    public HotkeyBox()
    {
        SetStyle(ControlStyles.Selectable
               | ControlStyles.UserPaint
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Font = Theme.UiFont;
        ForeColor = Theme.Text;
        BackColor = Theme.Input;
    }

    public Hotkey Value
    {
        get => _value;
        set
        {
            _value = value ?? new Hotkey();
            _needsModifier = false;
            Invalidate();
        }
    }

    // ---- arming ------------------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        StartCapture();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        StopCapture();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();   // focus ring only - deliberately does NOT arm capture
    }

    private void StartCapture()
    {
        if (_capturing) return;
        _capturing = true;
        _needsModifier = false;
        Invalidate();
    }

    private void StopCapture()
    {
        if (!_capturing && !_needsModifier) { Invalidate(); return; }
        _capturing = false;
        _needsModifier = false;
        Invalidate();
    }

    // ---- key capture -------------------------------------------------------

    // Intercepted here rather than in OnKeyDown so Tab, Enter, Space and the
    // arrow keys can be captured before WinForms turns them into navigation.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var code = keyData & Keys.KeyCode;

        if (!_capturing)
        {
            // Reached by keyboard: Space or Enter arms it.
            if (Focused && code is Keys.Space or Keys.Enter)
            {
                StartCapture();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Armed from here on.
        if (code == Keys.Escape)
        {
            StopCapture();
            return true;
        }

        if (code is Keys.Back or Keys.Delete)
        {
            Value = new Hotkey();
            _capturing = false;
            Changed?.Invoke();
            Invalidate();
            return true;
        }

        // Ignore the modifier keys themselves; wait for a real key.
        if (code is Keys.ControlKey or Keys.ShiftKey or Keys.Menu
                 or Keys.LWin or Keys.RWin or Keys.None)
            return true;

        var hk = Hotkey.FromKeyData(keyData);
        hk.Win = Native.WinKeyDown();

        // A bare letter would fire while typing in game chat, so require a
        // modifier unless it is a function key, which nothing else claims.
        bool bare = !hk.Ctrl && !hk.Alt && !hk.Shift && !hk.Win;
        bool functionKey = code is >= Keys.F1 and <= Keys.F24;
        if (bare && !functionKey)
        {
            _needsModifier = true;
            Invalidate();
            return true;
        }

        Value = hk;
        _capturing = false;
        Changed?.Invoke();
        Invalidate();
        return true;
    }

    // ---- painting ----------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;

        var back = _capturing ? Theme.ChromeHover
                 : _hover ? Theme.Chrome
                 : Theme.Input;
        using (var b = new SolidBrush(back)) g.FillRectangle(b, r);

        var border = _capturing ? Theme.Accent
                   : Focused ? Theme.Muted
                   : Color.FromArgb(70, 74, 82);
        using (var p = new Pen(border))
            g.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);

        // The assigned shortcut stays visible whatever the state; only the
        // unassigned and rejected cases substitute a prompt.
        string text;
        Color colour;
        if (_needsModifier)
        {
            text = "add Ctrl / Alt / Shift / Win";
            colour = Theme.Danger;
        }
        else if (!_value.IsSet)
        {
            text = _capturing ? "press a combination..." : "(none)";
            colour = Theme.Muted;
        }
        else
        {
            text = _value.ToString();
            colour = _capturing ? Theme.Accent : Theme.Text;
        }

        TextRenderer.DrawText(g, text, Font, r, colour,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
