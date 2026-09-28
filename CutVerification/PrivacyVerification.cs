using System.Text.Json;
using KakaoTalkGpuInvertSpike;

internal static class PrivacyVerification
{
    public static void Run(string output, List<string> checks)
    {
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add("PASS " + name);
        }

        Check(PrivacyPolicy.Next(PrivacyMode.Off) == PrivacyMode.StatusOnly, "first privacy press reveals status column");
        Check(PrivacyPolicy.Next(PrivacyMode.StatusOnly) == PrivacyMode.Full, "second privacy press covers status column");
        Check(PrivacyPolicy.Next(PrivacyMode.Full) == PrivacyMode.Off, "third privacy press disables mask");
        Check(PrivacyPolicy.Effective(PrivacyMode.StatusOnly, true, false, false) == PrivacyMode.Full, "Auto retains full protection while unfocused");
        Check(PrivacyPolicy.Effective(PrivacyMode.StatusOnly, true, true, false) == PrivacyMode.StatusOnly, "manual status mode returns on focus");
        Check(PrivacyPolicy.Effective(PrivacyMode.StatusOnly, true, false, true) == PrivacyMode.Off, "Peek temporarily reveals status-mode content");
        Check(JsonSerializer.Deserialize<GpuInvertSettings>("{\"PrivacyModeEnabled\":true}")!.GetPrivacyMode() == PrivacyMode.Full,
            "legacy privacy setting migrates to full protection");
        Check(JsonSerializer.Deserialize<GpuInvertSettings>(JsonSerializer.Serialize(new GpuInvertSettings { PrivacyLevel = 1 }))!.GetPrivacyMode() == PrivacyMode.StatusOnly,
            "status-only state persists");
        foreach (var dpi in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            var width = (int)Math.Round(510 * dpi);
            var target = new TargetWindow(0, 0, 0, width, 800);
            var band = PrivacyPolicy.StatusWidth(PrivacyMode.StatusOnly, dpi);
            var partial = PrivacyMaskOverlay.CalculateBounds(target, dpi, 125, band);
            var full = PrivacyMaskOverlay.CalculateBounds(target, dpi, 125, 0);
            Check(partial.Right == width - band && full.Right == width && partial.Bottom == 675,
                $"native mask status boundary and Cut agree at {dpi:0.##} DPI scale");
        }
        Check(PrivacyPolicy.StatusWidth(PrivacyMode.StatusOnly, 1.25f) == 111, "reference image uses x=527 on 638px window");

        using var panel = new StrengthSliderForm(100, true, false, false, _ => { }, _ => { }, _ => { }, _ => { }, () => { }, () => { });
        var checkbox = panel.Controls.OfType<CheckBox>().Single(control => control.Text == "Privacy");
        panel.SetPrivacyLevel(PrivacyMode.StatusOnly);
        Check(checkbox.CheckState == CheckState.Indeterminate, "status privacy displays a dash indicator");
        panel.SetPrivacyLevel(PrivacyMode.Full);
        Check(checkbox.CheckState == CheckState.Checked, "full privacy displays a check indicator");
        panel.SetPrivacyLevel(PrivacyMode.Off);
        Check(checkbox.CheckState == CheckState.Unchecked, "off privacy clears indicator");

        var previousPath = Environment.GetEnvironmentVariable("KAKAOTALK_GPU_CAPTURE_PATH");
        try
        {
            // Render directly to a hidden swap chain: no desktop focus or live screenshot is used.
            using var host = new Form { FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(638, 400) };
            foreach (var mode in new[] { PrivacyMode.StatusOnly, PrivacyMode.Full })
            {
                var path = Path.Combine(output, $"privacy-{mode}.png");
                Environment.SetEnvironmentVariable("KAKAOTALK_GPU_CAPTURE_PATH", path);
                var band = PrivacyPolicy.StatusWidth(mode, 1.25f);
                using var renderer = new GpuRenderer(host.Handle, 638, 400, 100, true, 1.25f, visibleStatusWidth: band);
                renderer.RenderDiagnosticPattern();
                using var pixels = new Bitmap(path);
                bool Black(int x, int y) => pixels.GetPixel(x, y).ToArgb() == Color.Black.ToArgb();
                Check(Black(250, 120), $"GPU {mode} hides conversation content");
                Check(!Black(50, 120) && !Black(250, 25), $"GPU {mode} preserves sidebar and title strip");
                Check(Black(526, 120), $"GPU {mode} masks the last private column");
                Check(Black(527, 120) == (mode == PrivacyMode.Full), $"GPU {mode} exact x=527 status boundary");
            }
        }
        finally { Environment.SetEnvironmentVariable("KAKAOTALK_GPU_CAPTURE_PATH", previousPath); }
    }
}
