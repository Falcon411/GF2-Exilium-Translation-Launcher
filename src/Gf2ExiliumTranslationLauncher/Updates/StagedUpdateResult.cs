namespace Gf2ExiliumTranslationLauncher.Updates;

public sealed record StagedUpdateResult(
    string Message,
    string StagedFilePath,
    GitLabFileMetadata Metadata,
    string GeneratedFileSha256,
    string TargetFilePath,
    string TargetFileSha256,
    string? BackupFileSha256,
    string ExportFilePath,
    string ExportSha256,
    TranslationComparison? Comparison,
    TranslationRecoveryChoice? Recovery = null);

public enum TranslationRecoveryChoice
{
    CurrentTable,
    Backup
}
