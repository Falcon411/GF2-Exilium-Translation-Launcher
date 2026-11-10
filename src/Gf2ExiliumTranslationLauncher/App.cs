using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Gf2ExiliumTranslationLauncher.Configuration;
using Gf2ExiliumTranslationLauncher.Diagnostics;
using Gf2ExiliumTranslationLauncher.Game;
using Gf2ExiliumTranslationLauncher.Translations;
using Gf2ExiliumTranslationLauncher.Translations.Protobuf;
using Gf2ExiliumTranslationLauncher.Updates;
using Gf2ExiliumTranslationLauncher.Ui;

namespace Gf2ExiliumTranslationLauncher;

public sealed class App : Application
{
    public override void Initialize()
    {
        AppLog.Info("Avalonia application initializing.");
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        AppLog.Info("Avalonia Fluent theme configured.");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppLog.Info("Avalonia framework initialization completed.");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppLog.Info("Creating application services.");
            var configService = new ConfigService();
            var gitLabClient = new GitLabFileClient(new HttpClient());
            var translationJsonReader = new TranslationJsonReader();
            var translationProtobufWriter = new TranslationProtobufWriter();
            var updateService = new UpdateService(
                gitLabClient,
                configService,
                translationJsonReader,
                translationProtobufWriter);
            var modLaunchService = new ModLaunchService(updateService, new GameLauncher());
            desktop.MainWindow = new MainWindow(configService, modLaunchService);
            AppLog.Info("Main window created.");
        }
        else
        {
            AppLog.Warn($"Unsupported application lifetime: {ApplicationLifetime?.GetType().FullName ?? "<null>"}");
        }

        base.OnFrameworkInitializationCompleted();
    }
}
