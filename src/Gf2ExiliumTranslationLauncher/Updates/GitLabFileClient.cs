using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gf2ExiliumTranslationLauncher.Configuration;
using Gf2ExiliumTranslationLauncher.Diagnostics;
using Gf2ExiliumTranslationLauncher.Localization;

namespace Gf2ExiliumTranslationLauncher.Updates;

public sealed class GitLabFileClient
{
    private const string PrivateTokenHeader = "PRIVATE-TOKEN";
    private const string PublishedRelease = "https://gitlab.com/api/v4/projects/83468208/releases/translations-latest";
    private const string PublishedPackage =
        "https://gitlab.com/api/v4/projects/83468208/packages/generic/translations/latest/translations_release.json";
    private const string JsonProjectApi = "https://gitlab.com/api/v4/projects/83468208";
    private const string ReadyProjectApi = "https://gitlab.com/api/v4/projects/81099896";
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<Uri, DateTimeOffset> _retryAfter = new();
    private GitLabFileMetadata? _readyMetadata;

    public GitLabFileClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<GitLabFileMetadata> GetMetadataAsync(LauncherConfig config, CancellationToken cancellationToken)
    {
        if (config.UsesPublishedTranslationPackage)
        {
            return await GetPublishedMetadataAsync(config, cancellationToken);
        }

        var url = BuildFileUrl(config, raw: false);
        AppLog.Info(
            $"GitLab metadata request starting. Url={url}, TokenPresent={!string.IsNullOrWhiteSpace(config.PrivateToken)}");
        try
        {
            using var response = await SendAsync(HttpMethod.Head, url, cancellationToken, config);
            AppLog.Info(
                $"GitLab metadata response received. StatusCode={(int)response.StatusCode}, Reason={response.ReasonPhrase}");
            if (!response.IsSuccessStatusCode)
            {
                var errorInfo = response.ReasonPhrase;
                AppLog.Warn(
                    $"GitLab metadata request failed. StatusCode={(int)response.StatusCode}, Reason={errorInfo}");
                throw new LauncherException(AppText.Format(
                    "ErrorGitLabStatusCheck",
                    (int)response.StatusCode,
                    errorInfo));
            }

            var contentSha256 = GetRequiredHeader(response, "X-Gitlab-Content-Sha256");
            var lastCommitId = GetHeader(response, "X-Gitlab-Last-Commit-Id");
            var blobId = GetHeader(response, "X-Gitlab-Blob-Id");
            var size = TryParseLong(GetHeader(response, "X-Gitlab-Size"));

            // GitLab's file metadata describes the Git blob (the pointer for LFS files).
            // LFS pointers are smaller than 1024 bytes, so large JSON files need no extra request.
            if (size is null or > 0 and < 1024)
            {
                var pointer = await DownloadRawTextAsync(config, cancellationToken, resolveLfs: false);
                if (pointer.StartsWith("version https://git-lfs.github.com/spec/v1\n", StringComparison.Ordinal)
                    || pointer.StartsWith("version https://hawser.github.com/spec/v1\n", StringComparison.Ordinal))
                {
                    var pointerHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pointer)));
                    if (!string.Equals(pointerHash, contentSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new LauncherException(AppText.Get("ErrorDownloadedChecksumMismatch"));
                    }

                    var lines = pointer.Split('\n');
                    var oidLines = lines.Where(line => line.StartsWith("oid sha256:", StringComparison.Ordinal))
                        .ToArray();
                    var sizeLines = lines.Where(line => line.StartsWith("size ", StringComparison.Ordinal)).ToArray();
                    if (oidLines.Length != 1 || sizeLines.Length != 1
                                             || oidLines[0].Length != 75 || !oidLines[0][11..].All(char.IsAsciiHexDigit)
                                             || !long.TryParse(sizeLines[0].AsSpan(5), NumberStyles.None,
                                                 CultureInfo.InvariantCulture, out var objectSize))
                    {
                        throw new InvalidDataException(
                            "Invalid Git LFS pointer: expected a SHA-256 object ID and file size.");
                    }

                    contentSha256 = oidLines[0][11..];
                    size = objectSize;
                    AppLog.Info($"Git LFS metadata resolved. ContentSha256={ShortHash(contentSha256)}, Size={size}");
                }
            }

