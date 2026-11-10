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

    public string ConfigDirectory { get; } = GetConfigDirectory();

    public string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    public string StagingDirectory { get; } = GetStagingDirectory();

    public string TranslationExportPath => Path.Combine(ConfigDirectory, "translations.local.json");

    public LauncherConfig Load()
    {
        AppLog.Info($"Loading config. ConfigPath={ConfigPath}, StagingDirectory={StagingDirectory}");
        if (!File.Exists(ConfigPath))
        {
            AppLog.Info("Config file does not exist. Using defaults.");
            var config = new LauncherConfig();
            config.ApplyDefaults();
            AppLog.Info($"Loaded default config. {DescribeConfig(config)}");
            return config;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<LauncherConfig>(json, SerializerOptions) ?? new LauncherConfig();
            config.ApplyDefaults();
            AppLog.Info($"Config loaded. {DescribeConfig(config)}");
            return config;
        }
        catch (JsonException exception)
        {
            AppLog.Error("Config JSON is corrupted. Backing it up and using defaults.", exception);
            // Corrupted JSON: backup the file so the user can potentially fix it
            var backupPath = $"{ConfigPath}.corrupted.{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
            try
            {
                File.Move(ConfigPath, backupPath);
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

    private static string GetConfigDirectory()
        => LauncherPaths.ExecutableDirectory;

    private static string GetStagingDirectory()
        => Path.Combine(LauncherPaths.ExecutableDirectory, "staging");

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
