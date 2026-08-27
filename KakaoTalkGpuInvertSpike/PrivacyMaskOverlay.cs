namespace KakaoTalkGpuInvertSpike;

internal sealed class PrivacyMaskOverlay : IDisposable
{
    private const float SidebarVisibleWidth = 78;
    private const float TitleButtonsVisibleWidth = 138;
    private const float TitleButtonsVisibleHeight = 38;
    private readonly PrivacyMaskForm _titleMask = new();
    private readonly PrivacyMaskForm _bodyMask = new();

    public void Position(TargetWindow target, float dpiScale, bool show)
    {
        if (!show)
        {
            Hide();
            return;
        }

        dpiScale = Math.Clamp(dpiScale, 0.5f, 4f);
        var sidebarWidth = Math.Clamp(
            (int)Math.Round(SidebarVisibleWidth * dpiScale),
            0,
            target.Width);
        var titleButtonsWidth = Math.Clamp(
            (int)Math.Round(TitleButtonsVisibleWidth * dpiScale),
            0,
            Math.Max(0, target.Width - sidebarWidth));
        var titleHeight = Math.Clamp(
            (int)Math.Round(TitleButtonsVisibleHeight * dpiScale),
            0,
            target.Height);

        _titleMask.Position(
            target.Handle,
            new Rectangle(
                target.X + sidebarWidth,
                target.Y,
                Math.Max(0, target.Width - sidebarWidth - titleButtonsWidth),
                titleHeight));
        _bodyMask.Position(
            target.Handle,
            new Rectangle(
                target.X + sidebarWidth,
                target.Y + titleHeight,
                Math.Max(0, target.Width - sidebarWidth),
                Math.Max(0, target.Height - titleHeight)));
    }

    public void Hide()
    {
        _titleMask.Hide();
        _bodyMask.Hide();
    }

    public void Dispose()
    {
        _titleMask.Dispose();
        _bodyMask.Dispose();
    }

    private sealed class PrivacyMaskForm : Form
    {
        private nint _ownerHandle;
        private bool _inputPassThroughVerified;

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
                    NativeMethods.WsExNoActivate;
                parameters.Style |= NativeMethods.WsDisabled;
                return parameters;
            }
        }

        public void Position(nint ownerHandle, Rectangle bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                Hide();
                return;
            }

            _ = Handle;
            if (_ownerHandle != ownerHandle)
            {
                _ = NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GwlpHwndParent, ownerHandle);
                _ownerHandle = ownerHandle;
                _inputPassThroughVerified = false;
            }

            if (!Visible)
            {
                Bounds = bounds;
                Show();
            }

            _ = NativeMethods.SetWindowPos(
                Handle,
                nint.Zero,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);

            if (!_inputPassThroughVerified && !IsInputPassThrough())
            {
                Hide();
                throw new InvalidOperationException(
                    "A privacy mask window failed its input pass-through check.");
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