            AppLog.Info(
                $"GitLab metadata read. ContentSha256={ShortHash(contentSha256)}, LastCommitId={ShortHash(lastCommitId)}, BlobId={ShortHash(blobId)}, Size={size?.ToString() ?? "<unknown>"}");
            return new GitLabFileMetadata(contentSha256, lastCommitId, blobId, size);
        }
        catch (HttpRequestException ex)
        {
            AppLog.Error($"GitLab metadata network error. Url={url}", ex);
            throw new LauncherException(AppText.Format("ErrorNetworkGitLabCheck", ex.Message), ex);
        }
    }

    internal async Task<GitLabFileMetadata> GetReadyTableMetadataAsync(
        TranslationComparison? previous, CancellationToken cancellationToken)
    {
        var url = $"{ReadyProjectApi}/repository/commits"
            + $"?ref_name=main&path={Game.GameInstallPaths.TranslationFileName}&per_page=1";
        using var response = await SendAsync(HttpMethod.Get, new Uri(url), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new LauncherException(AppText.Format(
                "ErrorGitLabStatusCheck", (int)response.StatusCode, response.ReasonPhrase));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0
            || !document.RootElement[0].TryGetProperty("id", out var id)
            || !document.RootElement[0].TryGetProperty("committed_date", out var date)
            || !TryReadDate(date, out var updatedAt))
        {
            throw new LauncherException(AppText.Get("ErrorTranslationDateMissing"));
        }

        var commit = id.GetString() ?? "";
        if (commit.Length != 40 || !commit.All(char.IsAsciiHexDigit))
        {
            throw new LauncherException(AppText.Get("ErrorTranslationDateMissing"));
        }

        // GitLab rate-limits file metadata requests for large blobs. Only fetch
        // the checksum when the file's commit has changed.
        if (_readyMetadata is { } cached && cached.LastCommitId == commit)
        {
            return cached;
        }

        if (previous is not null && previous.ReadyCommitId == commit
            && previous.ReadySha256.Length == 64 && previous.ReadySha256.All(char.IsAsciiHexDigit))
        {
            return new GitLabFileMetadata(previous.ReadySha256, commit, "", null, updatedAt, IsReadyTable: true);
        }

        var metadata = await GetMetadataAsync(ReadyTableSource(commit), cancellationToken);
        _readyMetadata = metadata with { LastCommitId = commit, IsReadyTable = true, UpdatedAt = updatedAt };
        return _readyMetadata;
    }

    private async Task<DateTimeOffset> ReadCommitDateAsync(
        string projectApi, string commit, LauncherConfig config, CancellationToken cancellationToken)
    {
        if (commit.Length != 40 || !commit.All(char.IsAsciiHexDigit))
        {
            throw new LauncherException(AppText.Get("ErrorTranslationDateMissing"));
        }

        var url = new Uri($"{projectApi}/repository/commits/{commit}");
        using var response = await SendAsync(HttpMethod.Get, url, cancellationToken, config);
        if (!response.IsSuccessStatusCode)
        {
            throw new LauncherException(AppText.Format(
                "ErrorGitLabStatusCheck", (int)response.StatusCode, response.ReasonPhrase));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.TryGetProperty("committed_date", out var value)
            && TryReadDate(value, out var date))
        {
            return date;
        }

        throw new LauncherException(AppText.Get("ErrorTranslationDateMissing"));
    }

    private static bool TryReadDate(JsonElement value, out DateTimeOffset date)
        => DateTimeOffset.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static LauncherConfig ReadyTableSource(string reference) => new()
    {
        ProjectIdOrPath = "81099896",
        Ref = reference,
        RemoteFilePath = Game.GameInstallPaths.TranslationFileName
    };

    private async Task<GitLabFileMetadata> GetPublishedMetadataAsync(
        LauncherConfig config, CancellationToken cancellationToken)
    {
        AppLog.Info($"Reading published translation release. Url={PublishedRelease}");
        try
        {
            using var response = await SendAsync(HttpMethod.Get, new Uri(PublishedRelease), cancellationToken, config);
            if (!response.IsSuccessStatusCode)
            {
                throw new LauncherException(AppText.Format(
                    "ErrorGitLabStatusCheck", (int)response.StatusCode, response.ReasonPhrase));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var release = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var description = release.RootElement.TryGetProperty("description", out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            var checksum = Regex.Match(
                description, @"^SHA-256:[ \t]*\x60([a-fA-F0-9]{64})\x60[ \t]*\r?$", RegexOptions.Multiline);
            if (!checksum.Success)
            {
                throw new LauncherException(AppText.Get("ErrorPublishedChecksumMissing"));
            }

            var commit = Regex.Match(description, @"\bWORK commit \[([a-fA-F0-9]{40})\]");
            DateTimeOffset updatedAt;
            if (commit.Success)
            {
                updatedAt = await ReadCommitDateAsync(JsonProjectApi, commit.Groups[1].Value, config, cancellationToken);
            }
            else if (!release.RootElement.TryGetProperty("released_at", out var releasedAt)
                     || !TryReadDate(releasedAt, out updatedAt))
            {
                throw new LauncherException(AppText.Get("ErrorTranslationDateMissing"));
            }

            return new GitLabFileMetadata(checksum.Groups[1].Value, commit.Groups[1].Value, "", null, updatedAt);
        }
        catch (HttpRequestException exception)
        {
            throw new LauncherException(AppText.Format("ErrorNetworkGitLabCheck", exception.Message), exception);
        }
        catch (JsonException exception)
        {
            throw new LauncherException(AppText.Get("ErrorPublishedChecksumMissing"), exception);
        }
    }

    public async Task DownloadRawFileAsync(
        LauncherConfig config,
        GitLabFileMetadata metadata,
        string targetPath,
        Action<double>? progress,
        CancellationToken cancellationToken)
    {
        if (metadata.IsReadyTable)
        {
            config = ReadyTableSource(metadata.LastCommitId);
        }

        var url = config.UsesPublishedTranslationPackage ? new Uri(PublishedPackage) : BuildFileUrl(config, raw: true);
        AppLog.Info(
            $"GitLab raw download starting. Url={url}, Target={targetPath}, TokenPresent={!string.IsNullOrWhiteSpace(config.PrivateToken)}");
        try
        {
            using var response = await SendAsync(HttpMethod.Get, url, cancellationToken, config);
            AppLog.Info(
                $"GitLab raw download response received. StatusCode={(int)response.StatusCode}, Reason={response.ReasonPhrase}");
            if (!response.IsSuccessStatusCode)
            {
                var errorInfo = response.ReasonPhrase;
                AppLog.Warn($"GitLab raw download failed. StatusCode={(int)response.StatusCode}, Reason={errorInfo}");
                throw new LauncherException(AppText.Format(
                    "ErrorGitLabStatusDownload",
                    (int)response.StatusCode,
                    errorInfo));
            }

            var contentLength = response.Content.Headers.ContentLength;
            AppLog.Info(
                $"GitLab raw download content length. ContentLength={contentLength?.ToString() ?? "<unknown>"}");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[128 * 1024];
            long totalRead = 0;

            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;

                if (contentLength is > 0)
                {
                    progress?.Invoke(Math.Clamp((double)totalRead / contentLength.Value, 0, 1));
                }
            }

            progress?.Invoke(1);
            AppLog.Info($"GitLab raw download finished. Target={targetPath}, Bytes={totalRead}");
        }
        catch (HttpRequestException ex)
        {
            AppLog.Error($"GitLab raw download network error. Url={url}, Target={targetPath}", ex);
            throw new LauncherException(AppText.Format("ErrorNetworkGitLabDownload", ex.Message), ex);
        }
    }

    public async Task<string> DownloadRawTextAsync(
        LauncherConfig config,
        CancellationToken cancellationToken,
        bool resolveLfs = true)
    {
        var url = BuildFileUrl(config, raw: true, resolveLfs);
        AppLog.Info(
            $"GitLab raw text download starting. Url={url}, TokenPresent={!string.IsNullOrWhiteSpace(config.PrivateToken)}");
        try
        {
            using var response = await SendAsync(HttpMethod.Get, url, cancellationToken, config);
            AppLog.Info(
                $"GitLab raw text response received. StatusCode={(int)response.StatusCode}, Reason={response.ReasonPhrase}");
            if (!response.IsSuccessStatusCode)
            {
                var errorInfo = response.ReasonPhrase;
                AppLog.Warn(
                    $"GitLab raw text download failed. StatusCode={(int)response.StatusCode}, Reason={errorInfo}");
                throw new LauncherException(AppText.Format(
                    "ErrorGitLabStatusDownload",
                    (int)response.StatusCode,
                    errorInfo));
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            AppLog.Info($"GitLab raw text download finished. Characters={content.Length}");
            return content;
        }
        catch (HttpRequestException ex)
        {
            AppLog.Error($"GitLab raw text download network error. Url={url}", ex);
            throw new LauncherException(AppText.Format("ErrorNetworkGitLabDownload", ex.Message), ex);
        }
    }


    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, Uri url, CancellationToken cancellationToken, LauncherConfig? config = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_retryAfter.TryGetValue(url, out var retryAt))
            {
                var remaining = retryAt - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    throw RateLimitError(remaining);
                }

                _retryAfter.TryRemove(new KeyValuePair<Uri, DateTimeOffset>(url, retryAt));
            }

            using var request = new HttpRequestMessage(method, url);
            if (config is not null)
            {
                ApplyAuthentication(request, config);
            }

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                return response;
            }

            var now = DateTimeOffset.UtcNow;
            TimeSpan delay;
            using (response)
            {
                var retryAfter = response.Headers.RetryAfter;
                delay = retryAfter?.Delta
                    ?? (retryAfter?.Date is { } date ? date - now : TimeSpan.FromSeconds(attempt == 0 ? 3 : 30));
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }
            }

            retryAt = now + delay;
            _retryAfter.AddOrUpdate(url, retryAt, (_, previous) => previous > retryAt ? previous : retryAt);
            AppLog.Warn($"GitLab rate-limited {method} {url}. Attempt={attempt + 1}, RetryAfter={delay.TotalSeconds:F1}s");
            if (attempt > 0 || delay > TimeSpan.FromSeconds(5))
            {
                throw RateLimitError(delay);
            }

            await Task.Delay(delay + TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    private static LauncherException RateLimitError(TimeSpan wait)
        => new(AppText.Format("ErrorGitLabRateLimited", Math.Max(1, Math.Ceiling(wait.TotalSeconds))));

    private static Uri BuildFileUrl(LauncherConfig config, bool raw, bool resolveLfs = true)
    {
        var baseUrl = config.GitLabBaseUrl.Trim().TrimEnd('/');
        var project = Uri.EscapeDataString(config.ProjectIdOrPath.Trim());
        var file = Uri.EscapeDataString(config.RemoteFilePath.Trim());
        var fileSuffix = raw ? "/raw" : "";
        var refValue = string.IsNullOrWhiteSpace(config.Ref) ? "HEAD" : config.Ref.Trim();

        var path = $"api/v4/projects/{project}/repository/files/{file}{fileSuffix}";
        var query = $"?ref={Uri.EscapeDataString(refValue)}";
        if (raw)
        {
            query += resolveLfs ? "&lfs=true" : "&lfs=false";
        }

        return new Uri($"{baseUrl}/{path}{query}");
    }

    private static void ApplyAuthentication(HttpRequestMessage request, LauncherConfig config)
    {
        var token = config.PrivateToken.Trim();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Add(PrivateTokenHeader, token);
        }
    }

    private static string GetRequiredHeader(HttpResponseMessage response, string headerName)
    {
        var value = GetHeader(response, headerName);
        if (string.IsNullOrWhiteSpace(value))
        {
            AppLog.Warn($"GitLab response is missing required header. Header={headerName}");
            throw new LauncherException(AppText.Format("ErrorGitLabHeaderMissing", headerName));
        }

        return value;
    }

    private static string GetHeader(HttpResponseMessage response, string headerName)
    {
        return response.Headers.TryGetValues(headerName, out var values)
            ? values.FirstOrDefault() ?? ""
            : "";
    }

    private static long? TryParseLong(string value)
    {
        return long.TryParse(value, out var result) ? result : null;
    }

    private static string ShortHash(string value)
        => string.IsNullOrWhiteSpace(value)
            ? "<empty>"
            : value.Trim().Length <= 12
                ? value.Trim()
                : value.Trim()[..12];
}