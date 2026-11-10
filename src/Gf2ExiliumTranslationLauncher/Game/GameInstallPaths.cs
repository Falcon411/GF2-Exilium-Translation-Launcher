using Gf2ExiliumTranslationLauncher.Diagnostics;
using Gf2ExiliumTranslationLauncher.Localization;

namespace Gf2ExiliumTranslationLauncher.Game;

public static class GameInstallPaths
{
    public const string ExpectedExecutableName = "GF2_Exilium.exe";
    public const string TranslationFileName = "LangPackageTableEnusData.bytes";

    public static string GetTranslationFilePath(string executablePath)
    {
        var gameDirectory = Path.GetDirectoryName(executablePath.Trim());
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            AppLog.Warn($"Cannot resolve translation file path from executable path: {AppLog.DescribePath(executablePath)}");
            throw new LauncherException(AppText.Get("ErrorInvalidGameExecutablePath"));
        }

        var translationFilePath = Path.Combine(
            gameDirectory,
            "GF2_Exilium_Data",
            "LocalCache",
            "Data",
            "Table",
            TranslationFileName);
        AppLog.Info($"Resolved translation file path. Executable={executablePath}, TranslationFile={translationFilePath}");
        return translationFilePath;
    }

    public static string GetStartupTranslationFilePath(string executablePath)
        => Path.Combine(
            Path.GetDirectoryName(GetTranslationFilePath(executablePath))!,
            "LangPackageTableEnusBuiltinData.bytes");

    internal static (Version Number, bool FromCache)? ReadInstalledVersion(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath)!;
        foreach (var launcherDirectory in new[] { directory, Path.GetDirectoryName(directory) })
        {
            if (launcherDirectory is null)
            {
                continue;
            }

            var version = ReadOfficialLauncherVersion(Path.Combine(launcherDirectory, "config.ini"), directory);
            if (version is not null)
            {
                return (version, false);
            }
        }

        var cache = Path.Combine(directory, "GF2_Exilium_Data", "LocalCache", "Data", "ClientRes_Windows");
        try
        {
            if (Directory.Exists(cache))
            {
                var version = Directory.EnumerateDirectories(cache)
                    .Select(path => Version.TryParse(Path.GetFileName(path), out var parsed) ? parsed : null)
                    .OfType<Version>()
                    .Max();
                if (version is not null)
                {
                    return (version, true);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Could not read cached game version: {exception.Message}");
        }

        return null;
    }

    private static Version? ReadOfficialLauncherVersion(string configPath, string gameDirectory)
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            string? installedPath = null;
            Version? version = null;
            var inLauncherSection = false;
            foreach (var line in File.ReadLines(configPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('['))
                {
                    inLauncherSection = string.Equals(trimmed, "[launcher]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                var separator = trimmed.IndexOf('=');
                if (!inLauncherSection || separator < 0)
                {
                    continue;
                }

                var key = trimmed[..separator].Trim();
                var value = trimmed[(separator + 1)..].Trim().Trim('"');
                if (string.Equals(key, "game_install_path", StringComparison.OrdinalIgnoreCase))
                {
                    installedPath = value;
                }
                else if (string.Equals(key, "downloaded_client_version", StringComparison.OrdinalIgnoreCase))
                {
                    Version.TryParse(value, out version);
                }
            }

            if (version is not null && installedPath is not null && Path.IsPathFullyQualified(installedPath)
                && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installedPath)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory)), StringComparison.OrdinalIgnoreCase))
            {
                return version;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Warn($"Could not read official launcher version: {exception.Message}");
        }

        return null;
    }

    internal static bool IsInstalled(string path)
        => IsExpectedExecutable(path)
           && Path.IsPathFullyQualified(path)
           && File.Exists(path)
           && Directory.Exists(Path.Combine(Path.GetDirectoryName(path)!, "GF2_Exilium_Data"));

    public static bool IsExpectedExecutable(string? executablePath)
        => !string.IsNullOrWhiteSpace(executablePath) && string.Equals(
            Path.GetFileName(executablePath.Trim()),
            ExpectedExecutableName,
            StringComparison.OrdinalIgnoreCase);
}
