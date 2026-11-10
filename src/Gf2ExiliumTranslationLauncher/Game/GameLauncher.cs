using System.Diagnostics;
using Gf2ExiliumTranslationLauncher.Configuration;
using Gf2ExiliumTranslationLauncher.Diagnostics;
using Gf2ExiliumTranslationLauncher.Localization;

namespace Gf2ExiliumTranslationLauncher.Game;

public sealed class GameLauncher
{
    public void Launch(LauncherConfig config)
    {
        var executablePath = config.GameExecutablePath.Trim();
        AppLog.Info($"Launch requested. Executable={AppLog.DescribePath(executablePath)}, ArgumentsPresent={!string.IsNullOrWhiteSpace(config.GameArguments)}");
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            AppLog.Warn("Launch failed: game executable path is empty.");
            throw new LauncherException(AppText.Get("ErrorGameExecutableRequired"));
        }

        if (!File.Exists(executablePath))
        {
            AppLog.Warn($"Launch failed: game executable does not exist. Executable={executablePath}");
            throw new LauncherException(AppText.Get("ErrorGameExecutableNotFound"));
        }

        var processName = Path.GetFileNameWithoutExtension(executablePath);
        EnsureNotRunning(executablePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? "",
            UseShellExecute = false
        };

        var arguments = config.GameArguments.Trim();
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            startInfo.Arguments = arguments;
        }

        var process = Process.Start(startInfo);
        if (process is null)
        {
            AppLog.Error($"Launch failed: Process.Start returned null. Executable={executablePath}");
            throw new LauncherException(AppText.Get("ErrorGameProcessStartFailed"));
        }

        AppLog.Info($"Game process started. ProcessName={processName}, ProcessId={process.Id}, WorkingDirectory={startInfo.WorkingDirectory}");
    }

    internal static void EnsureNotRunning(string executablePath)
    {
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        var processes = Process.GetProcessesByName(processName);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        if (processes.Length > 0)
        {
            AppLog.Warn($"Operation blocked: game is already running. ProcessName={processName}");
            throw new LauncherException(AppText.Format("ErrorGameAlreadyRunning", processName));
        }
    }
}
