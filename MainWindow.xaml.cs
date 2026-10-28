using System.Windows;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace AutoWallpaper;

public partial class MainWindow : Window
{
    private readonly WallpaperService _service;
    private bool _allowClose;
    private bool _isSaving;
    private static readonly HttpClient DnsHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    public MainWindow(WallpaperService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        InitializeComponent();
        LoadSettings();
    }

    public void SetStatus(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => SetStatus(message)));
            return;
        }
        StatusText.Text = message;
        var busy = message.StartsWith("正在", StringComparison.Ordinal)
            || message.StartsWith("预取中", StringComparison.Ordinal)
            || message.StartsWith("图片下载完成", StringComparison.Ordinal)
            || message.Contains("正在进行", StringComparison.Ordinal);
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Foreground = busy ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 78, 216))
                                     : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 84, 103));
    }

    public void PrepareForExit() => _allowClose = true;

    private void LoadSettings()
    {
        var s = SettingsStore.Load();
        ApiKeyBox.Password = s.AccessKey;
        ApiIpBox.Text = s.ApiIpOverride;
        ImageIpBox.Text = s.ImageIpOverride;
        QueryBox.Text = s.Query;
        var orientation = string.IsNullOrWhiteSpace(s.Orientation) ? "landscape" : s.Orientation;
        OrientationBox.SelectedValue = orientation;
        if (OrientationBox.SelectedItem == null) OrientationBox.SelectedValue = "landscape";
        IntervalBox.Text = s.IntervalMinutes.ToString();
        FitBox.SelectedIndex = Math.Clamp(s.FitMode, 0, 4);
        CacheBox.Text = s.CacheDirectory;
        MaxCountBox.Text = s.MaxCacheCount.ToString();
        MaxDaysBox.Text = s.MaxAgeDays.ToString();
        StartupBox.IsChecked = s.StartWithWindows;
        AccentBox.IsChecked = s.AutoAccent;
        MinimizeBox.IsChecked = s.MinimizeToTray;
        UpdateBeautifyHint();
    }


    private void WallpaperOptions_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateBeautifyHint();
    }

    private void UpdateBeautifyHint()
    {
        if (BeautifyHint == null || FitBox == null || OrientationBox == null) return;
        var orientation = OrientationBox.SelectedValue as string;
        var shouldShow = FitBox.SelectedIndex == 0
            && (string.Equals(orientation, "portrait", StringComparison.OrdinalIgnoreCase)
                || string.Equals(orientation, "any", StringComparison.OrdinalIgnoreCase));
        BeautifyHint.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
    }

    private AppSettings ReadSettings() => new()
    {
        AccessKey = ApiKeyBox.Password.Trim(),
        ApiIpOverride = NormalizeIp(ApiIpBox.Text),
        ImageIpOverride = NormalizeIp(ImageIpBox.Text),
        Query = QueryBox.Text.Trim(),
        Orientation = OrientationBox.SelectedValue as string ?? "landscape",
        IntervalMinutes = Parse(IntervalBox.Text, 60, 1, 10080),
        FitMode = Math.Clamp(FitBox.SelectedIndex, 0, 4),
        CacheDirectory = string.IsNullOrWhiteSpace(CacheBox.Text) ? SettingsStore.DefaultCache : CacheBox.Text.Trim(),
        MaxCacheCount = Parse(MaxCountBox.Text, 30, 1, 500),
        MaxAgeDays = Parse(MaxDaysBox.Text, 30, 1, 3650),
        StartWithWindows = StartupBox.IsChecked == true,
        AutoAccent = AccentBox.IsChecked == true,
        MinimizeToTray = MinimizeBox.IsChecked == true
    };

    private static int Parse(string value, int fallback, int min, int max) =>
        int.TryParse(value, out var number) ? Math.Clamp(number, min, max) : fallback;

    private bool SaveSettings()
    {
        if (_isSaving) return false;
        _isSaving = true;
        try
        {
            var settings = ReadSettings();
            SettingsStore.Save(settings);
            StartupManager.SetEnabled(settings.StartWithWindows);
            _service.ApplySettings(settings);
            SetStatus("设置已保存。");
            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            _isSaving = false;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveSettings();

    private void SaveAndHide_Click(object sender, RoutedEventArgs e)
    {
        if (SaveSettings()) Hide();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveSettings()) return;
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            SetStatus("请先填写 Unsplash Access Key。");
            return;
        }

        NextButton.IsEnabled = false;
        NextButton.Content = "正在换图…";
        SetStatus("正在准备壁纸，检查预取队列和本地图库…");
        try
        {
            // Network and image processing run asynchronously; the window remains responsive.
            await _service.ChangeNowAsync();
        }
        finally
        {
            NextButton.IsEnabled = true;
            NextButton.Content = "立即换一张";
        }
    }

    private static string NormalizeIp(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (!IPAddress.TryParse(value.Trim(), out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidOperationException("API IP 必须是有效的 IPv4 地址；如需恢复系统 DNS，请清空此输入框。");
        return address.ToString();
    }

    private async void RefreshIp_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as System.Windows.Controls.Button;
        if (button is not null) button.IsEnabled = false;
        try
        {
            SetStatus("正在通过公共 DNS 查询 api.unsplash.com 的 A 记录…");
            var addresses = await ResolveUnsplashIpsAsync("api.unsplash.com");
            if (addresses.Count == 0)
            {
                SetStatus("没有找到可通过 HTTPS/TLS 检查的 IPv4 地址。可点击 DNSChecker 核对，或清空 IP 输入框恢复系统 DNS。");
                return;
            }

            // Prefer a different address from the currently selected one when possible.
            var current = ApiIpBox.Text.Trim();
            var selected = addresses.FirstOrDefault(ip => !string.Equals(ip, current, StringComparison.OrdinalIgnoreCase)) ?? addresses[0];
            ApiIpBox.Text = selected;
            SetStatus($"已刷新 {addresses.Count} 个候选 IP，当前填入：{selected}。点击“保存设置”后生效。DNS 返回的 IP 不保证从当前网络一定可达。");
        }
        catch (Exception ex)
        {
            SetStatus("刷新 IP 失败：" + ex.Message + "。可打开 DNSChecker 手动核对。");
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private async void RefreshImageIp_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as System.Windows.Controls.Button;
        if (button is not null) button.IsEnabled = false;
        try
        {
            SetStatus("正在通过公共 DNS 查询 images.unsplash.com 的 A 记录…");
            var addresses = await ResolveUnsplashIpsAsync("images.unsplash.com");
            if (addresses.Count == 0)
            {
                SetStatus("没有找到可通过 HTTPS/TLS 检查的图片 CDN IPv4 地址。可点击 DNSChecker 核对，或清空图片 IP 输入框恢复系统 DNS。");
                return;
            }
            var current = ImageIpBox.Text.Trim();
            var selected = addresses.FirstOrDefault(ip => !string.Equals(ip, current, StringComparison.OrdinalIgnoreCase)) ?? addresses[0];
            ImageIpBox.Text = selected;
            SetStatus($"已刷新 {addresses.Count} 个图片 CDN 候选 IP，当前填入：{selected}。点击“保存设置”后生效。");
        }
        catch (Exception ex)
        {
            SetStatus("刷新图片 CDN IP 失败：" + ex.Message + "。可打开 DNSChecker 手动核对。");
        }
        finally { if (button is not null) button.IsEnabled = true; }
    }

    private static async Task<List<string>> ResolveUnsplashIpsAsync(string hostname)
    {
        var encodedHost = Uri.EscapeDataString(hostname);
        var endpoints = new[]
        {
            $"https://dns.google/resolve?name={encodedHost}&type=A",
            $"https://cloudflare-dns.com/dns-query?name={encodedHost}&type=A"
        };
        var results = await Task.WhenAll(endpoints.Select(async endpoint =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.Accept.ParseAdd("application/dns-json");
                using var response = await DnsHttp.SendAsync(request);
                response.EnsureSuccessStatusCode();
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                if (!document.RootElement.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
                    return new List<string>();
                return answers.EnumerateArray()
                    .Where(item => item.TryGetProperty("data", out _))
                    .Select(item => item.GetProperty("data").GetString())
                    .Where(value => IPAddress.TryParse(value, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(value => IPAddress.Parse(value!).ToString())
                    .ToList();
            }
            catch { return new List<string>(); }
        }));
        var candidates = results.SelectMany(x => x).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var probes = await Task.WhenAll(candidates.Select(async address =>
            (Address: address, Reachable: await CanReachUnsplashAsync(hostname, address))));
        return probes.Where(x => x.Reachable).Select(x => x.Address).ToList();
    }

    private static async Task<bool> CanReachUnsplashAsync(string hostname, string address)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await client.ConnectAsync(IPAddress.Parse(address), 443, timeout.Token);
            using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
            {
                TargetHost = hostname
            }, timeout.Token);
            return tls.IsAuthenticated && tls.IsEncrypted;
        }
        catch { return false; }
    }

    private void OpenDnsChecker_Click(object sender, RoutedEventArgs e)
    {
        var hostname = (sender as System.Windows.Controls.Button)?.Tag as string;
        hostname = hostname is "images.unsplash.com" ? hostname : "api.unsplash.com";
        Process.Start(new ProcessStartInfo($"https://dnschecker.org/#A/{hostname}") { UseShellExecute = true });
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择壁纸缓存目录",
            UseDescriptionForTitle = true,
            SelectedPath = CacheBox.Text
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            CacheBox.Text = dialog.SelectedPath;
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized) Hide();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose && MinimizeBox.IsChecked == true)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        // Closing this settings window does not shut down the tray application.
        base.OnClosing(e);
    }
}
