using System.Windows;
using Forms = System.Windows.Forms;

namespace AutoWallpaper;

public partial class App : System.Windows.Application
{
    private WallpaperService? _service;
    private Forms.NotifyIcon? _tray;
    private Forms.ContextMenuStrip? _trayMenu;
    private MainWindow? _window;
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

        _tray = new Forms.NotifyIcon
        {
            Icon = trayIcon,
            Text = "Unsplash 壁纸自动更换",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => PostToWpf(ShowSettings);
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
        _trayMenu?.Dispose();
        _trayMenu = null;
        _service?.Dispose();
        _service = null;
        base.OnExit(e);
    }
}
