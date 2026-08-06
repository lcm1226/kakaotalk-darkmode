namespace KakaoTalkGpuInvertSpike;

internal sealed class StrengthSliderForm : Form
{
    private const int EdgePadding = 4;
    private readonly CheckBox _enabledCheckBox;
    private readonly TrackBar _slider;
    private readonly Action<int> _strengthChanged;
    private readonly Action<bool> _enabledChanged;
    private readonly Action _hideRequested;
    private nint _ownerHandle;
    private bool _isSynchronizingEnabled;

    public StrengthSliderForm(
        int strength,
        bool enabled,
        Action<int> strengthChanged,
        Action<bool> enabledChanged,
        Action hideRequested)
    {
        _strengthChanged = strengthChanged;
        _enabledChanged = enabledChanged;
        _hideRequested = hideRequested;
        Text = "GPU invert strength";
        ClientSize = new Size(224, 40);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = SystemColors.Control;
        DoubleBuffered = true;

        _enabledCheckBox = new CheckBox
        {
            AccessibleName = "Enable GPU invert",
            Checked = enabled,
            Location = new Point(5, 2),
            Size = new Size(18, 18),
            TabStop = false,
            UseVisualStyleBackColor = true
        };
        _enabledCheckBox.CheckedChanged += (_, _) =>
        {
            if (!_isSynchronizingEnabled)
            {
                _enabledChanged(_enabledCheckBox.Checked);
            }
        };

        var titleLabel = new Label
        {
            AutoEllipsis = true,
            Location = new Point(25, 1),
            Size = new Size(174, 19),
            Text = "GPU invert strength",
            TextAlign = ContentAlignment.MiddleLeft
        };
        titleLabel.Click += (_, _) => _enabledCheckBox.Checked = !_enabledCheckBox.Checked;

        var closeButton = new Button
        {
            AccessibleName = "Hide strength control",
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8, FontStyle.Bold),
            Location = new Point(202, 0),
            Padding = Padding.Empty,
            Size = new Size(21, 20),
            TabStop = false,
            Text = "X",
            TextAlign = ContentAlignment.MiddleCenter,
            UseVisualStyleBackColor = true
        };
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.Click += (_, _) => HideAndNotify();

        _slider = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 10,
            SmallChange = 1,
            LargeChange = 10,
            Value = Math.Clamp(strength, 0, 100),
            Location = new Point(4, 18),
            Size = new Size(216, 20),
            AutoSize = false,
            TickStyle = TickStyle.None,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        _slider.ValueChanged += (_, _) =>
        {
            _strengthChanged(_slider.Value);
        };

        Controls.Add(_enabledCheckBox);
        Controls.Add(titleLabel);
        Controls.Add(closeButton);
        Controls.Add(_slider);
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

    public void ShowNear(TargetWindow target)
    {
        StartPosition = FormStartPosition.Manual;
        _ = Handle;
        if (_ownerHandle != target.Handle)
        {
            _ = NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GwlpHwndParent, target.Handle);
            _ownerHandle = target.Handle;
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

        if (!Visible)
        {
            Show();
        }

        _ = NativeMethods.SetWindowPos(
            Handle,
            nint.Zero,
            left,
            top,
            Width,
            Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
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
        ControlPaint.DrawBorder(
            e.Graphics,
            ClientRectangle,
            SystemColors.ActiveBorder,
            ButtonBorderStyle.Solid);
    }

    private void HideAndNotify()
    {
        Hide();
        _hideRequested();
    }
}
