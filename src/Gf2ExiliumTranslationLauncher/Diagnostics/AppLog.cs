using System.Globalization;
using Gf2ExiliumTranslationLauncher;

namespace Gf2ExiliumTranslationLauncher.Diagnostics;

public static class AppLog
{
    private const int MaxLaunchLogs = 5;
    private const string LogFilePrefix = "launch-";
    private const string LogFileExtension = ".log";
    private static readonly object SyncRoot = new();
    private static bool _initialized;

    public static string LogDirectory { get; private set; } = "";

    public static string CurrentLogPath { get; private set; } = "";

    public static void Initialize()
    {
        lock (SyncRoot)
        {
            if (_initialized)
            {
                return;
            }

            LogDirectory = GetLogDirectory();
            Directory.CreateDirectory(LogDirectory);
            CurrentLogPath = Path.Combine(
                LogDirectory,
                $"{LogFilePrefix}{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-pid{Environment.ProcessId}{LogFileExtension}");

            File.WriteAllText(CurrentLogPath, "");
            _initialized = true;
            DeleteOldLaunchLogs();
        }

        Info("Logging initialized.");
        Info($"Log file: {CurrentLogPath}");
        Info($"Keeping latest {MaxLaunchLogs} launch log files.");
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public static string DescribePath(string path)
        => string.IsNullOrWhiteSpace(path) ? "<empty>" : path;

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            EnsureInitialized();
            var line = FormatLine(level, message, exception);
            lock (SyncRoot)
            {
                File.AppendAllText(CurrentLogPath, line);
            }
        }
        catch
        {
            // Logging must never break launcher behavior.
        }
    }

    private static string FormatLine(string level, string message, Exception? exception)
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        var line = $"[{timestamp}] [{level}] {message}{Environment.NewLine}";
        return exception is null
            ? line
            : $"{line}{exception}{Environment.NewLine}";
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        Initialize();
    }

    private static string GetLogDirectory()
        => Path.Combine(LauncherPaths.ExecutableDirectory, "logs");

    private static void DeleteOldLaunchLogs()
    {
        try
        {
            var oldLogs = Directory
                .EnumerateFiles(LogDirectory, $"{LogFilePrefix}*{LogFileExtension}")
                .Where(path => !string.Equals(path, CurrentLogPath, StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.CreationTimeUtc)
                .ThenByDescending(file => file.Name)
                .Skip(MaxLaunchLogs - 1)
                .ToList();

            foreach (var oldLog in oldLogs)
            {
                oldLog.Delete();
            }
        }
        catch
        {
            // Ignore cleanup failures; the current launch log is more important.
        }
    }
}
