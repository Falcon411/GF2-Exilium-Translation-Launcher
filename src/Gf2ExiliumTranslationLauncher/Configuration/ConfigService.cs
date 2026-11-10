using System.Text.Json;
using Gf2ExiliumTranslationLauncher;
using Gf2ExiliumTranslationLauncher.Diagnostics;

namespace Gf2ExiliumTranslationLauncher.Configuration;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private const int SharingViolation = unchecked((int)0x80070020);

    public string ConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gf2ExiliumTranslationLauncher");

    public string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    public string StagingDirectory => Path.Combine(ConfigDirectory, "staging");

    public string TranslationExportPath => Path.Combine(ConfigDirectory, "translations.local.json");

    internal FileStream? TryAcquireSession()
    {
        Directory.CreateDirectory(ConfigDirectory);
        try
        {
            return new FileStream(Path.Combine(ConfigDirectory, "launcher.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when (exception.HResult == SharingViolation)
        {
            return null;
        }
    }

    public LauncherConfig Load()
    {
        var legacyPath = Path.Combine(LauncherPaths.ExecutableDirectory, "config.json");
        var path = File.Exists(ConfigPath) ? ConfigPath : legacyPath;
        AppLog.Info($"Loading config. ConfigPath={path}, StagingDirectory={StagingDirectory}");
        if (!File.Exists(path))
        {
            AppLog.Info("Config file does not exist. Using defaults.");
            var config = new LauncherConfig();
            config.ApplyDefaults();
            AppLog.Info($"Loaded default config. {DescribeConfig(config)}");
            return config;
        }

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<LauncherConfig>(json, SerializerOptions) ?? new LauncherConfig();
            config.ApplyDefaults();
            if (!string.Equals(path, ConfigPath, StringComparison.OrdinalIgnoreCase))
            {
                ImportLegacyConfig(path, json);
            }

            AppLog.Info($"Config loaded. {DescribeConfig(config)}");
            return config;
        }
        catch (JsonException exception)
        {
            AppLog.Error("Config JSON is corrupted. Backing it up and using defaults.", exception);
            // Corrupted JSON: backup the file so the user can potentially fix it
            var backupPath = $"{path}.corrupted.{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
            try
            {
                File.Move(path, backupPath);
                AppLog.Warn($"Corrupted config moved to {backupPath}");
            }
            catch (Exception backupException)
            {
                AppLog.Error($"Failed to move corrupted config to {backupPath}", backupException);
                // Ignore backup failure
            }

            var config = new LauncherConfig();
            config.ApplyDefaults();
            AppLog.Info($"Loaded default config after corrupted config. {DescribeConfig(config)}");
            return config;
        }
        catch (Exception exception)
        {
            AppLog.Error("Failed to load config. Using defaults.", exception);
            var config = new LauncherConfig();
            config.ApplyDefaults();
            AppLog.Info($"Loaded default config after config failure. {DescribeConfig(config)}");
            return config;
        }
    }

    public async Task SaveAsync(LauncherConfig config, CancellationToken cancellationToken = default)
    {
        config.ApplyDefaults();
        AppLog.Info($"Saving config. {DescribeConfig(config)}");

        var directory = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{ConfigPath}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(config, SerializerOptions);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, ConfigPath, overwrite: true);
            AppLog.Info($"Config saved to {ConfigPath}");
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
                AppLog.Warn($"Deleted leftover config temp file: {tempPath}");
            }
        }
    }

    private void ImportLegacyConfig(string path, string json)
    {
        var temporary = $"{ConfigPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(temporary, json);
            File.Move(temporary, ConfigPath);
            AppLog.Info($"Imported legacy config from {path} to {ConfigPath}");

            var legacyExport = Path.Combine(LauncherPaths.ExecutableDirectory, "translations.local.json");
            if (File.Exists(legacyExport) && !File.Exists(TranslationExportPath))
            {
                File.Copy(legacyExport, TranslationExportPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Could not finish importing legacy settings: {exception.Message}");
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Could not remove temporary settings: {exception.Message}");
            }
        }
    }

    private static string DescribeConfig(LauncherConfig config)
        => $"GitLabBaseUrl={config.GitLabBaseUrl}, Project={config.ProjectIdOrPath}, Ref={config.Ref}, "
           + $"RemoteFile={config.RemoteFilePath}, TokenPresent={!string.IsNullOrWhiteSpace(config.PrivateToken)}, "
           + $"LocalFile={AppLog.DescribePath(config.LocalFilePath)}, "
           + $"GameExecutable={AppLog.DescribePath(config.GameExecutablePath)}, "
           + $"UiLanguage={AppLog.DescribePath(config.UiLanguage)}, "
           + $"LastContentSha256={ShortHash(config.LastContentSha256)}, "
           + $"LastGeneratedFileSha256={ShortHash(config.LastGeneratedFileSha256)}, "
           + $"LastCommitId={ShortHash(config.LastCommitId)}, LastUpdatedAt={config.LastUpdatedAt?.ToString("O") ?? "<null>"}";

    private static string ShortHash(string value)
        => string.IsNullOrWhiteSpace(value)
            ? "<empty>"
            : value.Trim().Length <= 12
                ? value.Trim()
                : value.Trim()[..12];
}
