using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Gf2ExiliumTranslationLauncher.Localization;
using ResizeHit = Avalonia.Controls.Win32Properties.Win32HitTestValue;

namespace Gf2ExiliumTranslationLauncher.Ui;

internal sealed partial class MainWindow
{
    private static readonly IBrush Shell = Brush.Parse("#121E23");
    private static readonly IBrush PanelFill = Brush.Parse("#243138");
    private static readonly IBrush Divider = Brush.Parse("#425157");
    private static readonly IBrush TextMain = Brush.Parse("#F4F5F2");
    private static readonly IBrush TextMuted = Brush.Parse("#B6C3C8");
    private static readonly IBrush Accent = Brush.Parse("#FFAB63");
    private static readonly IBrush Error = Brush.Parse("#FFAA9A");

    private readonly Button _settings = new();
    private readonly Button _menu = new();
    private readonly Button _minimize = new();
    private readonly Button _maximize = new();
    private readonly Button _close = new();
    private readonly Grid _settingsOverlay = new() { IsVisible = false };
    private readonly TextBlock _settingsStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _version = new() { Foreground = TextMuted, FontSize = 12 };

    private Control BuildLayout()
    {
        Background = Shell;
        Foreground = TextMain;
        CanResize = true;
        CanMaximize = true;
        if (OperatingSystem.IsWindows())
        {
            const uint resizableFrame = 0x00040000; // WS_THICKFRAME, for native resizing without drawn borders.
            Win32Properties.AddWindowStylesCallback(this, (style, extendedStyle) => (style | resizableFrame, extendedStyle));
            Win32Properties.AddWndProcHookCallback(this, HandleWindowFrame);
        }
        WindowDecorations = WindowDecorations.None;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            Opened += (_, _) =>
            {
                if (TryGetPlatformHandle() is { } platformHandle)
                {
                    const uint borderColorAttribute = 34;
                    const uint noBorderColor = 0xfffffffe;
                    DwmSetWindowAttribute(platformHandle.Handle, borderColorAttribute, noBorderColor, sizeof(uint));
                }
            };
        }
        MinWidth = 740;
        MinHeight = 420;
        UseLayoutRounding = true;
        if (Screens.Primary is { } screen)
        {
            var availableWidth = screen.WorkingArea.Width / screen.Scaling - 24;
            var availableHeight = screen.WorkingArea.Height / screen.Scaling - 28;
            MinWidth = Math.Min(MinWidth, availableWidth);
            MinHeight = Math.Min(MinHeight, availableHeight);
            Width = Math.Min(Width, availableWidth);
            Height = Math.Min(Height, availableHeight);
        }

        StyleActionButton(_play, primary: true);
        StyleActionButton(_menu, primary: true);
        _play.FontSize = 22;
        _play.FontWeight = FontWeight.Bold;
        _play.Height = 58;
        _play.CornerRadius = new CornerRadius(4, 0, 0, 4);
        _menu.Width = 51;
        _menu.Height = 58;
        _menu.CornerRadius = new CornerRadius(0, 4, 4, 0);
        _menu.Content = ButtonIcon("M0 0H20V3H0Z M0 7H20V10H0Z M0 14H20V17H0Z", 19);

