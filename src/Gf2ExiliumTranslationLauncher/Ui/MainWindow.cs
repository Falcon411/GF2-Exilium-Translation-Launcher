using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Gf2ExiliumTranslationLauncher.Configuration;
using Gf2ExiliumTranslationLauncher.Diagnostics;
using Gf2ExiliumTranslationLauncher.Game;
using Gf2ExiliumTranslationLauncher.Localization;
using Gf2ExiliumTranslationLauncher.Updates;

namespace Gf2ExiliumTranslationLauncher.Ui;

internal sealed partial class MainWindow : Window
{
    private readonly ConfigService _configService;
    private readonly ModLaunchService _modLaunchService;
    private readonly LauncherConfig _config;
    private readonly TextBox _gamePath = new() { IsReadOnly = true };
    private readonly Button _browse = new();
    private readonly Button _resetDiscovery = new();
    private readonly TextBlock _discoveryStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _play = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _restore = new();
    private readonly TextBlock _coverage = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { IsVisible = false, Height = 5 };
    private bool _busy;

    public MainWindow(ConfigService configService, ModLaunchService modLaunchService)
    {
        _configService = configService;
        _modLaunchService = modLaunchService;
        _config = configService.Load();
        AppText.Initialize(_config.UiLanguage);

        Title = AppText.Get("AppTitle");
        Width = 1240;
        Height = 650;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _gamePath.Text = _config.GameExecutablePath;
        _gamePath.PlaceholderText = GameInstallPaths.ExpectedExecutableName;
        _browse.Content = AppText.Get("ButtonBrowse");
        _resetDiscovery.Content = AppText.Get("ModResetDiscovery");
        _discoveryStatus.Foreground = TextMuted;
        _restore.Content = AppText.Get("ModRestoreAll");
        _browse.Click += async (_, _) => await PickGameAsync();
        _resetDiscovery.Click += async (_, _) => await RunAsync(ResetDiscoveryAsync);
        _play.Click += async (_, _) => await (HasGame() ? RunAsync(PrepareAndLaunchAsync) : PickGameAsync());
        _restore.Click += async (_, _) => await RunAsync(RestoreAllAsync);
        Opened += async (_, _) => await RunAsync(InitializeGameAsync);
        Closed += (_, _) => DisposeBannerArtwork();
        Activated += (_, _) => RefreshGameVersion();
        Closing += (_, args) => args.Cancel = _busy;

        Content = BuildLayout();
        RefreshGameVersion();

        ShowStatus(HasGame() ? "ModCheckingInstallation" : "ModFindingGame");
        SetCoverage(null);
        RefreshButtons();
    }

    private async Task InitializeGameAsync()
    {
        if (!HasGame())
        {
            ShowStatus("ModFindingGame");
            _discoveryStatus.Text = AppText.Get("ModFindingGame");
            string? found;
            try
            {
                found = await Task.Run(GameInstallationFinder.Find);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or System.Security.SecurityException)
            {
                AppLog.Warn($"Game discovery failed: {exception.Message}");
                found = null;
            }

            if (found is null)
            {
                ShowStatus("ModChooseGame");
                _discoveryStatus.Text = AppText.Get("ModDiscoveryNoMatch");
                SetSettingsVisible(true);
                await SelectGameAsync();
                return;
            }

            await SetGamePathAsync(found);
            _discoveryStatus.Text = AppText.Get("ModDiscoveryFound");
        }
        else if (string.IsNullOrWhiteSpace(_config.LocalFilePath))
        {
            await SetGamePathAsync(_config.GameExecutablePath);
        }

        await CheckConfiguredGameAsync();
    }

    private async Task ResetDiscoveryAsync()
    {
        if (!string.IsNullOrWhiteSpace(_config.GameExecutablePath))
        {
            _config.GameExecutablePathBeforeReset = _config.GameExecutablePath;
        }

        _config.GameExecutablePath = "";
        _gamePath.Text = "";
        SetCoverage(null);
        RefreshGameVersion();
        ShowStatus("ModFindingGame");
        _discoveryStatus.Text = AppText.Get("ModFindingGame");
        await _configService.SaveAsync(_config);
        await InitializeGameAsync();
    }

    private Task PickGameAsync() => RunAsync(SelectGameAsync);

