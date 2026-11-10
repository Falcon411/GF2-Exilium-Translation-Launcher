using System.Text.Json;
using Gf2ExiliumTranslationLauncher.Diagnostics;

namespace Gf2ExiliumTranslationLauncher.Game;

internal sealed record OfficialBanner(Uri Image, Uri Link);

internal static class OfficialBannerCatalog
{
    private const string HeroEndpoint = "https://gf2-launcher-us.sunborngame.com/flexible_config";
    private const string EventsEndpoint = "https://gf2-web-us-api.sunborngame.com/website/rotation";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(8),
        MaxResponseContentBufferSize = 8 * 1024 * 1024
    };

    public static async Task<Uri?> GetHeroImageAsync(CancellationToken cancellationToken)
    {
        using var document = await FetchAsync(Client, HeroEndpoint, cancellationToken);
        if (document is null
            || !TryProperty(document.RootElement, "data", out var data)
            || !TryProperty(data, "flexible_config", out var config)
            || !TryProperty(config, "nexon_plug", out var plug))
        {
            return null;
        }

        try
        {
            // The official endpoint puts the launcher configuration in a JSON string.
            using var embedded = plug.ValueKind == JsonValueKind.String
                ? JsonDocument.Parse(plug.GetString()!)
                : null;
            var launcher = embedded is null ? plug : embedded.RootElement;
            return TryProperty(launcher, "head_pic", out var picture)
                ? ReadImageUri(picture, "pic_url")
                : null;
        }
        catch (JsonException exception)
        {
            AppLog.Warn($"Could not read official hero banner: {exception.Message}");
            return null;
        }
    }

    public static async Task<IReadOnlyList<OfficialBanner>?> GetEventsAsync(CancellationToken cancellationToken)
    {
        using var document = await FetchAsync(Client, EventsEndpoint, cancellationToken, retryTransient: true);
        if (document is null
            || !TryProperty(document.RootElement, "data", out var data)
            || !TryProperty(data, "list", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return list.EnumerateArray()
            .Where(entry => !TryProperty(entry, "hasUrl", out var hasUrl) || hasUrl.ValueKind != JsonValueKind.False)
            .Select(entry => ReadBanner(entry, "LauncherUrl", "JumpUrl"))
            .OfType<OfficialBanner>()
            .DistinctBy(banner => banner.Image)
            .ToArray();
    }

    public static Task<byte[]> GetImageAsync(Uri image, CancellationToken cancellationToken)
        => Client.GetByteArrayAsync(image, cancellationToken);

    private static async Task<JsonDocument?> FetchAsync(
        HttpClient client, string url, CancellationToken cancellationToken, bool retryTransient = false)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var bytes = await client.GetByteArrayAsync(url, cancellationToken);
                return JsonDocument.Parse(bytes);
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                AppLog.Warn($"Could not read official launcher banners from {url}: {exception.Message}");
                if (!retryTransient || !IsTransient(exception))
                {
                    return null;
                }

                retryTransient = false;
            }
        }

        return null;
    }

    private static bool IsTransient(Exception exception)
        => exception is OperationCanceledException
           || exception is HttpRequestException request
              && (request.StatusCode is null or System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests
                  || (int)request.StatusCode >= 500);

    private static OfficialBanner? ReadBanner(JsonElement element, string imageKey, string linkKey)
    {
        var image = ReadImageUri(element, imageKey);
        var link = ReadWebUri(element, linkKey);
        return image is not null && link is not null ? new OfficialBanner(image, link) : null;
    }

    private static Uri? ReadImageUri(JsonElement element, string key)
        => ReadWebUri(element, key) is { } image
           && image.Host is "gf2-us-cdn.sunborngame.com" or "gf2-us-image-cdn.sunborngame.com"
           && image.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? image
            : null;

    private static Uri? ReadWebUri(JsonElement element, string key)
        => TryProperty(element, key, out var value) && value.ValueKind == JsonValueKind.String
           && Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Host.Length > 0
            ? uri
            : null;

    private static bool TryProperty(JsonElement element, string key, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out value);
    }
}
