using System.Text.Json;
using Gf2ExiliumTranslationLauncher.Configuration;
using Gf2ExiliumTranslationLauncher.Diagnostics;
using Gf2ExiliumTranslationLauncher.Game;
using Gf2ExiliumTranslationLauncher.Localization;
using Gf2ExiliumTranslationLauncher.Translations.Protobuf;

namespace Gf2ExiliumTranslationLauncher.Updates;

internal sealed class StartupTranslationService
{
    internal const string OriginalSha256 = "2755a211beb031a3169fdb4f19a5e8b41d96316ea11d43c7d5cd4c0d62859f2b";
    private const string DictionaryResource = "Gf2ExiliumTranslationLauncher.Translations.StartupTranslation.ru.json";
    private const uint ChunkDistance = 71;

    private readonly TranslationProtobufWriter _tableWriter = new();

    public static string GetBackupPath(string executablePath)
        => FileReplaceService.GetBackupPath(GameInstallPaths.GetStartupTranslationFilePath(executablePath));

    public async Task<UpdateInstallResult> ApplyAsync(
        LauncherConfig config, CancellationToken cancellationToken)
    {
        var executablePath = config.GameExecutablePath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new LauncherException(AppText.Get("ErrorGameExecutableRequired"));
        }

        GameLauncher.EnsureNotRunning(executablePath);
        var targetPath = GameInstallPaths.GetStartupTranslationFilePath(executablePath);
        var (currentHash, isOriginal) = await InspectAsync(targetPath, cancellationToken);
        if (!isOriginal)
        {
            return new UpdateInstallResult(AppText.Get("StatusStartupTranslationApplied"), FileReplaceService.GetBackupPath(targetPath));
        }

        var preparedPath = $"{targetPath}.{Guid.NewGuid():N}.startup.tmp";
        try
        {
            var rows = await ReadTranslatedRowsAsync(targetPath, cancellationToken);
            await _tableWriter.WriteSingleChunkAsync(preparedPath, rows, ChunkDistance, cancellationToken);
            if (!File.Exists(targetPath)
                || await FileHash.ComputeSha256Async(targetPath, cancellationToken) != currentHash)
            {
                throw new LauncherException(AppText.Get("ErrorStartupSourceChanged"));
            }

            GameLauncher.EnsureNotRunning(executablePath);
            cancellationToken.ThrowIfCancellationRequested();
            var backupPath = FileReplaceService.ReplaceWithBackup(preparedPath, targetPath, refreshBackup: true);
            return new UpdateInstallResult(AppText.Get("StatusStartupTranslationApplied"), backupPath);
        }
        finally
        {
            if (File.Exists(preparedPath))
            {
                File.Delete(preparedPath);
            }
        }
    }

    internal async Task<(string Hash, bool IsOriginal)> InspectAsync(string targetPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(targetPath))
        {
            throw new LauncherException(AppText.Format("ErrorFileNotFound", targetPath));
        }

        using var readLock = File.OpenRead(targetPath);
        var hash = await FileHash.ComputeSha256Async(targetPath, cancellationToken);
        if (hash == OriginalSha256)
        {
            return (hash, true);
        }

        var backup = FileReplaceService.GetBackupPath(targetPath);
        if (!File.Exists(backup))
        {
            throw new LauncherException(AppText.Get("ErrorStartupTableUnsupported"));
        }

        var translated = await ReadTranslatedRowsAsync(backup, cancellationToken);
        var installed = await _tableWriter.ReadRowsAsync(targetPath, cancellationToken);
        if (!translated.SequenceEqual(installed))
        {
            throw new LauncherException(AppText.Get("ErrorStartupTableUnsupported"));
        }

        return (hash, false);
    }

    private async Task<IReadOnlyList<TranslationTableRow>> ReadTranslatedRowsAsync(
        string sourcePath, CancellationToken cancellationToken)
    {
        using var sourceReadLock = File.OpenRead(sourcePath);
        if (await FileHash.ComputeSha256Async(sourcePath, cancellationToken) != OriginalSha256)
        {
            AppLog.Warn($"Unsupported startup translation table. Source={sourcePath}");
            throw new LauncherException(AppText.Get("ErrorStartupTableUnsupported"));
        }

        using var stream = typeof(StartupTranslationService).Assembly.GetManifestResourceStream(DictionaryResource)
            ?? throw new InvalidDataException($"Missing resource: {DictionaryResource}");
        var translations = await JsonSerializer.DeserializeAsync<Dictionary<uint, string>>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException($"Empty resource: {DictionaryResource}");
        var rows = await _tableWriter.ReadRowsAsync(sourcePath, cancellationToken);
        if (rows.Count != translations.Count || rows.Any(row => !translations.ContainsKey(row.Id)))
        {
            throw new InvalidDataException("The startup translation dictionary does not match the source table.");
        }

        return rows.Select(row => row with { Text = translations[row.Id] }).ToArray();
    }
}
