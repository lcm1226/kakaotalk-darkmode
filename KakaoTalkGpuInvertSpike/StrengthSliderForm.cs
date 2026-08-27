using System.Drawing.Drawing2D;

namespace KakaoTalkGpuInvertSpike;

internal sealed class StrengthSliderForm : Form
{
    private const int EdgePadding = 4;
    private static readonly Color PanelBackground = Color.FromArgb(32, 33, 36);
    private static readonly Color PrimaryText = Color.FromArgb(245, 246, 247);
    private readonly DarkCheckBox _enabledCheckBox;
    private readonly DarkCheckBox _privacyModeCheckBox;
    private readonly DarkCheckBox _autoPrivacyCheckBox;
    private readonly DarkPeekButton _peekButton;
    private readonly DarkStrengthSlider _slider;
    private readonly ToolTip _toolTip = new();
    private readonly Action<int> _strengthChanged;
    private readonly Action<bool> _enabledChanged;
    private readonly Action<bool> _privacyModeChanged;
    private readonly Action<bool> _autoPrivacyChanged;
    private readonly Action _peekRequested;
    private readonly Action _hideRequested;
    private nint _ownerHandle;
    private bool _isSynchronizingEnabled;
    private bool _isSynchronizingPrivacyMode;
    private bool _isSynchronizingAutoPrivacy;
    private int _lastLeft = int.MinValue;
    private int _lastTop = int.MinValue;

    public StrengthSliderForm(
        int strength,
        bool enabled,
        bool privacyModeEnabled,
        bool autoPrivacyEnabled,
        Action<int> strengthChanged,
        Action<bool> enabledChanged,
        Action<bool> privacyModeChanged,
        Action<bool> autoPrivacyChanged,
        Action peekRequested,
        Action hideRequested)
    {
        _strengthChanged = strengthChanged;
        _enabledChanged = enabledChanged;
        _privacyModeChanged = privacyModeChanged;
        _autoPrivacyChanged = autoPrivacyChanged;
        _peekRequested = peekRequested;
        _hideRequested = hideRequested;
        Text = "GPU invert strength";
        ClientSize = new Size(281, 40);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = PanelBackground;
        ForeColor = PrimaryText;
        Font = new Font("Segoe UI", 9, FontStyle.Regular);
        DoubleBuffered = true;

        _enabledCheckBox = new DarkCheckBox
        {
            AccessibleName = "Enable GPU invert",
            BackColor = PanelBackground,
            Checked = enabled,
            Location = new Point(7, 2),
            Size = new Size(92, 20),
            TabStop = false,
            Text = "GPU invert"
        };
        _enabledCheckBox.CheckedChanged += (_, _) =>
        {
            if (!_isSynchronizingEnabled)
            {
                _enabledChanged(_enabledCheckBox.Checked);
            }
        };

        _privacyModeCheckBox = new DarkCheckBox
        {
            AccessibleName = "Privacy mode",
            BackColor = PanelBackground,
            Checked = privacyModeEnabled,
            Location = new Point(104, 2),
            Size = new Size(64, 20),
            TabStop = false,
            Text = "Privacy"
        };
        _privacyModeCheckBox.CheckedChanged += (_, _) =>
        {
            if (!_isSynchronizingPrivacyMode)
            {
                _privacyModeChanged(_privacyModeCheckBox.Checked);
            }
        };

        _autoPrivacyCheckBox = new DarkCheckBox
        {
            AccessibleName = "Auto privacy when KakaoTalk loses focus",
            BackColor = PanelBackground,
            Checked = autoPrivacyEnabled,
            Location = new Point(171, 2),
            Size = new Size(50, 20),
            TabStop = false,
            Text = "Auto"
        };
        _autoPrivacyCheckBox.CheckedChanged += (_, _) =>
        {
            if (!_isSynchronizingAutoPrivacy)
            {
                _autoPrivacyChanged(_autoPrivacyCheckBox.Checked);
            }
        };

        _peekButton = new DarkPeekButton
        {
            AccessibleName = "Focus reveal for 8 seconds",
            BackColor = PanelBackground,
            Location = new Point(226, 2),
            Size = new Size(26, 20),
            TabStop = false
        };
        _peekButton.Click += (_, _) => _peekRequested();
        _toolTip.SetToolTip(_peekButton, "Focus reveal for 8 seconds (Ctrl+Shift+H)");

        var closeButton = new Button
        {
            AccessibleName = "Hide strength control",
            BackColor = PanelBackground,
            ForeColor = PrimaryText,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8, FontStyle.Bold),
            Location = new Point(254, 1),
            Padding = Padding.Empty,
            Size = new Size(21, 20),
            TabStop = false,
            Text = "X",
            TextAlign = ContentAlignment.MiddleCenter,
            UseVisualStyleBackColor = false
        };
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(52, 54, 58);
        closeButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(68, 71, 76);
        closeButton.Click += (_, _) => HideAndNotify();

