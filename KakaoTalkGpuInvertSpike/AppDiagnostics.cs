using System.Runtime.InteropServices;

namespace KakaoTalkGpuInvertSpike;

internal static class AppDiagnostics
{
    private const long MaximumLogBytes = 1024 * 1024;
    private static readonly object Sync = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KakaoTalkGpuInvertSpike");
    private static readonly string StatusLogPath = Path.Combine(DirectoryPath, "spike.log");
    private static readonly string CrashLogPath = Path.Combine(DirectoryPath, "crash.log");

    public static void Initialize()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) =>
            WriteException("Unhandled UI thread exception", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteException(
                args.IsTerminating ? "Terminating runtime exception" : "Runtime exception",
                args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteException("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        WriteLifecycle(
            $"Process started | PID={Environment.ProcessId} | " +
            $"OS={RuntimeInformation.OSDescription} | Version={Environment.OSVersion.Version}");
    }

    public static void WriteStatus(string message)
    {
        Append(StatusLogPath, message);
    }

    public static void WriteLifecycle(string message)
    {
        Append(CrashLogPath, message);
    }

    public static void WriteException(string context, Exception exception)
    {
        Append(CrashLogPath, $"{context}{Environment.NewLine}{exception}");
    }

    private static void Append(string path, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                RotateIfNeeded(path);
                File.AppendAllText(
                    path,
                    $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never affect the rendering process.
        }
    }

    private static void RotateIfNeeded(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length < MaximumLogBytes)
        {
            return;
        }

        var previousPath = path + ".previous";
        File.Move(path, previousPath, true);
    }
}
