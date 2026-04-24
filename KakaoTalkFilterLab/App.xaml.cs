using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using KakaoTalkFilterLab.Services;

namespace KakaoTalkFilterLab;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (TryRunImageProcessingMode(Environment.GetCommandLineArgs().Skip(1).ToArray()))
        {
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static bool TryRunImageProcessingMode(string[] args)
    {
        if (args.Length < 2 || !string.Equals(args[0], "--process-file", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var inputPath = Path.GetFullPath(args[1]);
        if (!File.Exists(inputPath))
        {
            return false;
        }

        var outputDirectory = args.Length >= 3
            ? Path.GetFullPath(args[2])
            : Path.Combine(Path.GetDirectoryName(inputPath)!, "processed");

        Directory.CreateDirectory(outputDirectory);

        var source = LoadBitmap(inputPath);
        var captureService = new WindowCaptureService();

        captureService.SavePng(source, Path.Combine(outputDirectory, "source.png"));
        captureService.SavePng(captureService.Invert(source), Path.Combine(outputDirectory, "invert.png"));
        captureService.SavePng(captureService.SmartInvert(source), Path.Combine(outputDirectory, "smart.png"));
        captureService.SaveSmartDebugOutputs(source, outputDirectory);
        File.WriteAllText(
            Path.Combine(outputDirectory, "report.txt"),
            $"ProcessedAt={DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\nInput={inputPath}\r\n");

        return true;
    }

    private static BitmapSource LoadBitmap(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapFrame.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }
}