        _slider = new DarkStrengthSlider
        {
            Minimum = 0,
            Maximum = 100,
            SmallChange = 1,
            LargeChange = 10,
            Value = Math.Clamp(strength, 0, 100),
            Location = new Point(6, 22),
            Size = new Size(269, 18),
            BackColor = PanelBackground,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        _slider.ValueChanged += (_, _) =>
        {
            _strengthChanged(_slider.Value);
        };

        Controls.Add(_enabledCheckBox);
        Controls.Add(_privacyModeCheckBox);
        Controls.Add(_autoPrivacyCheckBox);
        Controls.Add(_peekButton);
        Controls.Add(closeButton);
        Controls.Add(_slider);
        UpdateRoundedRegion();
    }

    protected override bool ShowWithoutActivation => true;

    public void ShowControl()
    {
        if (!Visible)
        {
            Show();
        }

        Activate();
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabledCheckBox.Checked == enabled)
        {
            return;
        }

        _isSynchronizingEnabled = true;
        try
        {
            _enabledCheckBox.Checked = enabled;
        }
        finally
        {
            _isSynchronizingEnabled = false;
        }
    }

    public void SetPrivacyMode(bool enabled)
    {
        if (_privacyModeCheckBox.Checked == enabled)
        {
            return;
        }

        _isSynchronizingPrivacyMode = true;
        try
        {
            _privacyModeCheckBox.Checked = enabled;
        }
        finally
        {
            _isSynchronizingPrivacyMode = false;
        }
    }

    public void SetAutoPrivacy(bool enabled)
    {
        if (_autoPrivacyCheckBox.Checked == enabled)
        {
            return;
        }

        _isSynchronizingAutoPrivacy = true;
        try
        {
            _autoPrivacyCheckBox.Checked = enabled;
        }
        finally
        {
            _isSynchronizingAutoPrivacy = false;
        }
    }

    public void SetPeekActive(bool active)
    {
        _peekButton.IsActive = active;
        _toolTip.SetToolTip(
            _peekButton,
            active
                ? "End focus reveal now (Ctrl+Shift+H)"
                : "Focus reveal for 8 seconds (Ctrl+Shift+H)");
    }

    public void ShowNear(TargetWindow target)
    {
        StartPosition = FormStartPosition.Manual;
        _ = Handle;
        if (_ownerHandle != target.Handle)
        {
            _ = NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GwlpHwndParent, target.Handle);
            _ownerHandle = target.Handle;
            _lastLeft = int.MinValue;
            _lastTop = int.MinValue;
        }

        var targetBounds = new Rectangle(
            target.X,
            target.Y,
            Math.Max(1, target.Width),
            Math.Max(1, target.Height));
        var workingArea = Screen.FromRectangle(targetBounds).WorkingArea;
        var left = Math.Clamp(
            target.X,
            workingArea.Left + EdgePadding,
            Math.Max(workingArea.Left + EdgePadding, workingArea.Right - Width - EdgePadding));
        var top = target.Y - Height - EdgePadding;
        if (top < workingArea.Top + EdgePadding)
        {
            top = target.Y + EdgePadding;
        }

        top = Math.Clamp(
            top,
            workingArea.Top + EdgePadding,
            Math.Max(workingArea.Top + EdgePadding, workingArea.Bottom - Height - EdgePadding));

        var wasVisible = Visible;
        if (!wasVisible)
        {
            Show();
        }

        if (!wasVisible || _lastLeft != left || _lastTop != top)
        {
            _ = NativeMethods.SetWindowPos(
                Handle,
                nint.Zero,
                left,
                top,
                Width,
                Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
            _lastLeft = left;
            _lastTop = top;
        }
    }

    public void SaveDiagnosticImage(string path)
    {
        var wasVisible = Visible;
        if (!wasVisible)
        {
            Show();
        }

        PerformLayout();
        Refresh();
        foreach (Control control in Controls)
        {
            control.Refresh();
        }
        Application.DoEvents();
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        if (!wasVisible)
        {
            Hide();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideAndNotify();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var borderPath = CreatePanelPath(
            new Rectangle(0, 0, Math.Max(1, ClientSize.Width - 1), Math.Max(1, ClientSize.Height - 1)),
            12);
        using var borderPen = new Pen(Color.FromArgb(74, 77, 83));
        e.Graphics.DrawPath(borderPen, borderPath);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRoundedRegion();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private void HideAndNotify()
    {
        Hide();
        _hideRequested();
    }

    private void UpdateRoundedRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        using var path = CreatePanelPath(ClientRectangle, 12);
        var previousRegion = Region;
        Region = new Region(path);
        previousRegion?.Dispose();
    }

    private static GraphicsPath CreatePanelPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height * 2));
        path.StartFigure();
        path.AddLine(bounds.Left, bounds.Bottom, bounds.Left, bounds.Top + radius);
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddLine(bounds.Left + radius, bounds.Top, bounds.Right - radius, bounds.Top);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddLine(bounds.Right, bounds.Top + radius, bounds.Right, bounds.Bottom);
        path.CloseFigure();
        return path;
    }
}

