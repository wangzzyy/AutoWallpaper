using System.Windows;
using Border = System.Windows.Controls.Border;
using DockPanel = System.Windows.Controls.DockPanel;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using Forms = System.Windows.Forms;

namespace AutoWallpaper;

public partial class App : System.Windows.Application
{
    private WallpaperService? _service;
    private Forms.NotifyIcon? _tray;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.Timer? _trayClickTimer;
    private MainWindow? _window;
    private Window? _favoriteToast;
    private System.Windows.Threading.DispatcherTimer? _favoriteToastTimer;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            _service = new WallpaperService();
            _service.StatusChanged += OnStatusChanged;
            SetupTray();
            _service.Start();

            if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
                ShowSettings();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"应用启动失败：{ex.Message}", "Unsplash 壁纸自动更换", MessageBoxButton.OK, MessageBoxImage.Error);
            ExitApplication();
        }
    }

    private void SetupTray()
    {
        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("打开设置", null, (_, _) => PostToWpf(ShowSettings));
        _trayMenu.Items.Add("立即换图", null, async (_, _) =>
        {
            var service = _service;
            if (!_exiting && service is not null)
                await service.ChangeNowAsync();
        });
        _trayMenu.Items.Add("收藏当前壁纸", null, (_, _) => FavoriteCurrentWallpaper());
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("退出", null, (_, _) => PostToWpf(ExitApplication));

        System.Drawing.Icon trayIcon;
        var iconStream = typeof(App).Assembly.GetManifestResourceStream("AutoWallpaper.app.ico");
        if (iconStream is not null)
        {
            using (iconStream) trayIcon = new System.Drawing.Icon(iconStream);
        }
        else
        {
            trayIcon = (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        }

        _trayClickTimer = new Forms.Timer { Interval = Math.Max(Forms.SystemInformation.DoubleClickTime, 300) };
        _trayClickTimer.Tick += async (_, _) =>
        {
            _trayClickTimer.Stop();
            var service = _service;
            if (!_exiting && service is not null) await service.ChangeNowAsync();
        };

        _tray = new Forms.NotifyIcon
        {
            Icon = trayIcon,
            Text = "Unsplash 壁纸自动更换",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left || _exiting) return;
            // Delay a single-click action briefly so a second click can be recognized as a double-click.
            _trayClickTimer?.Stop();
            _trayClickTimer?.Start();
        };
        _tray.DoubleClick += (_, _) =>
        {
            _trayClickTimer?.Stop();
            FavoriteCurrentWallpaper();
        };
    }

    private void FavoriteCurrentWallpaper()
    {
        var service = _service;
        if (_exiting || service is null) return;

        var result = service.FavoriteCurrentWallpaper();
        var message = result switch
        {
            FavoriteResult.Success => "收藏成功",
            FavoriteResult.AlreadyExists => "这张图片已经收藏过了",
            _ => "当前壁纸不可用，暂时无法收藏"
        };
        OnStatusChanged(message);
        PostToWpf(() => ShowFavoriteToast(result, message));
    }

    
    private void ShowFavoriteToast(FavoriteResult result, string message)
    {
        _favoriteToastTimer?.Stop();
        _favoriteToast?.Close();

        var (accent, icon, subtitle) = result switch
        {
            FavoriteResult.Success => (
                System.Windows.Media.Color.FromRgb(22, 163, 74), "✓", "图片已保存到 Favorites 收藏夹"),
            FavoriteResult.AlreadyExists => (
                System.Windows.Media.Color.FromRgb(217, 119, 6), "i", "无需重复添加"),
            _ => (
                System.Windows.Media.Color.FromRgb(220, 38, 38), "!", "请先成功应用一张壁纸，再试一次")
        };

        var accentBrush = new System.Windows.Media.SolidColorBrush(accent);
        var iconBadge = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(19),
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(24, accent.R, accent.G, accent.B)),
            Child = new TextBlock
            {
                Text = icon,
                Foreground = accentBrush,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };

        var textStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(13, 0, 0, 0),
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(16, 24, 40))
                },
                new TextBlock
                {
                    Text = subtitle,
                    FontSize = 12,
                    Margin = new Thickness(0, 4, 0, 0),
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(102, 112, 133))
                }
            }
        };

        var content = new DockPanel
        {
            LastChildFill = true,
            Children = { iconBadge, textStack }
        };
        DockPanel.SetDock(iconBadge, Dock.Left);

        var toast = new Window
        {
            Title = "AutoWallpaper",
            Width = 350,
            Height = 88,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Content = new Border
            {
                Background = System.Windows.Media.Brushes.White,
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(234, 236, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(15),
                Padding = new Thickness(17, 14, 18, 14),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = System.Windows.Media.Colors.Black,
                    Opacity = 0.16,
                    BlurRadius = 22,
                    ShadowDepth = 4
                },
                Child = content
            }
        };

        var workArea = SystemParameters.WorkArea;
        toast.Left = workArea.Right - toast.Width - 22;
        toast.Top = workArea.Bottom - 110;
        _favoriteToast = toast;
        toast.Closed += (_, _) =>
        {
            if (ReferenceEquals(_favoriteToast, toast)) _favoriteToast = null;
        };
        toast.Show();

        _favoriteToastTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _favoriteToastTimer.Tick += (_, _) =>
        {
            _favoriteToastTimer?.Stop();
            if (ReferenceEquals(_favoriteToast, toast)) toast.Close();
        };
        _favoriteToastTimer.Start();
    }

    private void PostToWpf(Action action)
    {
        if (_exiting || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(action);
    }

    private void ShowSettings()
    {
        if (_exiting || _service is null) return;
        if (_window is null)
        {
            _window = new MainWindow(_service);
            _window.Closed += (_, _) => _window = null;
        }
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnStatusChanged(string message)
    {
        if (_exiting || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_exiting) return;
            _window?.SetStatus(message);
            if (_tray is not null)
            {
                var text = string.IsNullOrWhiteSpace(message) ? "Unsplash 壁纸自动更换" : message;
                _tray.Text = text.Length <= 63 ? text : text[..60] + "...";
            }
        }));
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;

        // Remove the tray icon first so it never remains visible after the app exits.
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip = null;
            _tray.Dispose();
            _tray = null;
        }
        _trayClickTimer?.Stop();
        _trayClickTimer?.Dispose();
        _trayClickTimer = null;
        _trayMenu?.Dispose();
        _trayMenu = null;

        if (_window is not null)
        {
            _window.PrepareForExit();
            _window.Close();
            _window = null;
        }

        if (_service is not null)
        {
            _service.StatusChanged -= OnStatusChanged;
            _service.Dispose(); // Cancels any in-flight download.
            _service = null;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Final safety net; do not call Shutdown() recursively from OnExit.
        _exiting = true;
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        _trayClickTimer?.Stop();
        _trayClickTimer?.Dispose();
        _trayClickTimer = null;
        _trayMenu?.Dispose();
        _trayMenu = null;
        _service?.Dispose();
        _service = null;
        base.OnExit(e);
    }
}
