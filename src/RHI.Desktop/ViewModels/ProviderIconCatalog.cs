using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

internal static class ProviderIconCatalog
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Bitmap? Get(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return null;
        var key = LinuxGameIdentityService.ProviderIconKey(provider);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        try
        {
            var uri = new Uri($"avares://RHI/Assets/providers/{key}.png");
            using var stream = AssetLoader.Open(uri);
            var bitmap = new Bitmap(stream);
            Cache[key] = bitmap;
            return bitmap;
        }
        catch
        {
            Cache[key] = null;
            return null;
        }
    }
}
