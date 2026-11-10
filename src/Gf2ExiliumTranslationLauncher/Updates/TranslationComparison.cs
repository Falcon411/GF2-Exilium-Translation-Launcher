namespace Gf2ExiliumTranslationLauncher.Updates;

public sealed record TranslationComparison(
    string JsonSha256,
    string ReadySha256,
    string ReadyCommitId,
    int DifferentText,
    int MissingFromReady,
    int ExtraInReady)
{
    public bool ReadyMatchesGame => MissingFromReady == 0 && ExtraInReady == 0;

    public bool Matches(GitLabFileMetadata json, GitLabFileMetadata ready)
        => string.Equals(JsonSha256, json.ContentSha256, StringComparison.OrdinalIgnoreCase)
           && string.Equals(ReadySha256, ready.ContentSha256, StringComparison.OrdinalIgnoreCase);
}
