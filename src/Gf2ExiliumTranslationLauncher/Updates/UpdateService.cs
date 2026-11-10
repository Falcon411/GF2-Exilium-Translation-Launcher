using System.Text.Encodings.Web;
using System.Text.Json;
using Gf2ExiliumTranslationLauncher.Configuration;
using Gf2ExiliumTranslationLauncher.Diagnostics;
using Gf2ExiliumTranslationLauncher.Game;
using Gf2ExiliumTranslationLauncher.Localization;
using Gf2ExiliumTranslationLauncher.Translations;
using Gf2ExiliumTranslationLauncher.Translations.Protobuf;

namespace Gf2ExiliumTranslationLauncher.Updates;

public sealed class UpdateService
{
    private readonly GitLabFileClient _gitLabClient;
    private readonly ConfigService _configService;
    private readonly TranslationJsonReader _translationJsonReader;
    private readonly TranslationProtobufWriter _translationProtobufWriter;

    public UpdateService(
        GitLabFileClient gitLabClient,
        ConfigService configService,
        TranslationJsonReader translationJsonReader,
        TranslationProtobufWriter translationProtobufWriter)
    {
        _gitLabClient = gitLabClient;
        _configService = configService;
        _translationJsonReader = translationJsonReader;
        _translationProtobufWriter = translationProtobufWriter;
    }

    public async Task<UpdateCheckResult> CheckAsync(LauncherConfig config, CancellationToken cancellationToken)
    {
        AppLog.Info($"Update check starting. {DescribeUpdateSource(config)}");
        var validationMessage = ValidateForCheck(config);
        if (validationMessage is not null)
        {
            AppLog.Warn($"Update check skipped: config is not complete. Message={validationMessage}");
            return new UpdateCheckResult(UpdateAvailability.NotConfigured, validationMessage, null);
        }

        var localMatches = await MatchesFileHashAsync(
            config.LocalFilePath, config.LastGeneratedFileSha256, cancellationToken);
        GitLabFileMetadata json;
        GitLabFileMetadata? ready = null;
        try
        {
            var jsonTask = _gitLabClient.GetMetadataAsync(config, cancellationToken);
            if (config.UsesPublishedTranslationPackage)
            {
                var readyTask = _gitLabClient.GetReadyTableMetadataAsync(config.LastComparison, cancellationToken);
                await Task.WhenAll(jsonTask, readyTask);
                ready = await readyTask;
            }
            json = await jsonTask;
        }
        catch (Exception exception) when (localMatches && !string.IsNullOrWhiteSpace(config.LastContentSha256)
            && !cancellationToken.IsCancellationRequested
            && exception is LauncherException or HttpRequestException or OperationCanceledException or JsonException)
        {
            AppLog.Warn($"Keeping the verified local translation because GitLab could not be checked: {exception.Message}");
            return new UpdateCheckResult(UpdateAvailability.LocalFallback, AppText.Get("UpdateUsingLocal"), null);
        }

        var comparison = localMatches && ready is not null
            && config.LastComparison is { } saved && saved.Matches(json, ready) ? saved : null;
        var metadata = SelectTranslation(json, ready, comparison);
        var alternate = metadata.IsReadyTable ? json : ready;
        var needsComparison = ready is not null && comparison is null;

        if (localMatches && !needsComparison
            && HashesEqual(config.LastContentSha256, metadata.ContentSha256)
            && config.LastInstalledReadyTable == metadata.IsReadyTable
            && await MatchesFileHashAsync(
                _configService.TranslationExportPath, config.LastTranslationExportSha256, cancellationToken))
        {
            if (!HashesEqual(config.LastCommitId, metadata.LastCommitId)
                || (comparison is not null && ready is not null && comparison.ReadyCommitId != ready.LastCommitId))
            {
                config.LastCommitId = metadata.LastCommitId;
                if (comparison is not null && ready is not null)
                {
                    config.LastComparison = comparison with { ReadyCommitId = ready.LastCommitId };
                }
                await _configService.SaveAsync(config, cancellationToken);
            }

            return new UpdateCheckResult(UpdateAvailability.UpToDate,
                GetComparisonStatus(config), metadata, alternate);
        }

        return new UpdateCheckResult(UpdateAvailability.UpdateAvailable,
            AppText.Get(needsComparison ? "UpdateComparePending" : "UpdateServiceMustRegenerate"),
            metadata, alternate);
    }

