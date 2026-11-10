using Gf2ExiliumTranslationLauncher.Diagnostics;

namespace Gf2ExiliumTranslationLauncher.Updates;

public static class FileReplaceService
{
    public static string? ReplaceWithBackup(
        string downloadedFilePath,
        string targetFilePath,
        bool refreshBackup)
    {
        AppLog.Info($"Replacing file with backup support. Source={downloadedFilePath}, Target={targetFilePath}, RefreshBackup={refreshBackup}");
        var targetDirectory = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
            AppLog.Info($"Ensured target directory exists. Directory={targetDirectory}");
        }

        string? backupPath = null;
        string? rollbackPath = null;
        if (File.Exists(targetFilePath))
        {
            backupPath = GetBackupPath(targetFilePath);
            rollbackPath = $"{targetFilePath}.{Guid.NewGuid():N}.rollback.tmp";
            File.Copy(targetFilePath, rollbackPath, overwrite: false);
            AppLog.Info($"Created rollback copy. Rollback={rollbackPath}");

            if (refreshBackup || !File.Exists(backupPath))
            {
                CopyToBackup(targetFilePath, overwrite: true);
            }
            else
            {
                AppLog.Info($"Existing backup kept. Backup={backupPath}");
            }
        }
        else
        {
            AppLog.Warn($"Target file does not exist before replacement. Target={targetFilePath}");
        }

        try
        {
            File.Move(downloadedFilePath, targetFilePath, overwrite: true);
            AppLog.Info($"File replacement finished. Target={targetFilePath}, Backup={backupPath ?? "<none>"}");
            return backupPath;
        }
        catch (Exception exception)
        {
            AppLog.Error($"File replacement failed. Source={downloadedFilePath}, Target={targetFilePath}", exception);
            if (rollbackPath is not null && File.Exists(rollbackPath))
            {
                File.Copy(rollbackPath, targetFilePath, overwrite: true);
                AppLog.Warn($"Rollback copy restored. Target={targetFilePath}, Rollback={rollbackPath}");
            }

            throw;
        }
        finally
        {
            if (rollbackPath is not null && File.Exists(rollbackPath))
            {
                File.Delete(rollbackPath);
                AppLog.Info($"Deleted rollback temp file. Rollback={rollbackPath}");
            }
        }
    }

    internal static async Task PreserveRecoveryCopiesAsync(
        string configDirectory, string target, string targetHash, string backupHash, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(configDirectory, "recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var currentCopy = Path.Combine(directory, Path.GetFileName(target));
        var backupCopy = Path.Combine(directory, Path.GetFileName(GetBackupPath(target)));
        File.Copy(target, currentCopy);
        File.Copy(GetBackupPath(target), backupCopy);
        if (await FileHash.ComputeSha256Async(currentCopy, cancellationToken) != targetHash
            || await FileHash.ComputeSha256Async(backupCopy, cancellationToken) != backupHash
            || await FileHash.ComputeSha256Async(target, cancellationToken) != targetHash
            || await FileHash.ComputeSha256Async(GetBackupPath(target), cancellationToken) != backupHash)
        {
            throw new LauncherException(Localization.AppText.Get("ErrorPreparedSourceChanged"));
        }

        AppLog.Info($"Preserved both translation tables before recovery. Directory={directory}");
    }

    public static string GetBackupPath(string targetFilePath)
        => Path.ChangeExtension(targetFilePath, ".bak");

    internal static async Task RestoreFromBackupsAsync(
        IReadOnlyList<(string Target, string CurrentHash, string BackupHash)> files,
        Action ensureGameNotRunning,
        CancellationToken cancellationToken)
    {
        var prepared = new List<(string Target, string Staged, string Rollback, string ExpectedHash)>();
        var replaced = 0;
        var committed = false;
        try
        {
            foreach (var file in files)
            {
                var staged = $"{file.Target}.{Guid.NewGuid():N}.restore.tmp";
                var rollback = $"{file.Target}.{Guid.NewGuid():N}.restore-rollback.tmp";
                prepared.Add((file.Target, staged, rollback, file.CurrentHash));
                File.Copy(GetBackupPath(file.Target), staged);
                if (await FileHash.ComputeSha256Async(staged, cancellationToken) != file.BackupHash)
                {
                    throw new LauncherException(Localization.AppText.Get("ErrorRestoredChecksumMismatch"));
                }
            }

            foreach (var file in prepared)
            {
                if (await FileHash.ComputeSha256Async(file.Target, cancellationToken) != file.ExpectedHash)
                {
                    throw new LauncherException(Localization.AppText.Get("ErrorPreparedSourceChanged"));
                }
            }

            ensureGameNotRunning();
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in prepared)
            {
                File.Replace(file.Staged, file.Target, file.Rollback);
                replaced++;
            }

            committed = true;
        }
        catch
        {
            // A failed replacement can still produce a rollback file.
            for (var index = Math.Min(replaced, prepared.Count - 1); index >= 0; index--)
            {
                var file = prepared[index];
                if (!File.Exists(file.Rollback))
                {
                    continue;
                }

                try
                {
                    File.Move(file.Rollback, file.Target, overwrite: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    AppLog.Error($"Restore rollback failed. Recovery copy={file.Rollback}", exception);
                }
            }

            throw;
        }
        finally
        {
            foreach (var file in prepared)
            {
                DeleteRestoreTemp(file.Staged);
                if (committed)
                {
                    DeleteRestoreTemp(file.Rollback);
                }
            }
        }
    }

    private static void DeleteRestoreTemp(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Could not clean up restore file. File={path}, Error={exception.Message}");
        }
    }

    public static string CopyToBackup(string targetFilePath, bool overwrite)
    {
        var backupPath = GetBackupPath(targetFilePath);
        File.Copy(targetFilePath, backupPath, overwrite);
        AppLog.Info($"Copied target file to backup. Target={targetFilePath}, Backup={backupPath}, Overwrite={overwrite}");
        return backupPath;
    }
}
