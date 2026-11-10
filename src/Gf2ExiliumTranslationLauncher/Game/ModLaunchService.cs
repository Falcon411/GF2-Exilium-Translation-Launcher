using Gf2ExiliumTranslationLauncher.Configuration;
using Gf2ExiliumTranslationLauncher.Diagnostics;
using Gf2ExiliumTranslationLauncher.Localization;
using Gf2ExiliumTranslationLauncher.Updates;

namespace Gf2ExiliumTranslationLauncher.Game;

internal sealed class ModLaunchService(UpdateService updateService, GameLauncher gameLauncher)
{
    private readonly StartupTranslationService _startupTranslation = new();

    public async Task<string?> PrepareAndLaunchAsync(
        LauncherConfig config,
        Action<string> reportPhase,
        IProgress<double>? downloadProgress,
        CancellationToken cancellationToken)
    {
        ValidateGame(config);
        reportPhase("BusyCheckingGitLab");
        var check = await CheckInstallationAsync(config, cancellationToken);
        if (check.Availability == UpdateAvailability.NotConfigured)
        {
            throw new LauncherException(check.Message);
        }

        StagedUpdateResult? staged = null;
        try
        {
            if (check.Availability == UpdateAvailability.UpdateAvailable)
            {
                var metadata = check.Metadata
                    ?? throw new InvalidOperationException("The translation check returned no package metadata.");
                reportPhase("BusyDownloadingTranslation");
                staged = await Task.Run(() => updateService.StageAsync(
                    config, metadata, check.AlternateMetadata, downloadProgress, cancellationToken), cancellationToken);
            }

            if (check.Availability != UpdateAvailability.LocalFallback
                && (staged?.Metadata ?? check.Metadata)?.IsReadyTable != true)
            {
                reportPhase("ModCheckingStartupPackage");
                await _startupTranslation.ApplyAsync(config, cancellationToken);
            }

            if (staged is not null)
            {
                reportPhase("BusyApplyingTranslation");
                await updateService.ApplyStagedAsync(config, staged, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            reportPhase("ModStartingGame");
            gameLauncher.Launch(config);
            return check.Availability == UpdateAvailability.LocalFallback ? check.Message : null;
        }
        finally
        {
            if (staged is not null)
            {
                foreach (var path in new[] { staged.StagedFilePath, staged.ExportFilePath })
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        AppLog.Warn($"Could not clean up staged file. File={path}, Error={exception.Message}");
                    }
                }
            }
        }
    }

    public async Task<UpdateCheckResult> CheckInstallationAsync(LauncherConfig config, CancellationToken cancellationToken)
    {
        if (!GameInstallPaths.IsInstalled(config.GameExecutablePath))
        {
            throw new LauncherException(AppText.Format("DetailWrongExecutableSelected", GameInstallPaths.ExpectedExecutableName));
        }

        if (!File.Exists(config.LocalFilePath))
        {
            throw new LauncherException(AppText.Format("ErrorOriginalEnUsTableMissing", config.LocalFilePath));
        }

        var check = await updateService.CheckAsync(config, cancellationToken);
        if (check.Availability == UpdateAvailability.UpToDate && check.Metadata?.IsReadyTable == false)
        {
            var startupPath = GameInstallPaths.GetStartupTranslationFilePath(config.GameExecutablePath);
            var (_, isOriginal) = await _startupTranslation.InspectAsync(startupPath, cancellationToken);
            if (isOriginal)
            {
                return check with { Message = $"{check.Message} {AppText.Get("ModStartupInstallPending")}" };
            }
        }

        return check;
    }

    public Task<double?> GetInstalledCoverageAsync(LauncherConfig config, CancellationToken cancellationToken)
        => updateService.GetInstalledCoverageAsync(config, cancellationToken);

    public async Task RestoreAllAsync(LauncherConfig config, CancellationToken cancellationToken)
    {
        ValidateGame(config);
        var mainPath = config.LocalFilePath;
        var startupPath = GameInstallPaths.GetStartupTranslationFilePath(config.GameExecutablePath);
        var backups = new List<(string Target, string CurrentHash, string BackupHash)>();
        if (!File.Exists(mainPath))
        {
            throw new LauncherException(AppText.Format("ErrorOriginalEnUsTableMissing", mainPath));
        }

        var mainBackup = FileReplaceService.GetBackupPath(mainPath);
        var currentHash = await FileHash.ComputeSha256Async(mainPath, cancellationToken);
        var isGenerated = !string.IsNullOrWhiteSpace(config.LastContentSha256)
            && string.Equals(currentHash, config.LastGeneratedFileSha256, StringComparison.OrdinalIgnoreCase);
        if (isGenerated && !File.Exists(mainBackup))
        {
            throw new LauncherException(AppText.Format("ErrorBackupFileMissing", mainBackup));
        }

        if (File.Exists(mainBackup))
        {
            var backupHash = await FileHash.ComputeSha256Async(mainBackup, cancellationToken);
            if (isGenerated && currentHash != backupHash)
            {
                backups.Add((mainPath, currentHash, backupHash));
            }
            else if (string.IsNullOrWhiteSpace(config.LastGeneratedFileSha256) && currentHash != backupHash)
            {
                throw new LauncherException(AppText.Format("ErrorOriginalTableAmbiguous", mainBackup));
            }
        }

        if (!config.LastInstalledReadyTable || File.Exists(FileReplaceService.GetBackupPath(startupPath)))
        {
            var (startupHash, startupOriginal) = await _startupTranslation.InspectAsync(startupPath, cancellationToken);
            if (!startupOriginal)
            {
                backups.Add((startupPath, startupHash, StartupTranslationService.OriginalSha256));
            }
        }

        await FileReplaceService.RestoreFromBackupsAsync(backups,
            () => GameLauncher.EnsureNotRunning(config.GameExecutablePath), cancellationToken);

        config.LastContentSha256 = "";
        config.LastCommitId = "";
        config.LastUpdatedAt = null;
        config.LastComparison = null;
        try
        {
            await updateService.ClearTranslationExportAsync(config, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Could not remove the translation export after reset: {exception.Message}");
        }

        config.LastTranslationExportSha256 = "";
    }

    private static void ValidateGame(LauncherConfig config)
    {
        if (!GameInstallPaths.IsInstalled(config.GameExecutablePath))
        {
            throw new LauncherException(AppText.Format("DetailWrongExecutableSelected", GameInstallPaths.ExpectedExecutableName));
        }

        GameLauncher.EnsureNotRunning(config.GameExecutablePath);
    }
}
