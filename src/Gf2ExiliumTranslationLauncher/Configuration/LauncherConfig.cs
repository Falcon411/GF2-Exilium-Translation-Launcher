using Gf2ExiliumTranslationLauncher.Updates;

namespace Gf2ExiliumTranslationLauncher.Configuration;

public sealed class LauncherConfig
{
    public const string DefaultGitLabBaseUrl = "https://gitlab.com";
    public const string DefaultProjectIdOrPath = "girls-frontline2-ru-team/girls-frontline-team-translate";
    public const string DefaultRef = "WORK";
    public const string StableRef = "master";
    public const string DefaultRemoteFilePath = "translations_release.json";

    public string GitLabBaseUrl { get; set; } = DefaultGitLabBaseUrl;

    public string ProjectIdOrPath { get; set; } = DefaultProjectIdOrPath;

    public string Ref { get; set; } = DefaultRef;

    public string RemoteFilePath { get; set; } = DefaultRemoteFilePath;

    public string PrivateToken { get; set; } = "";

    public string LocalFilePath { get; set; } = "";

    public string GameExecutablePath { get; set; } = "";
    public string GameExecutablePathBeforeReset { get; set; } = "";

    public string GameArguments { get; set; } = "";

    public string UiLanguage { get; set; } = "";


    public string LastContentSha256 { get; set; } = "";

    public string LastCommitId { get; set; } = "";

    public string LastGeneratedFileSha256 { get; set; } = "";

    public DateTimeOffset? LastUpdatedAt { get; set; }
    public bool LastInstalledReadyTable { get; set; }
    public string LastTranslationExportSha256 { get; set; } = "";
    public TranslationComparison? LastComparison { get; set; }

    internal bool UsesPublishedTranslationPackage
        => string.Equals(GitLabBaseUrl.TrimEnd('/'), DefaultGitLabBaseUrl, StringComparison.OrdinalIgnoreCase)
           && string.Equals(ProjectIdOrPath, DefaultProjectIdOrPath, StringComparison.OrdinalIgnoreCase)
           && string.Equals(RemoteFilePath, DefaultRemoteFilePath, StringComparison.Ordinal)
           && (string.Equals(Ref, DefaultRef, StringComparison.OrdinalIgnoreCase)
               || string.Equals(Ref, StableRef, StringComparison.OrdinalIgnoreCase));

    public void ApplyDefaults()
    {
        GitLabBaseUrl = DefaultIfBlank(GitLabBaseUrl, DefaultGitLabBaseUrl);
        ProjectIdOrPath = DefaultIfBlank(ProjectIdOrPath, DefaultProjectIdOrPath);
        Ref = DefaultIfBlank(Ref, DefaultRef);
        RemoteFilePath = DefaultIfBlank(RemoteFilePath, DefaultRemoteFilePath);
        if (UsesPublishedTranslationPackage)
        {
            Ref = DefaultRef;
        }
    }

    private static string DefaultIfBlank(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
