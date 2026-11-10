using System.Security.Cryptography;
using Gf2ExiliumTranslationLauncher.Diagnostics;

namespace Gf2ExiliumTranslationLauncher.Updates;

public static class FileHash
{
    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        AppLog.Info($"Computing SHA-256. File={AppLog.DescribePath(path)}");
        await using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        var value = Convert.ToHexString(hash).ToLowerInvariant();
        AppLog.Info($"Computed SHA-256. File={path}, Sha256={ShortHash(value)}");
        return value;
    }

    private static string ShortHash(string value)
        => value.Length <= 12 ? value : value[..12];
}