    private async Task SelectGameAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = AppText.Format("BrowseGameExecutableTitle", GameInstallPaths.ExpectedExecutableName),
            FileTypeFilter = [new FilePickerFileType("GF2") { Patterns = [GameInstallPaths.ExpectedExecutableName] }]
        });
        var selected = files.FirstOrDefault()?.TryGetLocalPath();
        if (selected is null)
        {
            return;
        }

        var path = Path.GetFullPath(selected);
        if (!GameInstallPaths.IsInstalled(path))
        {
            throw new LauncherException(AppText.Format("DetailWrongExecutableSelected", GameInstallPaths.ExpectedExecutableName));
        }

        await SetGamePathAsync(path);
        _discoveryStatus.Text = AppText.Get("ModDiscoverySelected");
        await CheckConfiguredGameAsync();
    }

    private async Task SetGamePathAsync(string path)
    {
        var previousPath = string.IsNullOrWhiteSpace(_config.GameExecutablePath)
            ? _config.GameExecutablePathBeforeReset
            : _config.GameExecutablePath;
        _config.GameExecutablePath = path;
        _config.GameExecutablePathBeforeReset = "";
        if (!string.Equals(path, previousPath, StringComparison.OrdinalIgnoreCase))
        {
            _config.LocalFilePath = GameInstallPaths.GetTranslationFilePath(path);
            _config.LastContentSha256 = "";
            _config.LastCommitId = "";
            _config.LastGeneratedFileSha256 = "";
            _config.LastUpdatedAt = null;
            _config.LastInstalledReadyTable = false;
            _config.LastTranslationExportSha256 = "";
            _config.LastComparison = null;
        }
        else if (string.IsNullOrWhiteSpace(_config.LocalFilePath))
        {
            _config.LocalFilePath = GameInstallPaths.GetTranslationFilePath(path);
        }

        _gamePath.Text = path;
        SetCoverage(null);
        await _configService.SaveAsync(_config);
        RefreshGameVersion();
    }

    private async Task CheckConfiguredGameAsync()
    {
        ShowStatus("ModCheckingInstallation");
        await Task.WhenAll(CheckTranslationAsync(), RefreshBannerArtworkAsync());
    }

    private async Task CheckTranslationAsync()
    {
        await RefreshCoverageAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var result = await _modLaunchService.CheckInstallationAsync(_config, timeout.Token);
            SetStatus(result.Message);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            ShowStatus("ModCheckTimedOut");
        }
    }

    private async Task RefreshCoverageAsync()
    {
        try
        {
            var coverage = await Task.Run(() => _modLaunchService.GetInstalledCoverageAsync(_config, CancellationToken.None));
            SetCoverage(coverage);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                          or InvalidDataException or ProtoBuf.ProtoException)
        {
            AppLog.Warn($"Could not calculate installed translation coverage: {exception.Message}");
            SetCoverage(null);
        }
    }

    private void SetCoverage(double? coverage)
        => _coverage.Text = coverage is { } ratio
            ? AppText.Format("ModCoveragePercent", ratio)
            : AppText.Get("ModCoverageUnavailable");

    private async Task PrepareAndLaunchAsync()
    {
        if (string.IsNullOrWhiteSpace(_config.LocalFilePath))
        {
            _config.LocalFilePath = GameInstallPaths.GetTranslationFilePath(_config.GameExecutablePath);
        }

        await _configService.SaveAsync(_config);
        var progress = new Progress<double>(value =>
        {
            if (_progress.IsVisible)
            {
                _progress.Value = value * 100;
            }
        });
        var warning = await _modLaunchService.PrepareAndLaunchAsync(_config, phase =>
        {
            _progress.IsVisible = phase == "BusyDownloadingTranslation";
            if (_progress.IsVisible)
            {
                _progress.Value = 0;
            }
            ShowStatus(phase);
        }, progress, CancellationToken.None);
        var launched = AppText.Get("StatusGameLaunched");
        var comparison = _config.LastComparison is null ? null : UpdateService.GetComparisonStatus(_config);
        var detail = warning ?? comparison;
        SetStatus(detail is null ? launched : $"{launched} {detail}");
        await RefreshCoverageAsync();
    }

    private async Task RestoreAllAsync()
    {
        if (!await ConfirmRestoreAsync("ModConfirmRestoreAll"))
        {
            return;
        }

        ShowStatus("BusyRestoringOriginal");
        await _modLaunchService.RestoreAllAsync(_config, CancellationToken.None);
        SetCoverage(null);
        await _configService.SaveAsync(_config);
        ShowStatus("ModAllRestored");
    }

    private async Task<bool> ConfirmRestoreAsync(string key)
    {
        var prompt = new Window
        {
            Title = AppText.Get("ButtonRestoreOriginal"),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancel = new Button { Content = AppText.Get("ButtonCancel") };
        var confirm = new Button { Content = AppText.Get("ButtonRestoreOriginalConfirm") };
        cancel.Click += (_, _) => prompt.Close(false);
        confirm.Click += (_, _) => prompt.Close(true);
        prompt.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = AppText.Get(key), TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, confirm } }
            }
        };
        return await prompt.ShowDialog<bool>(this);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        RefreshButtons();
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            AppLog.Error("Mod launcher operation failed.", exception);
            SetStatus(AppText.Format("DetailErrorWithLog", exception.Message, AppLog.CurrentLogPath), isError: true);
        }
        finally
        {
            _busy = false;
            _progress.IsVisible = false;
            RefreshButtons();
        }
    }

    private void ShowStatus(string key) => SetStatus(AppText.Get(key));

    private void SetStatus(string message, bool isError = false)
    {
        _status.Text = message;
        _settingsStatus.Text = message;
        _status.Foreground = _settingsStatus.Foreground = isError ? Error : TextMain;
    }

    private bool HasGame()
        => GameInstallPaths.IsInstalled(_config.GameExecutablePath);

    private void RefreshButtons()
    {
        var hasGame = HasGame();
        var ready = !_busy && hasGame;
        _browse.IsEnabled = _resetDiscovery.IsEnabled = !_busy;
        _play.IsEnabled = !_busy;
        _play.Content = AppText.Get(_busy ? "ModPleaseWait" : hasGame ? "ModPlay" : "ModSelectGame");
        _settings.IsEnabled = _menu.IsEnabled = !_busy;
        _close.IsEnabled = !_busy;
        _restore.IsEnabled = ready &&
            ((!string.IsNullOrWhiteSpace(_config.LocalFilePath) && File.Exists(FileReplaceService.GetBackupPath(_config.LocalFilePath)))
             || File.Exists(StartupTranslationService.GetBackupPath(_config.GameExecutablePath)));
    }
}