internal sealed class DarkCheckBox : CheckBox
{
    private static readonly Color BoxBackground = Color.FromArgb(45, 47, 51);
    private static readonly Color Border = Color.FromArgb(151, 155, 163);
    private static readonly Color Foreground = Color.FromArgb(245, 246, 247);

    public DarkCheckBox()
    {
        SetStyle(
            ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
            true);
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        const int boxSize = 13;
        var box = new Rectangle(1, Math.Max(1, (Height - boxSize) / 2), boxSize, boxSize);
        using var boxBrush = new SolidBrush(Checked ? Foreground : BoxBackground);
        using var borderPen = new Pen(Enabled ? Border : Color.FromArgb(90, 93, 99));
        e.Graphics.FillRectangle(boxBrush, box);
        e.Graphics.DrawRectangle(borderPen, box);

        if (Checked)
        {
            using var checkPen = new Pen(Color.FromArgb(32, 33, 36), 1.8f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            e.Graphics.DrawLines(
                checkPen,
                new Point[]
                {
                    new Point(box.Left + 3, box.Top + 7),
                    new Point(box.Left + 6, box.Top + 10),
                    new Point(box.Left + 11, box.Top + 4)
                });
        }

        if (!string.IsNullOrEmpty(Text))
        {
            var textBounds = new Rectangle(19, 0, Math.Max(0, Width - 19), Height);
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                textBounds,
                Enabled ? Foreground : Color.FromArgb(135, 138, 145),
                TextFormatFlags.Left |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine |
                    TextFormatFlags.NoPadding);
        }
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }
}

internal sealed class DarkPeekButton : Button
{
    private static readonly Color Foreground = Color.FromArgb(245, 246, 247);
    private static readonly Color ActiveBackground = Color.FromArgb(68, 71, 76);
    private bool _isActive;
    private bool _isHovered;
    private bool _isPressed;

