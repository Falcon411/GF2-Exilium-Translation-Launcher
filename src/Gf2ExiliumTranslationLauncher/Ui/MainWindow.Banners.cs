using System.Buffers.Binary;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Gf2ExiliumTranslationLauncher.Diagnostics;
using Gf2ExiliumTranslationLauncher.Game;
using Gf2ExiliumTranslationLauncher.Localization;

namespace Gf2ExiliumTranslationLauncher.Ui;

internal sealed partial class MainWindow
{
    private sealed record BannerArtwork(Bitmap Image, Uri Link);

    private readonly CancellationTokenSource _bannerLifetime = new();
    private readonly Button _eventLink = new();
    private readonly Image _hero = new() { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
    private readonly Image _event = new() { Stretch = Stretch.UniformToFill };
    private readonly Button _previousEvent = new() { IsVisible = false };
    private readonly Button _nextEvent = new() { IsVisible = false };
    private readonly Border _eventPosition = new() { IsVisible = false };
    private readonly TextBlock _eventPositionText = new() { Foreground = TextMain, FontSize = 11 };
    private IReadOnlyList<BannerArtwork> _eventBanners = [];
    private int _eventBannerIndex;

    private Control BuildHeroBanner()
        => Banner(_hero, "EXILIUM", "GIRLS’ FRONTLINE 2");

    private Control BuildEventCarousel()
    {
        SetupBannerArrow(_previousEvent, "ModPreviousEventBanner", "M11 1L3 9L11 17L13 15L7 9L13 3Z",
            HorizontalAlignment.Left, -1);
        SetupBannerArrow(_nextEvent, "ModNextEventBanner", "M3 1L11 9L3 17L1 15L7 9L1 3Z",
            HorizontalAlignment.Right, 1);
        _eventPosition.Background = Brush.Parse("#D0121E23");
        _eventPosition.CornerRadius = new CornerRadius(9);
        _eventPosition.Padding = new Thickness(8, 2);
        _eventPosition.Margin = new Thickness(0, 0, 0, 6);
        _eventPosition.HorizontalAlignment = HorizontalAlignment.Center;
        _eventPosition.VerticalAlignment = VerticalAlignment.Bottom;
        _eventPosition.IsHitTestVisible = false;
        _eventPosition.Child = _eventPositionText;

        return Banner(SetupBannerLink(_eventLink, _event), "GF2 / RU", AppText.Get("ModLauncherKicker"),
            _previousEvent, _nextEvent, _eventPosition);
    }

    private void SetupBannerArrow(Button button, string label, string icon, HorizontalAlignment side, int direction)
    {
        StyleActionButton(button, primary: false);
        button.Background = Brush.Parse("#D0121E23");
        button.CornerRadius = new CornerRadius(4);
        button.Content = ButtonIcon(icon, 16);
        button.Width = 32;
        button.Height = 42;
        button.Margin = new Thickness(6, 0);
        button.HorizontalAlignment = side;
        button.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(button, AppText.Get(label));
        ToolTip.SetTip(button, AppText.Get(label));
        button.Click += (_, _) =>
        {
            if (_eventBanners.Count > 1)
            {
                _eventBannerIndex = (_eventBannerIndex + direction + _eventBanners.Count) % _eventBanners.Count;
                ShowEventBanner();
            }
        };
    }

    private Button SetupBannerLink(Button button, Image image)
    {
        button.IsVisible = false;
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        button.Padding = new Thickness(0);
        button.CornerRadius = new CornerRadius(0);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.VerticalAlignment = VerticalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.VerticalContentAlignment = VerticalAlignment.Stretch;
        button.Cursor = new Cursor(StandardCursorType.Hand);
        button.Content = image;
        button.Resources["ButtonBackgroundPointerOver"] = Brushes.Transparent;
        button.Resources["ButtonBackgroundPressed"] = Brushes.Transparent;
        AutomationProperties.SetName(button, AppText.Get("ModOpenBannerLink"));
        ToolTip.SetTip(button, AppText.Get("ModOpenBannerLink"));
        button.Click += async (_, _) =>
        {
            if (button.CommandParameter is not Uri link)
            {
                return;
            }

            try
            {
                if (!await Launcher.LaunchUriAsync(link))
                {
                    ShowBannerLinkError();
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                AppLog.Warn($"Could not open official banner link: {exception.Message}");
                ShowBannerLinkError();
            }
        };
        return button;
    }

    private void ShowBannerLinkError()
        => SetStatus(AppText.Get("ModBannerLinkError"), isError: true);

    private Task RefreshBannerArtworkAsync()
    {
        var cancellationToken = _bannerLifetime.Token;
        var gameDirectory = Path.GetDirectoryName(_config.GameExecutablePath);
        var directories = ArtworkDirectories(gameDirectory).ToArray();
        return Task.WhenAll(RefreshHeroAsync(directories, cancellationToken),
            RefreshEventBannersAsync(directories, cancellationToken));
    }

    private async Task RefreshHeroAsync(IReadOnlyList<string> directories, CancellationToken cancellationToken)
    {
        var heroUrl = await OfficialBannerCatalog.GetHeroImageAsync(cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var hero = heroUrl is null ? null : await LoadBannerImageAsync(heroUrl, directories, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            hero?.Dispose();
            return;
        }

        hero ??= LoadCachedHero(directories);
        if (hero is not null)
        {
            var previous = _hero.Source as Bitmap;
            _hero.Source = hero;
            previous?.Dispose();
        }
    }

    private async Task RefreshEventBannersAsync(IReadOnlyList<string> directories, CancellationToken cancellationToken)
    {
        var events = await OfficialBannerCatalog.GetEventsAsync(cancellationToken);
        if (events is null || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var images = await Task.WhenAll(events.Select(banner => LoadBannerAsync(banner, directories, cancellationToken)));
        var linkedEvents = images.OfType<BannerArtwork>().ToArray();
        if (cancellationToken.IsCancellationRequested)
        {
            foreach (var banner in linkedEvents)
            {
                banner.Image.Dispose();
            }
            return;
        }

        // A temporary CDN failure should not erase cards that are already visible.
        if (events.Count > 0 && linkedEvents.Length == 0)
        {
            return;
        }

        var previous = _eventBanners;
        var selected = previous.Count == 0 ? null : previous[_eventBannerIndex].Link;
        _eventBanners = linkedEvents;
        _eventBannerIndex = Math.Max(0, Array.FindIndex(linkedEvents, banner => banner.Link == selected));
        ShowEventBanner();
        foreach (var banner in previous)
        {
            banner.Image.Dispose();
        }
    }

    private void DisposeBannerArtwork()
    {
        _bannerLifetime.Cancel();
        _bannerLifetime.Dispose();
        var hero = _hero.Source as Bitmap;
        _hero.Source = _event.Source = null;
        hero?.Dispose();
        foreach (var banner in _eventBanners)
        {
            banner.Image.Dispose();
        }
    }

    private void RefreshGameVersion()
    {
        var version = HasGame() ? GameInstallPaths.ReadInstalledVersion(_config.GameExecutablePath) : null;
        _version.Text = version is { } installed
            ? AppText.Format(installed.FromCache ? "ModCachedGameVersion" : "ModGameVersion", installed.Number)
            : AppText.Get("ModGameVersionUnknown");
    }

    private static IEnumerable<string> ArtworkDirectories(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            yield break;
        }

        yield return Path.Combine(gameDirectory, "bg");
        if (Path.GetDirectoryName(gameDirectory) is { } parent)
        {
            yield return Path.Combine(parent, "bg");
        }
    }

    private void ShowEventBanner()
    {
        var banner = _eventBanners.Count > 0 ? _eventBanners[_eventBannerIndex] : null;
        SetBannerArtwork(_eventLink, _event, banner);
        var canBrowse = _eventBanners.Count > 1;
        _previousEvent.IsVisible = _nextEvent.IsVisible = _eventPosition.IsVisible = canBrowse;
        _eventPositionText.Text = canBrowse ? $"{_eventBannerIndex + 1} / {_eventBanners.Count}" : "";
    }

    private static void SetBannerArtwork(Button button, Image image, BannerArtwork? banner)
    {
        image.Source = banner?.Image;
        button.CommandParameter = banner?.Link;
        button.IsVisible = banner is not null;
    }

    private static async Task<BannerArtwork?> LoadBannerAsync(
        OfficialBanner banner, IReadOnlyList<string> directories, CancellationToken cancellationToken)
    {
        var image = await LoadBannerImageAsync(banner.Image, directories, cancellationToken);
        return image is null ? null : new BannerArtwork(image, banner.Link);
    }

    private static async Task<Bitmap?> LoadBannerImageAsync(
        Uri image, IReadOnlyList<string> directories, CancellationToken cancellationToken)
    {
        var filename = Path.GetFileName(image.AbsolutePath);
        foreach (var directory in directories)
        {
            var path = Path.Combine(directory, filename);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                return new Bitmap(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                AppLog.Warn($"Could not load cached banner {path}: {exception.Message}");
            }
        }

        try
        {
            var bytes = await OfficialBannerCatalog.GetImageAsync(image, cancellationToken);
            using var stream = new MemoryStream(bytes, writable: false);
            return new Bitmap(stream);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
                                          or IOException or ArgumentException)
        {
            AppLog.Warn($"Could not load official banner {filename}: {exception.Message}");
            return null;
        }
    }

    private static Bitmap? LoadCachedHero(IEnumerable<string> directories)
    {
        var candidates = new List<FileInfo>();
        foreach (var directory in directories.Where(Directory.Exists))
        {
            try
            {
                candidates.AddRange(new DirectoryInfo(directory).EnumerateFiles("*.png"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Could not list cached banners in {directory}: {exception.Message}");
            }
        }

        Span<byte> header = stackalloc byte[24];
        foreach (var candidate in candidates.OrderByDescending(file => file.LastWriteTimeUtc)
                     .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var stream = candidate.OpenRead();
                if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length
                    || !header[12..16].SequenceEqual("IHDR"u8))
                {
                    continue;
                }

                var width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
                var height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
                // The cache also contains smaller event cards and square media icons.
                if (width >= 600 && height > 0 && Math.Abs((double)width / height - 1143.0 / 447) < 0.04)
                {
                    stream.Position = 0;
                    return new Bitmap(stream);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                AppLog.Warn($"Could not load cached hero {candidate.FullName}: {exception.Message}");
            }
        }

        return null;
    }

    private static Control Banner(Control artwork, string title, string subtitle, params Control[] overlays)
    {
        var layers = new Grid();
        layers.Children.Add(new StackPanel
        {
            Margin = new Thickness(21, 12),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { Label(title, 30, TextMain, FontWeight.Bold), Label(subtitle, 12, Accent) }
        });
        layers.Children.Add(artwork);
        layers.Children.AddRange(overlays);

        return new Border
        {
            Background = Brush.Parse("#20323A"),
            BorderBrush = Divider,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = layers
        };
    }
}