    public async Task<StagedUpdateResult> StageAsync(
        LauncherConfig config,
        GitLabFileMetadata metadata,
        GitLabFileMetadata? alternate,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ValidateForInstall(config);

        var targetPath = Path.GetFullPath(config.LocalFilePath);
        var stagingDirectory = _configService.StagingDirectory;
        Directory.CreateDirectory(stagingDirectory);

        var temporaryPrefix = Path.Combine(stagingDirectory, $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}");
        var jsonFile = $"{temporaryPrefix}.json.tmp";
        var readyFile = $"{temporaryPrefix}.ready.tmp";
        var stagedFile = $"{temporaryPrefix}.staged";
        var exportFile = $"{temporaryPrefix}.export.tmp";
        var keepStagedFiles = false;

        try
        {
            var json = metadata.IsReadyTable
                ? alternate ?? throw new InvalidOperationException("The ready table requires a JSON comparison source.")
                : metadata;
            var ready = metadata.IsReadyTable ? metadata : alternate;
            var jsonShare = ready is null ? 1.0 : 0.6;
            await DownloadVerifiedAsync(config, json, jsonFile, value => progress?.Report(value * jsonShare), cancellationToken);
            if (ready is not null)
            {
                await DownloadVerifiedAsync(config, ready, readyFile,
                    value => progress?.Report(jsonShare + value * (1 - jsonShare)), cancellationToken);
            }

            var translationEntries = await _translationJsonReader.ReadAsync(jsonFile, cancellationToken);
            var (originalRows, targetHash, backupHash) =
                await ReadSourceRowsAsync(targetPath, config.LastGeneratedFileSha256, ready?.ContentSha256, cancellationToken);
            var jsonRows = BuildOutputRows(originalRows, translationEntries);
            IReadOnlyList<TranslationTableRow>? readyRows = null;
            TranslationComparison? comparison = null;
            if (ready is not null)
            {
                readyRows = await _translationProtobufWriter.ReadRowsAsync(readyFile, cancellationToken);
                comparison = CompareTranslations(json, ready, jsonRows, readyRows, cancellationToken);
                AppLog.Info($"Compared translation sources. JsonDate={json.UpdatedAt:O}, ReadyDate={ready.UpdatedAt:O}, "
                    + $"DifferentText={comparison.DifferentText}, MissingIds={comparison.MissingFromReady}, ExtraIds={comparison.ExtraInReady}");
            }

            var selected = SelectTranslation(json, ready, comparison);
            AppLog.Info($"Selected translation. ReadyTable={selected.IsReadyTable}, Date={selected.UpdatedAt:O}, "
                + $"Commit={ShortHash(selected.LastCommitId)}");
            var installedRows = selected.IsReadyTable ? readyRows! : jsonRows;
            if (selected.IsReadyTable)
            {
                File.Move(readyFile, stagedFile);
            }
            else
            {
                await _translationProtobufWriter.WriteRowsAsync(stagedFile, installedRows, cancellationToken);
            }

            await WriteTranslationExportAsync(exportFile, originalRows, installedRows, cancellationToken);
            var installedHash = await FileHash.ComputeSha256Async(stagedFile, cancellationToken);
            var exportHash = await FileHash.ComputeSha256Async(exportFile, cancellationToken);
            keepStagedFiles = true;

            return new StagedUpdateResult(AppText.Get("StageUpdatePrepared"), stagedFile,
                selected, installedHash, targetPath, targetHash, backupHash, exportFile, exportHash, comparison);
        }
        finally
        {
            DeleteIfExists(jsonFile);
            DeleteIfExists(readyFile);
            if (!keepStagedFiles)
            {
                DeleteIfExists(stagedFile);
                DeleteIfExists(exportFile);
            }
        }
    }

    private async Task DownloadVerifiedAsync(
        LauncherConfig config, GitLabFileMetadata metadata, string path,
        Action<double> progress, CancellationToken cancellationToken)
    {
        await _gitLabClient.DownloadRawFileAsync(config, metadata, path, progress, cancellationToken);
        var hash = await FileHash.ComputeSha256Async(path, cancellationToken);
        if (!HashesEqual(hash, metadata.ContentSha256))
        {
            AppLog.Error($"Downloaded checksum mismatch. Downloaded={ShortHash(hash)}, Metadata={ShortHash(metadata.ContentSha256)}");
            throw new LauncherException(AppText.Get("ErrorDownloadedChecksumMismatch"));
        }
    }

    private static GitLabFileMetadata SelectTranslation(
        GitLabFileMetadata json, GitLabFileMetadata? ready, TranslationComparison? comparison)
        => ready is not null && ready.UpdatedAt >= json.UpdatedAt && comparison?.ReadyMatchesGame != false
            ? ready
            : json;

