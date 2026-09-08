namespace KakaoTalkGpuInvertSpike;

internal sealed class BottomResizeGrip : Form
{
    private readonly Action _changed;
    private nint _target;
    private NativeMethods.Rect _startBounds;
    private int _startPointerY;
    public bool IsDragging { get; private set; }

    public BottomResizeGrip(Action changed)
    {
        _changed = changed;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(42, 44, 48);
        Cursor = Cursors.SizeNS;
        Text = "KakaoDark bottom resize";
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
            return parameters;
        }
    }

    public void Position(TargetWindow target, int cut)
    {
        if (cut <= 0) { Hide(); return; }
        if (_target != target.Handle)
        {
            _target = target.Handle;
            NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GwlpHwndParent, _target);
        }
        var visibleHeight = WindowRegionCut.VisibleHeight(target.Height, cut);
        var height = Math.Min(visibleHeight, Math.Max(4, (int)Math.Round(NativeMethods.GetDpiForWindow(_target) / 96d * 4)));
        var rectangle = new Rectangle(target.X, target.Y + visibleHeight - height, target.Width, height);
        if (!Visible)
        {
            Bounds = rectangle;
            Show();
        }
        // Do not raise this strip over an unrelated foreground application on each refresh.
        NativeMethods.SetWindowPos(Handle, 0, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder | NativeMethods.SwpShowWindow);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !NativeMethods.GetWindowRect(_target, out _startBounds)) return;
        _startPointerY = Cursor.Position.Y;
        IsDragging = true;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsDragging || !NativeMethods.IsWindow(_target)) return;
        var height = CalculateHeight(_startBounds.Height, Cursor.Position.Y - _startPointerY);
        NativeMethods.SetWindowPos(_target, 0, _startBounds.Left, _startBounds.Top, _startBounds.Width, height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder);
        _changed();
    }

    internal static int CalculateHeight(int initialHeight, int delta) => Math.Clamp(initialHeight + delta, 500, 20000);

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        // Update the zone baseline while the manual-resize flag is still true.
        if (IsDragging) _changed();
        IsDragging = false;
        Capture = false;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && IsDragging)
        {
            _changed();
            IsDragging = false;
        }
    }
}
