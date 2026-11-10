namespace Gf2ExiliumTranslationLauncher.Updates;

public sealed record GitLabFileMetadata(
    string ContentSha256,
    string LastCommitId,
    string BlobId,
    long? Size,
    DateTimeOffset? UpdatedAt = null,
    bool IsReadyTable = false);