    public DarkPeekButton()
    {
        SetStyle(
            ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
            true);
        Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var background = _isPressed || _isActive
            ? ActiveBackground
            : _isHovered
                ? Color.FromArgb(52, 54, 58)
                : BackColor;
        e.Graphics.Clear(background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var centerX = Width / 2f;
        var centerY = Height / 2f;
        var left = centerX - 7;
        var right = centerX + 7;
        var top = centerY - 4.5f;
        var bottom = centerY + 4.5f;
        using var eyePath = new GraphicsPath();
        eyePath.StartFigure();
        eyePath.AddBezier(left, centerY, left + 4, top, right - 4, top, right, centerY);
        eyePath.AddBezier(right, centerY, right - 4, bottom, left + 4, bottom, left, centerY);
        eyePath.CloseFigure();
        using var eyePen = new Pen(Foreground, 1.4f);
        e.Graphics.DrawPath(eyePen, eyePath);
        using var pupilBrush = new SolidBrush(Foreground);
        e.Graphics.FillEllipse(pupilBrush, centerX - 2, centerY - 2, 4, 4);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _isHovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _isHovered = false;
        _isPressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _isPressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _isPressed = false;
        Invalidate();
    }
}

internal sealed class DarkStrengthSlider : Control
{
    private static readonly Color InactiveTrack = Color.FromArgb(82, 85, 91);
    private static readonly Color ActiveTrack = Color.FromArgb(202, 205, 211);
    private static readonly Color Thumb = Color.FromArgb(248, 249, 250);
    private int _minimum;
    private int _maximum = 100;
    private int _value;
    private bool _dragging;

    public DarkStrengthSlider()
    {
        SetStyle(
            ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable,
            true);
        AccessibleRole = AccessibleRole.Slider;
        AccessibleName = "Invert strength";
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    public event EventHandler? ValueChanged;

    public int Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            if (_maximum < _minimum)
            {
                _maximum = _minimum;
            }

            Value = _value;
            Invalidate();
        }
    }

    public int Maximum
    {
        get => _maximum;
        set
        {
            _maximum = Math.Max(value, _minimum);
            Value = _value;
            Invalidate();
        }
    }

    public int SmallChange { get; set; } = 1;

    public int LargeChange { get; set; } = 10;

    public int Value
    {
        get => _value;
        set
        {
            var normalized = Math.Clamp(value, _minimum, _maximum);
            if (_value == normalized)
            {
                return;
            }

            _value = normalized;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        const int thumbRadius = 5;
        var left = thumbRadius + 1;
        var right = Math.Max(left + 1, Width - thumbRadius - 1);
        var centerY = Height / 2;
        var ratio = _maximum == _minimum
            ? 0f
            : (_value - _minimum) / (float)(_maximum - _minimum);
        var thumbX = left + (int)Math.Round((right - left) * ratio);

        using var inactivePen = CreateTrackPen(Enabled ? InactiveTrack : Color.FromArgb(58, 60, 64));
        using var activePen = CreateTrackPen(Enabled ? ActiveTrack : Color.FromArgb(92, 94, 99));
        e.Graphics.DrawLine(inactivePen, left, centerY, right, centerY);
        e.Graphics.DrawLine(activePen, left, centerY, thumbX, centerY);

        using var thumbBrush = new SolidBrush(Enabled ? Thumb : Color.FromArgb(120, 123, 129));
        using var thumbBorder = new Pen(Color.FromArgb(28, 29, 32));
        var thumbBounds = new Rectangle(
            thumbX - thumbRadius,
            centerY - thumbRadius,
            thumbRadius * 2,
            thumbRadius * 2);
        e.Graphics.FillEllipse(thumbBrush, thumbBounds);
        e.Graphics.DrawEllipse(thumbBorder, thumbBounds);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _dragging = true;
        Capture = true;
        SetValueFromPosition(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            SetValueFromPosition(e.X);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = false;
            Capture = false;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var direction = Math.Sign(e.Delta);
        if (direction != 0)
        {
            Value += direction * Math.Max(1, SmallChange);
        }
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    private static Pen CreateTrackPen(Color color)
    {
        return new Pen(color, 3)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
    }

    private void SetValueFromPosition(int x)
    {
        const int thumbRadius = 5;
        var left = thumbRadius + 1;
        var right = Math.Max(left + 1, Width - thumbRadius - 1);
        var normalizedX = Math.Clamp(x, left, right);
        var ratio = (normalizedX - left) / (float)(right - left);
        Value = _minimum + (int)Math.Round((_maximum - _minimum) * ratio);
    }
}
