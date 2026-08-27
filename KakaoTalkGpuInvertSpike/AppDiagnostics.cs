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
    private static readonly string SessionMarkerPath = Path.Combine(DirectoryPath, "active-session.txt");

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

        var sessionDescription =
            $"PID={Environment.ProcessId} | Started={DateTimeOffset.Now:O} | " +
            $"OS={RuntimeInformation.OSDescription} | Version={Environment.OSVersion.Version}";
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                if (File.Exists(SessionMarkerPath))
                {
                    var previousSession = File.ReadAllText(SessionMarkerPath).Trim();
                    AppendCore(CrashLogPath, $"Previous session ended unexpectedly | {previousSession}");
                }

                File.WriteAllText(SessionMarkerPath, sessionDescription);
            }
        }
        catch
        {
            // Session tracking is best effort.
        }

        WriteLifecycle($"Process started | {sessionDescription}");
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

    public static void CompleteSession(string reason, bool cleanShutdown)
    {
        WriteLifecycle($"Process stopped | Reason={reason}");
        if (!cleanShutdown)
        {
            return;
        }

        try
        {
            lock (Sync)
            {
                File.Delete(SessionMarkerPath);
            }
        }
        catch
        {
            // Session tracking is best effort.
        }
    }

    private static void Append(string path, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                AppendCore(path, message);
            }
        }
        catch
        {
            // Diagnostics must never affect the rendering process.
        }
    }

    private static void AppendCore(string path, string message)
    {
        RotateIfNeeded(path);
        File.AppendAllText(
            path,
            $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
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