    private static TranslationComparison CompareTranslations(
        GitLabFileMetadata json, GitLabFileMetadata ready,
        IReadOnlyList<TranslationTableRow> jsonRows, IReadOnlyList<TranslationTableRow> readyRows,
        CancellationToken cancellationToken)
    {
        // Both readers return rows in ID order, and the JSON overlay preserves that order.
        var jsonIndex = 0;
        var readyIndex = 0;
        var different = 0;
        var missing = 0;
        var extra = 0;
        while (jsonIndex < jsonRows.Count && readyIndex < readyRows.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var jsonRow = jsonRows[jsonIndex];
            var readyRow = readyRows[readyIndex];
            if (jsonRow.Id < readyRow.Id)
            {
                missing++;
                jsonIndex++;
            }
            else if (jsonRow.Id > readyRow.Id)
            {
                extra++;
                readyIndex++;
            }
            else
            {
                if (!string.Equals(jsonRow.Text, readyRow.Text, StringComparison.Ordinal))
                {
                    different++;
                }

                jsonIndex++;
                readyIndex++;
            }
        }

        return new TranslationComparison(json.ContentSha256, ready.ContentSha256, ready.LastCommitId, different,
            missing + jsonRows.Count - jsonIndex, extra + readyRows.Count - readyIndex);
    }

    private static async Task WriteTranslationExportAsync(
        string path, IReadOnlyList<TranslationTableRow> original, IReadOnlyList<TranslationTableRow> installed,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        writer.WriteStartObject();
        for (var index = 0; index < original.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = original[index];
            var row = installed[index];
            if (source.Text != row.Text)
            {
                writer.WriteString(FormattableString.Invariant($"#ID:{row.Id}|{source.Text}"), row.Text);
            }

            if (writer.BytesPending >= 64 * 1024)
            {
                await writer.FlushAsync(cancellationToken);
            }
        }

        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
    }

    internal static string GetComparisonStatus(LauncherConfig config)
    {
        var comparison = config.LastComparison;
        if (comparison is null)
        {
            return AppText.Get("UpdateServiceGeneratedCurrent");
        }

        var source = AppText.Get(config.LastInstalledReadyTable ? "UpdateSourceReady" : "UpdateSourceJson");
        return comparison.ReadyMatchesGame
            ? AppText.Format("UpdateCompared", source, comparison.DifferentText)
            : AppText.Format("UpdateComparedIncompatible", source, comparison.DifferentText,
                comparison.MissingFromReady, comparison.ExtraInReady);
    }

    internal async Task ClearTranslationExportAsync(LauncherConfig config, CancellationToken cancellationToken)
    {
        if (await MatchesFileHashAsync(
            _configService.TranslationExportPath, config.LastTranslationExportSha256, cancellationToken))
        {
            DeleteIfExists(_configService.TranslationExportPath);
        }
    }

    public async Task<UpdateInstallResult> ApplyStagedAsync(
        LauncherConfig config,
        StagedUpdateResult stagedUpdate,
        CancellationToken cancellationToken)
    {
        AppLog.Info($"Apply staged update starting. LocalFile={config.LocalFilePath}, StagedFile={stagedUpdate.StagedFilePath}, GeneratedHash={ShortHash(stagedUpdate.GeneratedFileSha256)}");
        ValidateForInstall(config);

        if (!File.Exists(stagedUpdate.StagedFilePath))
        {
            AppLog.Warn($"Apply failed: staged file is missing. StagedFile={stagedUpdate.StagedFilePath}");
            throw new LauncherException(AppText.Get("ErrorPreparedFileMissing"));
        }

        var stagedHash = await FileHash.ComputeSha256Async(stagedUpdate.StagedFilePath, cancellationToken);
        if (!HashesEqual(stagedHash, stagedUpdate.GeneratedFileSha256))
        {
            AppLog.Error($"Prepared checksum mismatch. Actual={ShortHash(stagedHash)}, Expected={ShortHash(stagedUpdate.GeneratedFileSha256)}");
            throw new LauncherException(AppText.Get("ErrorPreparedChecksumMismatch"));
        }
        AppLog.Info($"Prepared checksum verified. Hash={ShortHash(stagedHash)}");

        var targetPath = Path.GetFullPath(config.LocalFilePath);
        var backupPath = FileReplaceService.GetBackupPath(targetPath);
        if (!string.Equals(targetPath, stagedUpdate.TargetFilePath, StringComparison.OrdinalIgnoreCase)
            || !await MatchesFileHashAsync(targetPath, stagedUpdate.TargetFileSha256, cancellationToken)
            || (stagedUpdate.BackupFileSha256 is { } backupHash
                && !await MatchesFileHashAsync(backupPath, backupHash, cancellationToken)))
        {
            AppLog.Warn($"Apply skipped: the source table changed after staging. Target={targetPath}");
            throw new LauncherException(AppText.Get("ErrorPreparedSourceChanged"));
        }

        EnsureGameNotRunning(config);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await MatchesFileHashAsync(stagedUpdate.ExportFilePath, stagedUpdate.ExportSha256, cancellationToken))
        {
            throw new LauncherException(AppText.Get("ErrorPreparedChecksumMismatch"));
        }

