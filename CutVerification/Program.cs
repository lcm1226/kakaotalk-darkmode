using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using KakaoTalkGpuInvertSpike;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [STAThread]
    private static int Main(string[] args)
    {
        var prior = NativeMethods.GetForegroundWindow();
        var priorThread = NativeMethods.GetWindowThreadProcessId(prior, out var priorProcess);
        var output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        if (args.Contains("--headless")) return VerifyWithoutFocus(output);
        var checks = new List<string>();
        try
        {
            NativeMethods.SetProcessDpiAwarenessContext(new nint(-4));
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var notice = new Form
            {
                Text = "Desktop verification", TopMost = true, Size = new Size(580, 130),
                StartPosition = FormStartPosition.CenterScreen
            };
            notice.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "For the next 20 seconds, CutVerification will be brought to the foreground. Please pause your current work briefly.",
                Padding = new Padding(15)
            });
            notice.Show();
            Application.DoEvents();
            Check(NativeMethods.IsWindowVisible(notice.Handle), "announcement visible", checks);
            var deadline = Stopwatch.StartNew();
            var area = Screen.PrimaryScreen!.WorkingArea;
            using var behind = new Form
            {
                Text = "Cut verification backdrop", FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(area.Left + 80, area.Top + 110, 520, 500),
                BackColor = Color.FromArgb(30, 140, 85)
            };
            using var source = new Form
            {
                Text = "Cut verification source", FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual, Bounds = behind.Bounds, BackColor = Color.White
            };
            behind.Show();
            source.Show();
            notice.Hide();
            SetForegroundWindow(source.Handle);
            Application.DoEvents();
            Check(NativeMethods.GetForegroundWindow() == source.Handle, "source foreground verified", checks);
            var target = new TargetWindow(source.Handle, source.Left, source.Top, source.Width, source.Height);
            using var cut = new WindowRegionCut();
            cut.Apply(target, 125);
            Application.DoEvents();
            var bottomPoint = new NativeMethods.Point { X = source.Left + 260, Y = source.Bottom - 50 };
            Check(NativeMethods.WindowFromPoint(bottomPoint) == behind.Handle, "bottom hit test reaches backdrop", checks);
            Check(NativeMethods.WindowFromPoint(new NativeMethods.Point { X = source.Left + 260, Y = source.Top + 200 }) == source.Handle,
                "upper hit test remains on source", checks);
            cut.Apply(target, 0);
            Check(NativeMethods.WindowFromPoint(bottomPoint) == source.Handle, "zero restores bottom input", checks);
            cut.Apply(target, 125);
            source.Height = 540;
            target = target with { Height = 540 };
            cut.Apply(target, 125);
            Check(source.Height == 540, "cut preserves host dimensions after resize", checks);
            source.Height = 500;
            target = target with { Height = 500 };
            cut.Apply(target, 125);
            using var overlay = new OverlayWindow { BottomCutPixels = 125 };
            overlay.Position(target, false);
            using var renderer = new GpuRenderer(overlay.Handle, target.Width, target.Height, 100, false, 1);
            using var capture = new WgcCaptureSession(renderer, () => { }, () => { });
            capture.Start(source.Handle);
            while (!capture.HasPresentedFrame && !capture.IsFaulted && deadline.ElapsedMilliseconds < 12000)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
            Check(capture.HasPresentedFrame && !capture.IsFaulted, "WGC presents clipped source", checks);
            overlay.Position(target, true);
            using var control = new StrengthSliderForm(100, true, false, false,
                _ => { }, _ => { }, _ => { }, _ => { }, () => { }, () => { }, 125);
            control.ShowNear(target);
            control.ShowControl();
            Application.DoEvents();
            Check(NativeMethods.GetForegroundWindow() == control.Handle, "control foreground verified", checks);
            Check(NativeMethods.WindowFromPoint(bottomPoint) == behind.Handle, "GPU overlay bottom passes input", checks);
            Check(deadline.ElapsedMilliseconds < 19000, "foreground interval within 20 seconds", checks);
            var rectangle = Rectangle.Union(control.Bounds, source.Bounds);
            using var screenshot = new Bitmap(rectangle.Width, rectangle.Height);
            using (var graphics = Graphics.FromImage(screenshot))
                graphics.CopyFromScreen(rectangle.Location, Point.Empty, rectangle.Size);
            screenshot.Save(Path.Combine(output, "bottom-cut.png"));
            var bottomColor = screenshot.GetPixel(bottomPoint.X - rectangle.Left, bottomPoint.Y - rectangle.Top);
            Check(Math.Abs(bottomColor.G - behind.BackColor.G) < 5, "cut visually reveals backdrop", checks);
            var topColor = screenshot.GetPixel(source.Left + 260 - rectangle.Left, source.Top + 200 - rectangle.Top);
            Check(topColor.R < 15 && topColor.G < 15 && topColor.B < 15, "GPU invert remains above cut", checks);
            control.Hide();
            overlay.Hide();
            capture.SetRenderingEnabled(false);
            using var privacy = new PrivacyMaskOverlay { BottomCutPixels = 125 };
            privacy.Position(target, 1, true);
            Check(NativeMethods.WindowFromPoint(bottomPoint) == behind.Handle, "privacy fallback preserves bottom pass-through", checks);
            privacy.Hide();
            cut.Dispose();
            Check(NativeMethods.WindowFromPoint(bottomPoint) == source.Handle, "dispose restores original region", checks);
            Check(JsonSerializer.Deserialize<GpuInvertSettings>("{}")!.BottomCutPixels == 125, "legacy settings default to 125", checks);
            var restored = JsonSerializer.Deserialize<GpuInvertSettings>(JsonSerializer.Serialize(new GpuInvertSettings { BottomCutPixels = 73 }));
            Check(restored!.BottomCutPixels == 73, "custom cut setting round-trips", checks);
            File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
            Console.WriteLine(string.Join(Environment.NewLine, checks));
            return 0;
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(output, "failure.txt"), exception.ToString());
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            var thread = NativeMethods.GetWindowThreadProcessId(prior, out var process);
            var restored = prior != 0 && thread == priorThread && process == priorProcess && SetForegroundWindow(prior)
                && NativeMethods.GetForegroundWindow() == prior;
            Console.WriteLine($"Prior foreground restored: {restored}");
        }
    }

    private static void Check(bool condition, string name, List<string> checks)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks.Add("PASS " + name);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    private static extern int GetRgnBox(nint region, out NativeMethods.Rect bounds);
    [DllImport("gdi32.dll")]
    private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint region);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetProp(nint window, string name, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint RemoveProp(nint window, string name);

    private static int VerifyWithoutFocus(string output)
    {
        var checks = new List<string>();
        var region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            NativeMethods.SetProcessDpiAwarenessContext(new nint(-4));
            Application.EnableVisualStyles();
            using var source = new Form { FormBorderStyle = FormBorderStyle.None, Size = new Size(520, 500) };
            var hwnd = source.Handle;
            NativeMethods.GetWindowRect(hwnd, out var bounds);
            var target = new TargetWindow(hwnd, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            using var cut = new WindowRegionCut();
            cut.Apply(target, 125);
            GetWindowRgn(hwnd, region);
            GetRgnBox(region, out var clipped);
            Check(clipped.Bottom == 375, "125 physical pixels removed from bottom", checks);
            Check(PtInRegion(region, 260, 374) && !PtInRegion(region, 260, 375), "exact region boundary", checks);
            Check(source.Height == 500, "host height unchanged", checks);
            for (var i = 0; i < 100; i++) cut.Apply(target, 125);
            cut.Apply(target, 73);
            GetWindowRgn(hwnd, region);
            GetRgnBox(region, out clipped);
            Check(clipped.Bottom == 427, "changing cut does not accumulate", checks);
            cut.Apply(target, 10000);
            GetWindowRgn(hwnd, region);
            GetRgnBox(region, out clipped);
            Check(clipped.Bottom == 1, "oversized value leaves one pixel", checks);
            cut.Apply(target, 0);
            Check(GetWindowRgn(hwnd, region) == 0, "zero restores original unbounded region", checks);
            cut.Apply(target, target.Height, allowEmpty: true);
            Check(GetWindowRgn(hwnd, region) == 1, "fully cut advertisement has empty region", checks);
            cut.Apply(target, 60, allowEmpty: true);
            GetWindowRgn(hwnd, region);
            GetRgnBox(region, out clipped);
            Check(clipped.Bottom == 440, "advertisement can return from empty to partial region", checks);
            cut.Apply(target, 0, allowEmpty: true);
            Check(GetWindowRgn(hwnd, region) == 0, "zero restores fully cut advertisement", checks);
            source.Height = 600;
            target = target with { Height = 600 };
            cut.Apply(target, 125);
            GetWindowRgn(hwnd, region);
            GetRgnBox(region, out clipped);
            Check(clipped.Bottom == 475, "resize preserves requested bottom cut", checks);
            cut.Dispose();
            Check(GetWindowRgn(hwnd, region) == 0, "dispose restores original region", checks);
            using var shape = new System.Drawing.Region(new Rectangle(10, 10, 490, 570));
            source.Region = shape.Clone();
            cut.Apply(target, 125);
            cut.Dispose();
            GetWindowRgn(hwnd, region);
            GetRgnBox(region, out clipped);
            Check(clipped.Left == 10 && clipped.Bottom == 580, "preexisting custom region restored", checks);
            source.Region = null;
            using (var alignment = new ZoneCutAlignment())
            {
                var unchanged = alignment.Apply(target, 125);
                Check(unchanged.Height == 600, "unassigned windows are not expanded", checks);
                SetProp(hwnd, "FancyZones_zones", 1);
                try
                {
                    var aligned = alignment.Apply(target, 125);
                    Check(aligned.Height == 725, "zone receives exactly one hidden tail", checks);
                    for (var i = 0; i < 50; i++) aligned = alignment.Apply(aligned, 125);
                    Check(aligned.Height == 725, "zone compensation does not accumulate", checks);
                    aligned = alignment.Apply(aligned, 80);
                    Check(aligned.Height == 680, "cut edits preserve the visible zone height", checks);
                    aligned = alignment.Apply(aligned, 0);
                    Check(aligned.Height == 600, "zero restores zone height", checks);
                    source.Height = 700;
                    aligned = alignment.Apply(target with { Height = 700 }, 125);
                    Check(aligned.Height == 825, "new zone placement receives fresh compensation", checks);
                    source.Height = 850;
                    aligned = alignment.Apply(aligned with { Height = 850 }, 125, manualResize: true);
                    Check(alignment.Apply(aligned, 125).Height == 850, "manual drag is not compensated twice", checks);
                    alignment.Dispose();
                    Check(source.Height == 725, "zone tail restored on disposal", checks);
                }
                finally { RemoveProp(hwnd, "FancyZones_zones"); }
            }
            Check(BottomResizeGrip.CalculateHeight(700, 90) == 790, "bottom drag increases actual height", checks);
            Check(BottomResizeGrip.CalculateHeight(700, -90) == 610, "bottom drag decreases actual height", checks);
            Check(BottomResizeGrip.CalculateHeight(700, -900) == 500, "bottom drag respects minimum detection height", checks);
            Check(JsonSerializer.Deserialize<GpuInvertSettings>("{}")!.BottomCutPixels == 125, "legacy settings default 125", checks);
            Check(JsonSerializer.Deserialize<GpuInvertSettings>(JsonSerializer.Serialize(new GpuInvertSettings { BottomCutPixels = 73 }))!.BottomCutPixels == 73,
                "custom setting round-trip", checks);
            var callback = -1;
            using var control = new StrengthSliderForm(100, true, false, false,
                _ => { }, _ => { }, _ => { }, _ => { }, () => { }, () => { }, 125, value => callback = value);
            var input = control.Controls.OfType<NumericUpDown>().Single();
            input.Value = 73;
            Check(callback == 73, "numeric input sends edited cut", checks);
            input.Text = "160";
            var commitWait = Stopwatch.StartNew();
            while (callback != 160 && commitWait.ElapsedMilliseconds < 1000)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
            Check(callback == 160, "typed cut commits without Enter or focus loss", checks);
            input.Value = 125;
            using var bitmap = new Bitmap(control.Width, control.Height);
            _ = control.Handle;
            control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
            // Render each child explicitly because the parent stays hidden throughout QA.
            using (var graphics = Graphics.FromImage(bitmap))
            {
                foreach (Control child in control.Controls)
                {
                    _ = child.Handle;
                    using var childBitmap = new Bitmap(child.Width, child.Height);
                    child.DrawToBitmap(childBitmap, new Rectangle(Point.Empty, child.Size));
                    graphics.DrawImageUnscaled(childBitmap, child.Location);
                    foreach (Control nested in child.Controls)
                    {
                        _ = nested.Handle;
                        using var nestedBitmap = new Bitmap(nested.Width, nested.Height);
                        nested.DrawToBitmap(nestedBitmap, new Rectangle(Point.Empty, nested.Size));
                        graphics.DrawImageUnscaled(nestedBitmap, child.Left + nested.Left, child.Top + nested.Top);
                    }
                }
            }
            bitmap.Save(Path.Combine(output, "control-render.png"));
            Check(!source.Visible && !control.Visible, "verification did not show or focus windows", checks);
            File.WriteAllLines(Path.Combine(output, "headless-checks.txt"), checks);
            Console.WriteLine(string.Join(Environment.NewLine, checks));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally { DeleteObject(region); }
    }
}
