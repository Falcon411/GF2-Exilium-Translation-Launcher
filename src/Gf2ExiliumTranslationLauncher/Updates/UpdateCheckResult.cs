namespace Gf2ExiliumTranslationLauncher.Updates;

public enum UpdateAvailability
{
    NotConfigured,
    UpToDate,
    UpdateAvailable,
    LocalFallback
}

public sealed record UpdateCheckResult(
    UpdateAvailability Availability,
    string Message,
    GitLabFileMetadata? Metadata,
    GitLabFileMetadata? AlternateMetadata = null);