        string? installedBackupPath = backupPath;
        if (stagedUpdate.BackupFileSha256 is null || !HashesEqual(stagedHash, stagedUpdate.TargetFileSha256))
        {
            installedBackupPath = FileReplaceService.ReplaceWithBackup(
                stagedUpdate.StagedFilePath,
                targetPath,
                refreshBackup: stagedUpdate.BackupFileSha256 is null);
        }

        config.LastContentSha256 = stagedUpdate.Metadata.ContentSha256;
        config.LastCommitId = stagedUpdate.Metadata.LastCommitId;
        config.LastGeneratedFileSha256 = stagedUpdate.GeneratedFileSha256;
        config.LastUpdatedAt = DateTimeOffset.UtcNow;
        config.LastInstalledReadyTable = stagedUpdate.Metadata.IsReadyTable;
        config.LastTranslationExportSha256 = stagedUpdate.ExportSha256;
        config.LastComparison = stagedUpdate.Comparison;
        await _configService.SaveAsync(config);
        File.Move(stagedUpdate.ExportFilePath, _configService.TranslationExportPath, overwrite: true);

        AppLog.Info($"Apply staged update finished. Backup={installedBackupPath ?? "<none>"}, LastUpdatedAt={config.LastUpdatedAt:O}");
        return new UpdateInstallResult(AppText.Get("InstallTranslationApplied"), installedBackupPath);
    }

    internal async Task<double?> GetInstalledCoverageAsync(LauncherConfig config, CancellationToken cancellationToken)
    {
        var path = config.LocalFilePath;
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(config.LastContentSha256))
        {
            return null;
        }

        var backup = FileReplaceService.GetBackupPath(path);
        if (!File.Exists(path) || !File.Exists(backup))
        {
            return null;
        }

        using var installedReadLock = File.OpenRead(path);
        using var originalReadLock = File.OpenRead(backup);
        if (!await MatchesFileHashAsync(path, config.LastGeneratedFileSha256, cancellationToken))
        {
            return null;
        }

        var original = await _translationProtobufWriter.ReadRowsAsync(backup, cancellationToken);
        var installed = await _translationProtobufWriter.ReadRowsAsync(path, cancellationToken);
        if (original.Count == 0 || installed.Count != original.Count)
        {
            return null;
        }

        var translated = 0;
        for (var index = 0; index < original.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (installed[index].Id != original[index].Id)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(installed[index].Text) && installed[index].Text != original[index].Text)
            {
                translated++;
            }
        }

        return (double)translated / original.Count;
    }

    private static void DeleteIfExists(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            AppLog.Info($"Deleted temp file. File={filePath}");
        }
    }

    private async Task<(IReadOnlyList<TranslationTableRow> Rows, string TargetHash, string? BackupHash)>
        ReadSourceRowsAsync(
            string targetPath, string lastGeneratedHash, string? readyHash, CancellationToken cancellationToken)
    {
        if (!File.Exists(targetPath))
        {
            throw new LauncherException(AppText.Format("ErrorOriginalEnUsTableMissing", targetPath));
        }

        // Keep the table stable while hashing and reading it.
        using var targetReadLock = File.OpenRead(targetPath);
        var targetHash = await FileHash.ComputeSha256Async(targetPath, cancellationToken);
        var backupPath = FileReplaceService.GetBackupPath(targetPath);
        var isGenerated = HashesEqual(targetHash, lastGeneratedHash) || HashesEqual(targetHash, readyHash);
        var hasBackup = File.Exists(backupPath);
        if (isGenerated && !hasBackup)
        {
            throw new LauncherException(AppText.Format("ErrorBackupFileMissing", backupPath));
        }

        if (hasBackup && (isGenerated || string.IsNullOrWhiteSpace(lastGeneratedHash)))
        {
            using var backupReadLock = File.OpenRead(backupPath);
            var backupHash = await FileHash.ComputeSha256Async(backupPath, cancellationToken);
            if (!isGenerated && !HashesEqual(targetHash, backupHash))
            {
                AppLog.Warn($"Cannot identify the original table. Target={targetPath}, Backup={backupPath}");
                throw new LauncherException(AppText.Format("ErrorOriginalTableAmbiguous", backupPath));
            }

            AppLog.Info($"Using backup rows as translation source. Backup={backupPath}");
            var backupRows = await _translationProtobufWriter.ReadRowsAsync(backupPath, cancellationToken);
            return (backupRows, targetHash, backupHash);
        }

        AppLog.Info($"Using current game table as translation source. Target={targetPath}");
        var targetRows = await _translationProtobufWriter.ReadRowsAsync(targetPath, cancellationToken);
        return (targetRows, targetHash, null);
    }

    private static async Task<bool> MatchesFileHashAsync(
        string path, string expectedHash, CancellationToken cancellationToken)
        => !string.IsNullOrWhiteSpace(expectedHash)
           && File.Exists(path)
           && HashesEqual(await FileHash.ComputeSha256Async(path, cancellationToken), expectedHash);

    private static void EnsureGameNotRunning(LauncherConfig config)
        => GameLauncher.EnsureNotRunning(string.IsNullOrWhiteSpace(config.GameExecutablePath)
            ? GameInstallPaths.ExpectedExecutableName
            : config.GameExecutablePath);

    private static IReadOnlyList<TranslationTableRow> BuildOutputRows(
        IReadOnlyList<TranslationTableRow> sourceRows,
        IReadOnlyList<TranslationEntry> translationEntries)
    {
        var translationsById = new Dictionary<uint, TranslationEntry>();
        var duplicateIds = 0;
        foreach (var entry in translationEntries)
        {
            if (entry.Id < 0)
            {
                AppLog.Warn($"Cannot build output rows: negative translation ID {entry.Id}.");
                throw new InvalidDataException(AppText.Format("ErrorNegativeTranslationId", entry.Id));
            }

            if (translationsById.ContainsKey((uint)entry.Id))
            {
                duplicateIds++;
            }

            translationsById[(uint)entry.Id] = entry;
        }

        var translatedRows = 0;
        var outputRows = new List<TranslationTableRow>(sourceRows.Count);
        foreach (var row in sourceRows)
        {
            if (translationsById.TryGetValue(row.Id, out var entry))
            {
                translatedRows++;
                outputRows.Add(new TranslationTableRow(row.Id, entry.Translation ?? entry.Original ?? string.Empty));
                continue;
            }

            outputRows.Add(row);
        }

        AppLog.Info($"Built output rows. SourceRows={sourceRows.Count}, TranslationEntries={translationEntries.Count}, UniqueTranslationIds={translationsById.Count}, DuplicateTranslationIds={duplicateIds}, TranslatedRows={translatedRows}, UnchangedRows={outputRows.Count - translatedRows}");
        return outputRows;
    }

    private static string? ValidateForCheck(LauncherConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.GitLabBaseUrl))
        {
            return AppText.Get("ValidationGitLabBaseUrl");
        }

        if (string.IsNullOrWhiteSpace(config.ProjectIdOrPath))
        {
            return AppText.Get("ValidationProjectId");
        }

        if (string.IsNullOrWhiteSpace(config.RemoteFilePath))
        {
            return AppText.Get("ValidationRemoteFile");
        }

        if (string.IsNullOrWhiteSpace(config.LocalFilePath))
        {
            return AppText.Get("ValidationLocalFile");
        }

        return null;
    }

    private static void ValidateForInstall(LauncherConfig config)
    {
        var validationMessage = ValidateForCheck(config);
        if (validationMessage is not null)
        {
            AppLog.Warn($"Install validation failed. Message={validationMessage}");
            throw new LauncherException(validationMessage);
        }
    }

    private static bool HashesEqual(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string ShortHash(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "<empty>"
            : value.Trim().Length <= 12
                ? value.Trim()
                : value.Trim()[..12];

    private static string DescribeUpdateSource(LauncherConfig config)
        => $"GitLabBaseUrl={config.GitLabBaseUrl}, Project={config.ProjectIdOrPath}, Ref={config.Ref}, "
           + $"RemoteFile={config.RemoteFilePath}, LocalFile={AppLog.DescribePath(config.LocalFilePath)}, "
           + $"TokenPresent={!string.IsNullOrWhiteSpace(config.PrivateToken)}";
}
