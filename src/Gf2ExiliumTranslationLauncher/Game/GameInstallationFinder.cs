using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Gf2ExiliumTranslationLauncher.Game;

internal static class GameInstallationFinder
{
    private static readonly string[] FolderNames =
        ["GF2Exilium", "GF2 Exilium", "GF2 Game", "Girls Frontline 2", "Girls Frontline 2 Exilium", "Girls' Frontline 2 Exilium"];

    public static string? Find()
    {
        foreach (var root in CandidateRoots().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var path in new[]
            {
                Path.Combine(root, GameInstallPaths.ExpectedExecutableName),
                Path.Combine(root, "GF2 Game", GameInstallPaths.ExpectedExecutableName)
            })
            {
                if (GameInstallPaths.IsInstalled(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        foreach (var location in RegisteredInstallations())
        {
            yield return location;
        }

        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady))
        {
            var root = drive.RootDirectory.FullName;
            foreach (var folder in FolderNames)
            {
                yield return Path.Combine(root, folder);
                yield return Path.Combine(root, "Games", folder);
                yield return Path.Combine(root, "Program Files", folder);
                yield return Path.Combine(root, "Program Files (x86)", folder);
                yield return Path.Combine(root, "SteamLibrary", "steamapps", "common", folder);
            }
        }

        foreach (var library in SteamLibraries())
        {
            var common = Path.Combine(library, "steamapps", "common");
            foreach (var folder in EnumerateDirectories(common))
            {
                if (LooksLikeGame(Path.GetFileName(folder)))
                {
                    yield return folder;
                }
            }
        }
    }

    private static IEnumerable<string> RegisteredInstallations()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var path in new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            })
            {
                using var uninstall = hive.OpenSubKey(path);
                foreach (var name in uninstall?.GetSubKeyNames() ?? [])
                {
                    using var entry = uninstall?.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is string title && LooksLikeGame(title)
                        && entry.GetValue("InstallLocation") is string location
                        && Path.IsPathFullyQualified(location.Trim('"')))
                    {
                        yield return location.Trim('"');
                    }
                }
            }
        }
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string installedSteam && Path.IsPathFullyQualified(installedSteam))
            {
                steam = installedSteam;
            }
        }

        yield return steam;
        foreach (var line in ReadLines(Path.Combine(steam, "steamapps", "libraryfolders.vdf")))
        {
            var match = Regex.Match(line, "^\\s*\"path\"\\s+\"([^\"]+)\"");
            if (match.Success)
            {
                var library = match.Groups[1].Value.Replace(@"\\", @"\");
                if (Path.IsPathFullyQualified(library))
                {
                    yield return library;
                }
            }
        }
    }

    private static string[] ReadLines(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string[] EnumerateDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool LooksLikeGame(string name)
        => name.Contains("Exilium", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Girls Frontline 2", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Girls' Frontline 2", StringComparison.OrdinalIgnoreCase);
}