        _settings.Click += (_, _) => SetSettingsVisible(!_settingsOverlay.IsVisible);
        _menu.Click += (_, _) => SetSettingsVisible(true);
        _minimize.Click += (_, _) => WindowState = WindowState.Minimized;
        _maximize.Click += (_, _) => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty)
            {
                UpdateMaximizeButton();
            }
        };
        _close.Click += (_, _) => Close();
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && _settingsOverlay.IsVisible)
            {
                SetSettingsVisible(false);
                args.Handled = true;
            }
        };

        var titleBar = BuildTitleBar();
        var topStrip = new Border
        {
            Background = PanelFill,
            Padding = new Thickness(11, 6),
            Child = Label(AppText.Get("ModLauncherKicker"), 11, TextMuted)
        };

        var side = BuildSidebar();
        var content = BuildDashboard();
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*"), ColumnSpacing = 14 };
        Grid.SetColumn(content, 1);
        body.Children.Add(side);
        body.Children.Add(content);

        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        var top = new Border { Margin = new Thickness(16, 0, 16, 0), Child = topStrip };
        var bodyFrame = new Border { Margin = new Thickness(16, 12, 16, 16), Child = body };
        Grid.SetRow(top, 1);
        Grid.SetRow(bodyFrame, 2);
        main.Children.Add(titleBar);
        main.Children.Add(top);
        main.Children.Add(bodyFrame);

        var bodyWithSettings = new Grid { Background = Shell };
        bodyWithSettings.Children.Add(main);
        BuildSettings();
        bodyWithSettings.Children.Add(_settingsOverlay);
        return bodyWithSettings;
    }

    private nint HandleWindowFrame(nint window, uint message, nint wParam, nint lParam, ref bool handled)
    {
        const uint calculateClientArea = 0x0083;
        const uint nonClientHitTest = 0x0084;
        const double grip = 5;
        if (message == calculateClientArea && wParam != 0 && IsZoomed(window))
        {
            FitMaximizedClientArea(lParam);
            handled = true;
            return 0;
        }
        if (message != nonClientHitTest || WindowState != WindowState.Normal)
        {
            return 0;
        }

        var coordinates = lParam.ToInt32();
        var point = this.PointToClient(new PixelPoint(
            unchecked((short)coordinates), unchecked((short)(coordinates >> 16))));
        var horizontal = point.X < grip ? -1 : point.X >= ClientSize.Width - grip ? 1 : 0;
        var vertical = point.Y < grip ? -1 : point.Y >= ClientSize.Height - grip ? 1 : 0;
        var hit = (horizontal, vertical) switch
        {
            (-1, -1) => ResizeHit.TopLeft,
            (0, -1) => ResizeHit.Top,
            (1, -1) => ResizeHit.TopRight,
            (-1, 0) => ResizeHit.Left,
            (1, 0) => ResizeHit.Right,
            (-1, 1) => ResizeHit.BottomLeft,
            (0, 1) => ResizeHit.Bottom,
            (1, 1) => ResizeHit.BottomRight,
            _ => ResizeHit.Client
        };
        if (hit == ResizeHit.Client)
        {
            return 0;
        }

        handled = true;
        return (nint)hit;
    }

    private void FitMaximizedClientArea(nint clientRect)
    {
        // The first RECT in NCCALCSIZE_PARAMS contains the proposed window bounds.
        var left = Marshal.ReadInt32(clientRect, 0);
        var top = Marshal.ReadInt32(clientRect, 4);
        var right = Marshal.ReadInt32(clientRect, 8);
        var bottom = Marshal.ReadInt32(clientRect, 12);
        var center = new PixelPoint(left + (right - left) / 2, top + (bottom - top) / 2);
        if (Screens.ScreenFromPoint(center) is { } screen)
        {
            var area = screen.WorkingArea;
            Marshal.WriteInt32(clientRect, 0, Math.Max(left, area.X));
            Marshal.WriteInt32(clientRect, 4, Math.Max(top, area.Y));
            Marshal.WriteInt32(clientRect, 8, Math.Min(right, area.Right));
            Marshal.WriteInt32(clientRect, 12, Math.Min(bottom, area.Bottom));
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(nint window);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, in uint value, uint size);

    private void UpdateMaximizeButton()
    {
        var maximized = WindowState == WindowState.Maximized;
        StyleTitleButton(_maximize, maximized ? "ModRestoreWindow" : "ModMaximize", ButtonIcon(
            maximized
                ? "F0 M5 0H16V11H13V9H14V2H7V5H5Z M0 5H11V16H0Z M2 7V14H9V7Z"
                : "F0 M0 0H16V16H0Z M2 2V14H14V2Z",
            15));
    }

    private Control BuildTitleBar()
    {
        StyleTitleButton(_settings, "ModSettings", ButtonIcon(
            "F0 M8.79 4.24 10.4 3.75 10.47 1.11 13.53 1.11 13.6 3.75"
            + "L15.21 4.24 16.7 5.04 18.62 3.22 20.78 5.38 18.96 7.3"
            + "L19.76 8.79 20.25 10.4 22.89 10.47 22.89 13.53 20.25 13.6"
            + "L19.76 15.21 18.96 16.7 20.78 18.62 18.62 20.78 16.7 18.96"
            + "L15.21 19.76 13.6 20.25 13.53 22.89 10.47 22.89 10.4 20.25"
            + "L8.79 19.76 7.3 18.96 5.38 20.78 3.22 18.62 5.04 16.7"
            + "L4.24 15.21 3.75 13.6 1.11 13.53 1.11 10.47 3.75 10.4"
            + "L4.24 8.79 5.04 7.3 3.22 5.38 5.38 3.22 7.3 5.04Z M12 8A4 4 0 1 0 12 16A4 4 0 1 0 12 8Z", 18));
        StyleTitleButton(_minimize, "ModMinimize",
            new Border { Width = 16, Height = 3, Background = TextMain });
        UpdateMaximizeButton();
        StyleTitleButton(_close, "ModClose", ButtonIcon(
            "M4 2L10 8L16 2L18 4L12 10L18 16L16 18L10 12L4 18L2 16L8 10L2 4Z", 17));
        AutomationProperties.SetName(_menu, AppText.Get("ModSettings"));
        ToolTip.SetTip(_menu, AppText.Get("ModSettings"));

        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Spacing = 9,
            Children =
            {
                CreateTitleIcon(),
                Label("GIRLS’ FRONTLINE 2: EXILIUM", 13, TextMain, FontWeight.Bold),
                Label("/ RU", 13, TextMuted, FontWeight.Bold),
                Label($"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}", 13, TextMuted)
            }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { _settings, _minimize, _maximize, _close } };
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 38, Background = Shell };
        Grid.SetColumn(buttons, 1);
        title.Children.Add(brand);
        title.Children.Add(buttons);
        title.PointerPressed += (_, args) =>
        {
            if (args.GetPosition(title).X < title.Bounds.Width - buttons.Bounds.Width
                && args.GetCurrentPoint(title).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(args);
            }
        };
        return title;
    }

    private Image CreateTitleIcon()
    {
        using var ico = typeof(MainWindow).Assembly.GetManifestResourceStream("Gf2ExiliumTranslationLauncher.Launcher.ico")
            ?? throw new InvalidOperationException("Launcher icon resource is missing.");
        using var reader = new BinaryReader(ico);
        ico.Position = 4;
        var frameCount = reader.ReadUInt16();
        // The last frame in Launcher.ico is the largest PNG.
        ico.Position = 6 + (frameCount - 1) * 16 + 8;
        var length = reader.ReadInt32();
        ico.Position = reader.ReadInt32();
        using var png = new MemoryStream(reader.ReadBytes(length));
        var bitmap = new Bitmap(png);
        Icon = new WindowIcon(bitmap);
        return new Image { Source = bitmap, Width = 19, Height = 19, VerticalAlignment = VerticalAlignment.Center };
    }

    private Control BuildSidebar()
    {
        var background = new LinearGradientBrush
        {
            StartPoint = RelativePoint.TopLeft,
            EndPoint = RelativePoint.BottomRight,
            GradientStops =
            {
                new GradientStop(Color.Parse("#21343B"), 0),
                new GradientStop(Color.Parse("#17272F"), 0.57),
                new GradientStop(Color.Parse("#111A20"), 1)
            }
        };
        var gameName = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(17, 0),
            Spacing = 4,
            Children =
            {
                new Border { Background = Accent, Height = 2, Width = 38, HorizontalAlignment = HorizontalAlignment.Left },
                Label("GIRLS’ FRONTLINE 2", 13, TextMuted, FontWeight.SemiBold),
                Label("EXILIUM", 39, TextMain, FontWeight.Bold),
                Label(AppText.Get("ModLauncherKicker"), 11, Accent, FontWeight.SemiBold)
            }
        };

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_menu, 1);
        actions.Children.Add(_play);
        actions.Children.Add(_menu);
        var separator = new Border
        {
            Width = 1,
            Background = Brush.Parse("#FFC190"),
            Margin = new Thickness(0, 12),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false
        };
        Grid.SetColumn(separator, 1);
        actions.Children.Add(separator);

        var startArea = new StackPanel
        {
            Margin = new Thickness(12, 0, 12, 16),
            Spacing = 9,
            Children = { actions, _version }
        };
        var side = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(startArea, 1);
        side.Children.Add(gameName);
        side.Children.Add(startArea);
        return new Border
        {
            Background = background,
            BorderBrush = Divider,
            BorderThickness = new Thickness(1),
            Child = side
        };
    }

    private Control BuildDashboard()
    {
        var hero = BuildHeroBanner();
        var promo = BuildEventCarousel();
        var info = new Border
        {
            Background = PanelFill,
            BorderBrush = Divider,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        Label(AppText.Get("ModTranslationStatus"), 15, Accent, FontWeight.SemiBold),
                        new Border { Height = 1, Background = Divider },
                        _status,
                        _progress,
                        _coverage,
                        new TextBlock { Text = SourceLabel(), Foreground = TextMuted, FontSize = 12, TextWrapping = TextWrapping.Wrap }
                    }
                }
            }
        };
        _status.Foreground = TextMain;
        _status.FontSize = 14;
        _coverage.Foreground = Accent;
        _coverage.FontSize = 13;
        _coverage.FontWeight = FontWeight.SemiBold;
        ToolTip.SetTip(_coverage, AppText.Get("ModCoverageHelp"));

        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        Grid.SetColumn(info, 1);
        bottom.Children.Add(promo);
        bottom.Children.Add(info);

        var dashboard = new Grid { RowDefinitions = new RowDefinitions("*,150"), RowSpacing = 12 };
        Grid.SetRow(bottom, 1);
        dashboard.Children.Add(hero);
        dashboard.Children.Add(bottom);
        return dashboard;
    }

    private void BuildSettings()
    {
        var done = new Button { Content = AppText.Get("ModDone"), HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in new[] { _browse, _resetDiscovery, _restore, done })
        {
            button.FontWeight = FontWeight.Bold;
            button.MinHeight = 36;
        }
        done.Click += (_, _) => SetSettingsVisible(false);
        var gameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(_browse, 1);
        gameRow.Children.Add(_gamePath);
        gameRow.Children.Add(_browse);

        var options = new StackPanel
        {
            Spacing = 13,
            Children =
            {
                Label(AppText.Get("ModSettings"), 24, TextMain, FontWeight.SemiBold),
                Label(AppText.Get("ModGameExecutableLabel"), 13, TextMuted),
                gameRow,
                _resetDiscovery,
                _discoveryStatus,
                new TextBlock { Text = SourceLabel(), Foreground = TextMuted, TextWrapping = TextWrapping.Wrap },
                new Border { Height = 1, Background = Divider },
                Label(AppText.Get("ButtonRestoreOriginal"), 16, TextMain, FontWeight.SemiBold),
                _restore,
                _settingsStatus,
                done
            }
        };
        _settingsOverlay.Margin = new Thickness(0, 38, 0, 0);
        _settingsOverlay.Background = Brush.Parse("#B010171A");
        _settingsOverlay.Children.Add(new Border
        {
            Width = 560,
            MaxHeight = 500,
            Padding = new Thickness(22),
            Margin = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = Divider,
            BorderThickness = new Thickness(1),
            Background = PanelFill,
            Child = new ScrollViewer { Content = options }
        });
    }

    private void SetSettingsVisible(bool visible)
    {
        _settingsOverlay.IsVisible = visible;
        if (visible)
        {
            _browse.Focus();
        }
        else
        {
            _settings.Focus();
        }
    }

    private string SourceLabel()
        => _config.UsesPublishedTranslationPackage
            ? AppText.Get("ModPublishedSource")
            : AppText.Format("ModCustomSource", _config.ProjectIdOrPath, _config.Ref);

    private static TextBlock Label(string text, double size, IBrush color, FontWeight? weight = null)
        => new()
        {
            Text = text,
            FontSize = size,
            Foreground = color,
            FontWeight = weight ?? FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center
        };

    private static Avalonia.Controls.Shapes.Path ButtonIcon(string path, double size)
        => new()
        {
            Data = Geometry.Parse(path),
            Fill = TextMain,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

    private static void StyleTitleButton(Button button, string key, Control icon)
    {
        StyleActionButton(button, primary: false);
        button.Width = 42;
        button.Height = 38;
        button.Content = icon;
        AutomationProperties.SetName(button, AppText.Get(key));
        ToolTip.SetTip(button, AppText.Get(key));
    }

    private static void StyleActionButton(Button button, bool primary)
    {
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        button.Padding = new Thickness(3, 0);
        button.BorderThickness = new Thickness(0);
        button.CornerRadius = new CornerRadius(primary ? 3 : 0);
        button.Foreground = TextMain;
        button.Background = primary ? Brush.Parse("#D86112") : Shell;
        button.Resources["ButtonBackgroundPointerOver"] = Brush.Parse(primary ? "#F07925" : "#3A4B52");
        button.Resources["ButtonBackgroundPressed"] = Brush.Parse(primary ? "#B54B0B" : "#283840");
        button.Resources["ButtonForegroundPointerOver"] = TextMain;
        button.Resources["ButtonForegroundPressed"] = TextMain;
    }
}
