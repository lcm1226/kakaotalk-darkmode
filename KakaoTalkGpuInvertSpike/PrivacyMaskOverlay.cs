namespace KakaoTalkGpuInvertSpike;

internal sealed class PrivacyMaskOverlay : IDisposable
{
    private const float SidebarVisibleWidth = 78;
    private const float TitleButtonsVisibleHeight = 38;
    private readonly PrivacyMaskForm _contentMask = new();
    public int BottomCutPixels { get; set; }
    public int VisibleStatusWidth { get; set; }
    public Color MaskColor { get; set; } = Color.Black;

    public void Position(TargetWindow target, float dpiScale, bool show)
    {
        if (!show)
        {
            Hide();
            return;
        }

        _contentMask.Position(
            target.Handle,
            CalculateBounds(target, dpiScale, BottomCutPixels, VisibleStatusWidth),
            MaskColor);
    }

    internal static Rectangle CalculateBounds(TargetWindow target, float dpiScale, int bottomCutPixels, int visibleStatusWidth)
    {
        dpiScale = Math.Clamp(dpiScale, 0.5f, 4f);
        var sidebarWidth = Math.Clamp(
            (int)Math.Round(SidebarVisibleWidth * dpiScale),
            0,
            target.Width);
        var titleHeight = Math.Clamp(
            (int)Math.Round(TitleButtonsVisibleHeight * dpiScale),
            0,
            target.Height);

        return new Rectangle(
                target.X + sidebarWidth,
                target.Y + titleHeight,
                Math.Max(0, target.Width - sidebarWidth - Math.Max(0, visibleStatusWidth)),
                Math.Max(0, WindowRegionCut.VisibleHeight(target.Height, bottomCutPixels) - titleHeight));
    }

    public void Hide()
    {
        _contentMask.Hide();
    }

    public void Dispose()
    {
        _contentMask.Dispose();
    }

    private sealed class PrivacyMaskForm : Form
    {
        private nint _ownerHandle;
        private bool _inputPassThroughVerified;
        private bool _layeredAlphaConfigured;
        private Color _requestedColor = Color.Black;

        public PrivacyMaskForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = false;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= NativeMethods.WsExTransparent |
                    NativeMethods.WsExToolWindow |
                    NativeMethods.WsExNoActivate |
                    NativeMethods.WsExLayered;
                parameters.Style |= NativeMethods.WsDisabled;
                return parameters;
            }
        }

        public void Position(nint ownerHandle, Rectangle bounds, Color color)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                Hide();
                return;
            }

            _requestedColor = color;

            _ = Handle;
            if (_ownerHandle != ownerHandle)
            {
                _ = NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GwlpHwndParent, ownerHandle);
                _ownerHandle = ownerHandle;
                _inputPassThroughVerified = false;
            }

            if (!_layeredAlphaConfigured)
            {
                _layeredAlphaConfigured = NativeMethods.SetLayeredWindowAttributes(
                    Handle, 0, 255, NativeMethods.LwaAlpha);
                if (!_layeredAlphaConfigured)
                    AppDiagnostics.WriteStatus("Privacy mask layered hit-testing could not be configured");
            }

            if (BackColor != _requestedColor) BackColor = _requestedColor;

            if (!Visible)
            {
                Bounds = bounds;
                Show();
            }

            // Keep this native mask immediately above its owner, underneath any
            // GPU surface and unrelated foreground app. Excluding the GPU surface
            // from capture still leaves this normal mask visible in desktop capture.
            var insertAfter = NativeMethods.GetWindow(ownerHandle, NativeMethods.GwHwndPrevious);
            var flags = NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow;
            if (insertAfter == Handle) flags |= NativeMethods.SwpNoZOrder;
            _ = NativeMethods.SetWindowPos(
                Handle,
                insertAfter == Handle ? nint.Zero : insertAfter,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                flags);

            if (!_inputPassThroughVerified && !IsInputPassThrough())
            {
                // A transient WindowFromPoint result must not hide Privacy or unwind Refresh.
                AppDiagnostics.WriteStatus("Privacy mask hit-test confirmation deferred");
                return;
            }

            _inputPassThroughVerified = true;
        }

        private bool IsInputPassThrough()
        {
            if (!NativeMethods.GetWindowRect(Handle, out var bounds) ||
                bounds.Width <= 0 ||
                bounds.Height <= 0)
            {
                return false;
            }

            var hitPoint = new NativeMethods.Point
            {
                X = bounds.Left + bounds.Width / 2,
                Y = bounds.Top + bounds.Height / 2
            };
            return !NativeMethods.IsWindowEnabled(Handle) &&
                NativeMethods.WindowFromPoint(hitPoint) != Handle;
        }
    }
}
