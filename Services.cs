using Microsoft.Win32;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoWallpaper;

public sealed class AppSettings
{
    public string AccessKey { get; set; } = "";
    public string ApiIpOverride { get; set; } = "";
    public string ImageIpOverride { get; set; } = "";
    public string Query { get; set; } = "";
    public string Orientation { get; set; } = "landscape";
    public int IntervalMinutes { get; set; } = 60;
    public int FitMode { get; set; } = 0;
    public string CacheDirectory { get; set; } = SettingsStore.DefaultCache;
    public int MaxCacheCount { get; set; } = 30;
    public int MaxAgeDays { get; set; } = 30;
    public bool StartWithWindows { get; set; }
    public bool AutoAccent { get; set; }
    public bool MinimizeToTray { get; set; } = true;
}
public static class SettingsStore
{
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoWallpaper");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    public static string DefaultCache => Path.Combine(Dir, "Cache");
    public static AppSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); } catch { }
        return new();
    }
    public static void Save(AppSettings s)
    {
        Directory.CreateDirectory(Dir);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("AutoWallpaper", $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue("AutoWallpaper", false);
    }
}
public sealed class WallpaperService : IDisposable
{
    // Connect directly to a user-supplied A-record IP for api.unsplash.com while keeping
    // the original hostname in the HTTP request. SocketsHttpHandler still performs TLS
    // using api.unsplash.com as SNI and validates the certificate against that hostname.
    private static volatile string _apiIpOverride = "";
    private static volatile string _imageIpOverride = "";
    private static readonly SocketsHttpHandler HttpHandler = new()
    {
        // Do not keep a connection opened against an old IP after the user changes it.
        PooledConnectionLifetime = TimeSpan.Zero,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                var host = context.DnsEndPoint.Host;
                var overrideText = _apiIpOverride;
                var imageOverrideText = _imageIpOverride;
                var selectedOverride = string.Equals(host, "api.unsplash.com", StringComparison.OrdinalIgnoreCase)
                    ? overrideText
                    : string.Equals(host, "images.unsplash.com", StringComparison.OrdinalIgnoreCase)
                        ? imageOverrideText
                        : "";
                if (IPAddress.TryParse(selectedOverride, out var overrideAddress)
                    && overrideAddress.AddressFamily == AddressFamily.InterNetwork)
                {
                    await socket.ConnectAsync(new IPEndPoint(overrideAddress, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                }
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };
    private static readonly HttpClient Http = new(HttpHandler) { Timeout = TimeSpan.FromSeconds(25) };
    private readonly System.Threading.SemaphoreSlim _gate = new(1, 1);
    private readonly System.Threading.SemaphoreSlim _prefetchGate = new(1, 1);
    private readonly object _prefetchLock = new();
    private readonly Queue<string> _prefetchedImages = new();
    private const int MaxPrefetchedImages = 2;
    private System.Threading.Timer? _timer;
    private readonly object _timerLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private AppSettings _settings = SettingsStore.Load();
    public event Action<string>? StatusChanged;
    public void Start()
    {
        ApplySettings(_settings);
        if (Environment.GetCommandLineArgs().Contains("--background")) { /* App starts hidden through startup path. */ }
    }
    public void ApplySettings(AppSettings settings)
    {
        if (_disposed) return;
        var previous = _settings;
        var selectionChanged = !string.Equals(previous.Orientation, settings.Orientation, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previous.Query, settings.Query, StringComparison.Ordinal)
            || !string.Equals(previous.CacheDirectory, settings.CacheDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previous.AccessKey, settings.AccessKey, StringComparison.Ordinal);
        _settings = settings;
        if (selectionChanged)
        {
            // Do not serve images prefetched for a previous orientation/query/cache.
            // The files remain in the local cache and can still be used as offline fallback.
            lock (_prefetchLock) _prefetchedImages.Clear();
        }
        _apiIpOverride = NormalizeIpOverride(settings.ApiIpOverride);
        _imageIpOverride = NormalizeIpOverride(settings.ImageIpOverride);
        Directory.CreateDirectory(_settings.CacheDirectory);
        _ = Task.Run(() => CleanupCache());
        ScheduleNext();
        _ = EnsurePrefetchAsync();
    }

    // A one-shot timer avoids periodic callbacks while a download is still running.
    private void ScheduleNext()
    {
        lock (_timerLock)
        {
            if (_disposed) return;
            _timer?.Dispose();
            _timer = new System.Threading.Timer(
                static state => _ = ((WallpaperService)state!).TimerElapsedAsync(),
                this,
                TimeSpan.FromMinutes(Math.Clamp(_settings.IntervalMinutes, 1, 10080)),
                Timeout.InfiniteTimeSpan);
        }
    }

    private async Task TimerElapsedAsync()
    {
        try
        {
            if (!_disposed) await ChangeNowAsync();
        }
        finally
        {
            ScheduleNext();
        }
    }
    public async Task ChangeNowAsync()
    {
        if (_disposed) return;
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            StatusChanged?.Invoke("已有换图任务正在进行，请稍候…");
            return;
        }
        StatusChanged?.Invoke("正在准备壁纸，检查预取队列和本地图库…");
        string? selectedPath = null;
        var usedFallback = false;
        try
        {
            var settings = _settings;
            Directory.CreateDirectory(settings.CacheDirectory);

            // Prefer a previously downloaded image so switching works even if the network is down.
            selectedPath = TakePrefetchedImage(settings.CacheDirectory);
            if (selectedPath is null)
            {
                if (string.IsNullOrWhiteSpace(settings.AccessKey))
                {
                    selectedPath = FindCachedFallback(settings.CacheDirectory, settings.Orientation);
                    if (selectedPath is null)
                    {
                        StatusChanged?.Invoke("请填写 Unsplash Access Key；本地图库中也没有可用壁纸。 ");
                        return;
                    }
                }
                else
                {
                    try
                    {
                        selectedPath = await DownloadNextImageAsync(settings, _lifetime.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
                    catch (Exception networkError)
                    {
                        selectedPath = FindCachedFallback(settings.CacheDirectory, settings.Orientation);
                        if (selectedPath is null) throw;
                        usedFallback = true;
                    }
                }
            }

            _lifetime.Token.ThrowIfCancellationRequested();
            var accentSourcePath = selectedPath;
            if (settings.FitMode == 0)
            {
                StatusChanged?.Invoke("正在检查图片方向；纵向图片将生成横屏填充壁纸…");
                selectedPath = await Task.Run(() => CreatePortraitLandscapeWallpaperIfNeeded(selectedPath, settings), _lifetime.Token).ConfigureAwait(false);
            }
            SetWallpaper(selectedPath, settings.FitMode);
            if (settings.AutoAccent)
                await Task.Run(() => SetAccentFromImage(accentSourcePath), _lifetime.Token).ConfigureAwait(false);
            await Task.Run(() => CleanupCache(selectedPath), _lifetime.Token).ConfigureAwait(false);
            StatusChanged?.Invoke((usedFallback ? "网络不可用，已使用本地缓存壁纸：" : "已更新壁纸：") + Path.GetFileName(selectedPath));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Expected during application exit.
        }
        catch (Exception ex)
        {
            if (!_disposed) StatusChanged?.Invoke("换图失败：" + ex.Message);
        }
        finally
        {
            _gate.Release();
            if (!_disposed) _ = EnsurePrefetchAsync();
        }
    }

    private async Task EnsurePrefetchAsync()
    {
        if (_disposed || string.IsNullOrWhiteSpace(_settings.AccessKey)) return;
        if (!await _prefetchGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            while (!_disposed)
            {
                lock (_prefetchLock)
                    if (_prefetchedImages.Count >= MaxPrefetchedImages) return;
                var settings = _settings;
                try
                {
                    StatusChanged?.Invoke("预取中：正在后台准备下一张壁纸…");
                    var path = await DownloadNextImageAsync(settings, _lifetime.Token).ConfigureAwait(false);
                    lock (_prefetchLock)
                    {
                        if (_disposed) { try { File.Delete(path); } catch { } return; }
                        var current = _settings;
                        var settingsStillMatch = string.Equals(current.Orientation, settings.Orientation, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(current.Query, settings.Query, StringComparison.Ordinal)
                            && string.Equals(current.CacheDirectory, settings.CacheDirectory, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(current.AccessKey, settings.AccessKey, StringComparison.Ordinal);
                        if (!settingsStillMatch)
                        {
                            // A settings change occurred during download; don't queue an outdated result.
                            // Keep the downloaded file in cache for possible later use.
                            return;
                        }
                        if (_prefetchedImages.Count < MaxPrefetchedImages) _prefetchedImages.Enqueue(path);
                        else { try { File.Delete(path); } catch { } }
                    }
                    int readyCount;
                    lock (_prefetchLock) readyCount = _prefetchedImages.Count;
                    if (readyCount >= MaxPrefetchedImages)
                    {
                        StatusChanged?.Invoke($"后台预取完成：已缓存 {readyCount} 张壁纸，离线时也可切换。");
                        return;
                    }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    if (!_disposed) StatusChanged?.Invoke("预取暂不可用，将在切换时重试：" + ex.Message);
                    return;
                }
            }
        }
        finally { _prefetchGate.Release(); }
    }

    private string? TakePrefetchedImage(string cacheDirectory)
    {
        lock (_prefetchLock)
        {
            while (_prefetchedImages.Count > 0)
            {
                var path = _prefetchedImages.Dequeue();
                if (File.Exists(path) && string.Equals(Path.GetDirectoryName(path), cacheDirectory, StringComparison.OrdinalIgnoreCase))
                    return path;
            }
        }
        return null;
    }

    private static string? FindCachedFallback(string cacheDirectory, string? selectedOrientation)
    {
        try
        {
            if (!Directory.Exists(cacheDirectory)) return null;
            var orientation = (selectedOrientation ?? "landscape").Trim().ToLowerInvariant();
            if (orientation is not ("landscape" or "portrait" or "squarish" or "any")) orientation = "landscape";
            foreach (var file in new DirectoryInfo(cacheDirectory).GetFiles("*.jpg")
                .Where(f => !f.Name.StartsWith(".", StringComparison.Ordinal))
                .OrderByDescending(f => f.LastWriteTimeUtc))
            {
                try
                {
                    using var image = Image.FromFile(file.FullName);
                    var matches = orientation switch
                    {
                        "portrait" => image.Height > image.Width,
                        "squarish" => Math.Abs(image.Width - image.Height) <= Math.Max(image.Width, image.Height) * 0.15,
                        "any" => true,
                        _ => image.Width >= image.Height
                    };
                    if (matches) return file.FullName;
                }
                catch { /* Skip corrupt cache entries. */ }
            }
            return null;
        }
        catch { return null; }
    }

    private static string NormalizeIpOverride(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return IPAddress.TryParse(value.Trim(), out var address) ? address.ToString() : "";
    }

    private async Task<string> DownloadNextImageAsync(AppSettings settings, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(settings.AccessKey))
            throw new InvalidOperationException("请在设置中填写 Unsplash Access Key。");
        Directory.CreateDirectory(settings.CacheDirectory);
        var orientation = (settings.Orientation ?? "landscape").Trim().ToLowerInvariant();
        if (orientation is not ("landscape" or "portrait" or "squarish" or "any")) orientation = "landscape";
        var url = "https://api.unsplash.com/photos/random?count=30&content_filter=high&" +
                  (orientation == "any" ? "" : "orientation=" + Uri.EscapeDataString(orientation) + "&") +
                  (string.IsNullOrWhiteSpace(settings.Query) ? "" : "query=" + Uri.EscapeDataString(settings.Query) + "&");
        StatusChanged?.Invoke("正在向 Unsplash 获取候选壁纸…");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("AutoWallpaper/1.0");
        req.Headers.TryAddWithoutValidation("Authorization", "Client-ID " + settings.AccessKey);
        using var response = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var jsonStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(jsonStream, cancellationToken: token).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            throw new InvalidDataException("Unsplash 没有返回候选图片。");
        bool MatchesOrientation(JsonElement photo)
        {
            if (!photo.TryGetProperty("width", out var w) || !photo.TryGetProperty("height", out var h)) return false;
            var width = w.GetInt32();
            var height = h.GetInt32();
            return orientation switch
            {
                "portrait" => height > width,
                "squarish" => Math.Abs(width - height) <= Math.Max(width, height) * 0.15,
                "any" => true,
                _ => width >= height
            };
        }
        bool MeetsPreferredResolution(JsonElement photo)
        {
            if (!photo.TryGetProperty("width", out var w) || !photo.TryGetProperty("height", out var h)) return false;
            var width = w.GetInt32();
            var height = h.GetInt32();
            return orientation switch
            {
                "portrait" => width >= 1080 && height >= 1920,
                "squarish" => width >= 1080 && height >= 1080,
                "any" => (width >= 1920 && height >= 1080) || (width >= 1080 && height >= 1920),
                _ => width >= 1920 && height >= 1080
            };
        }
        var candidates = root.EnumerateArray()
            .Where(photo => MeetsPreferredResolution(photo)
                         && MatchesOrientation(photo)
                         && photo.TryGetProperty("urls", out var urls)
                         && urls.TryGetProperty("raw", out var raw) && !string.IsNullOrWhiteSpace(raw.GetString()))
            .ToList();
        if (candidates.Count == 0)
            candidates = root.EnumerateArray()
                .Where(photo => photo.TryGetProperty("width", out var w) && photo.TryGetProperty("height", out var h)
                             && MatchesOrientation(photo)
                             && photo.TryGetProperty("urls", out var urls)
                             && urls.TryGetProperty("raw", out var raw) && !string.IsNullOrWhiteSpace(raw.GetString()))
                .OrderByDescending(photo => (long)photo.GetProperty("width").GetInt32() * photo.GetProperty("height").GetInt32())
                .Take(5).ToList();
        if (candidates.Count == 0) throw new InvalidDataException($"Unsplash 本次没有返回符合“{OrientationLabel(orientation)}”方向的可用原图，请重试或更换关键词。");

        var selected = candidates[Random.Shared.Next(candidates.Count)];
        var photoId = selected.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N");
        var rawUrl = selected.GetProperty("urls").GetProperty("raw").GetString();
        if (string.IsNullOrWhiteSpace(rawUrl)) throw new InvalidDataException("Unsplash 未返回原图地址。");
        var targetWidth = orientation switch { "portrait" => 2160, "squarish" => 2160, _ => 3840 };
        var imageUrl = rawUrl + (rawUrl.Contains('?') ? "&" : "?") + $"w={targetWidth}&q=85&fm=jpg&fit=max";

        if (selected.TryGetProperty("links", out var links)
            && links.TryGetProperty("download_location", out var downloadLocation)
            && !string.IsNullOrWhiteSpace(downloadLocation.GetString()))
        {
            try
            {
                using var trackingRequest = new HttpRequestMessage(HttpMethod.Get, downloadLocation.GetString());
                trackingRequest.Headers.TryAddWithoutValidation("Authorization", "Client-ID " + settings.AccessKey);
                trackingRequest.Headers.UserAgent.ParseAdd("AutoWallpaper/1.0");
                using var trackingResponse = await Http.SendAsync(trackingRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { /* best-effort download tracking */ }
        }

        StatusChanged?.Invoke("正在下载高清壁纸…");
        var tempPath = Path.Combine(settings.CacheDirectory, $".{Guid.NewGuid():N}.download");
        try
        {
            using var imageResponse = await Http.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            imageResponse.EnsureSuccessStatusCode();
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await imageResponse.Content.CopyToAsync(output, token).ConfigureAwait(false);
            var valid = await Task.Run(() =>
            {
                using var img = Image.FromFile(tempPath);
                return orientation switch
                {
                    "portrait" => img.Height > img.Width,
                    "squarish" => Math.Abs(img.Width - img.Height) <= Math.Max(img.Width, img.Height) * 0.15,
                    "any" => true,
                    _ => img.Width >= img.Height
                };
            }, token).ConfigureAwait(false);
            if (!valid) throw new InvalidDataException($"收到的图片方向与当前设置“{OrientationLabel(orientation)}”不符，已拒绝保存并将在下次重试。");
            token.ThrowIfCancellationRequested();
            StatusChanged?.Invoke("图片下载完成，正在应用壁纸…");
            // Keep cache filenames short and readable; avoid appending a long GUID.
            var baseName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{photoId}";
            var path = Path.Combine(settings.CacheDirectory, baseName + ".jpg");
            var suffix = 1;
            while (File.Exists(path))
                path = Path.Combine(settings.CacheDirectory, $"{baseName}_{suffix++}.jpg");
            File.Move(tempPath, path, overwrite: false);
            return path;
        }
        finally { try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { } }
    }

    private static string OrientationLabel(string orientation) => orientation switch
    {
        "portrait" => "纵向",
        "squarish" => "方形",
        "any" => "不限方向",
        _ => "横向"
    };

    // Portrait-landscape beautifier inspired by portrait_landscape_beautifier.html.
    // Only invoked for Windows Fill mode (FitMode == 0); landscape images remain untouched.
    private static string CreatePortraitLandscapeWallpaperIfNeeded(string sourcePath, AppSettings settings)
    {
        using var source = Image.FromFile(sourcePath);
        if (source.Width >= source.Height) return sourcePath;

        const int canvasWidth = 1920, canvasHeight = 1080;
        // Keep the original photo timestamp and Unsplash photo ID in the generated filename.
        // Example: 20261028_110857_gGi6Ya4TqGw_beautified.png
        var sourceBaseName = Path.GetFileNameWithoutExtension(sourcePath);
        var outputBaseName = $"{sourceBaseName}_beautified";
        var outputPath = Path.Combine(settings.CacheDirectory, outputBaseName + ".png");
        int suffix = 2;
        while (File.Exists(outputPath))
            outputPath = Path.Combine(settings.CacheDirectory, $"{outputBaseName}_{suffix++}.png");

        using var canvas = new Bitmap(canvasWidth, canvasHeight, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.FromArgb(32, 40, 58));

            // Blur background: cover the canvas, downsample, Gaussian-blur, then upscale.
            using var low = new Bitmap(480, 270, PixelFormat.Format32bppArgb);
            using (var bg = Graphics.FromImage(low))
            {
                bg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                DrawCover(bg, source, 480, 270);
            }
            ApplyGaussianBlur(low, 6);
            g.DrawImage(low, new Rectangle(0, 0, canvasWidth, canvasHeight));

            // bgShade=5, bgBlend=10, autoBlend=true: softly darken and tint with a sampled image color.
            using (var shade = new SolidBrush(Color.FromArgb(13, 5, 8, 15))) g.FillRectangle(shade, 0, 0, canvasWidth, canvasHeight);
            var average = SampleAverageColor(source);
            using (var tint = new SolidBrush(Color.FromArgb(26, average))) g.FillRectangle(tint, 0, 0, canvasWidth, canvasHeight);

            // Smooth radial vignette, applied to the background only. Generate it at
            // reduced resolution to avoid hard ellipse boundaries and keep CPU usage low.
            ApplyBackgroundVignette(g, canvasWidth, canvasHeight, strength: 38);

            // Foreground image: zoom=90%, posX=75%, posY=30%, radius=12px.
            double scale = Math.Min((double)canvasWidth / source.Width, (double)canvasHeight / source.Height) * .90;
            int drawWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
            int drawHeight = Math.Max(1, (int)Math.Round(source.Height * scale));
            float dx = (float)((canvasWidth - drawWidth) * .75);
            float dy = (float)((canvasHeight - drawHeight) * .30);
            float radius = 12f;

            // Subtle shadow (opacity 15%, offset 0/15px), blurred on a quarter-size mask.
            using (var shadowLow = new Bitmap(canvasWidth / 4, canvasHeight / 4, PixelFormat.Format32bppArgb))
            {
                using (var sg = Graphics.FromImage(shadowLow))
                {
                    sg.SmoothingMode = SmoothingMode.AntiAlias;
                    using var shadowBrush = new SolidBrush(Color.FromArgb(38, Color.Black));
                    using var shadowPath = RoundedRectangle(new RectangleF(dx / 4, (dy + 15) / 4, drawWidth / 4f, drawHeight / 4f), radius / 4f);
                    sg.FillPath(shadowBrush, shadowPath);
                }
                ApplyGaussianBlur(shadowLow, 6);
                g.DrawImage(shadowLow, new Rectangle(0, 0, canvasWidth, canvasHeight));
            }

            using var foreground = new Bitmap(drawWidth, drawHeight, PixelFormat.Format32bppArgb);
            using (var fg = Graphics.FromImage(foreground))
            {
                fg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                fg.SmoothingMode = SmoothingMode.HighQuality;
                using var attributes = new ImageAttributes();
                // brightness/contrast/saturation all 100%; keep this matrix ready for matching the HTML settings.
                var matrix = new ColorMatrix();
                attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                fg.DrawImage(source, new Rectangle(0, 0, drawWidth, drawHeight), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
            }
            using (var clip = RoundedRectangle(new RectangleF(dx, dy, drawWidth, drawHeight), radius))
            {
                g.SetClip(clip);
                g.DrawImage(foreground, dx, dy, drawWidth, drawHeight);
                g.ResetClip();
            }
        }
        canvas.Save(outputPath, ImageFormat.Png);
        return outputPath;
    }


    private static void ApplyBackgroundVignette(Graphics graphics, int width, int height, byte strength)
    {
        const int maskWidth = 480, maskHeight = 270;
        using var mask = new Bitmap(maskWidth, maskHeight, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, maskWidth, maskHeight);
        var data = mask.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var pixels = new byte[stride * maskHeight];
            double cx = (maskWidth - 1) / 2.0, cy = (maskHeight - 1) / 2.0;
            for (int y = 0; y < maskHeight; y++)
            {
                for (int x = 0; x < maskWidth; x++)
                {
                    double dx = (x - cx) / cx;
                    double dy = (y - cy) / cy;
                    double distance = Math.Sqrt(dx * dx + dy * dy) / Math.Sqrt(2.0);
                    // Keep the central area untouched, then smoothly increase darkness
                    // toward the edges; no oversized ellipse or abrupt corners.
                    double t = Math.Clamp((distance - 0.52) / (1.0 - 0.52), 0.0, 1.0);
                    t = t * t * (3.0 - 2.0 * t);
                    byte alpha = (byte)Math.Round(strength * t);
                    int i = y * stride + x * 4;
                    pixels[i] = 0; pixels[i + 1] = 0; pixels[i + 2] = 0; pixels[i + 3] = alpha;
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally { mask.UnlockBits(data); }

        var oldInterpolation = graphics.InterpolationMode;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(mask, new Rectangle(0, 0, width, height));
        graphics.InterpolationMode = oldInterpolation;
    }

    private static void DrawCover(Graphics g, Image image, int width, int height)
    {
        double scale = Math.Max((double)width / image.Width, (double)height / image.Height) * 1.04;
        float w = (float)(image.Width * scale), h = (float)(image.Height * scale);
        g.DrawImage(image, (width - w) / 2, (height - h) / 2, w, h);
    }

    private static GraphicsPath RoundedRectangle(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Max(0, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
        if (d < 1) { path.AddRectangle(rect); return path; }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color SampleAverageColor(Image image)
    {
        using var sample = new Bitmap(24, 24, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(sample)) g.DrawImage(image, 0, 0, 24, 24);
        long r = 0, green = 0, b = 0, count = 0;
        for (int y = 0; y < 24; y++) for (int x = 0; x < 24; x++)
        {
            var c = sample.GetPixel(x, y);
            if (c.GetBrightness() < .12f || c.GetBrightness() > .92f) continue;
            r += c.R; green += c.G; b += c.B; count++;
        }
        return count == 0 ? Color.FromArgb(66, 75, 96) : Color.FromArgb((int)(r / count), (int)(green / count), (int)(b / count));
    }

    // Fast separable Gaussian blur for small working bitmaps (background and shadow layers).
    private static void ApplyGaussianBlur(Bitmap bitmap, int radius)
    {
        radius = Math.Clamp(radius, 1, 10);
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride, width = bitmap.Width, height = bitmap.Height;
            int length = Math.Abs(stride) * height;
            var input = new byte[length]; var temp = new byte[length]; var output = new byte[length];
            Marshal.Copy(data.Scan0, input, 0, length);
            var weights = new double[radius * 2 + 1]; double total = 0; double sigma = Math.Max(1, radius / 2.2);
            for (int i = -radius; i <= radius; i++) { var weight = Math.Exp(-(i * i) / (2 * sigma * sigma)); weights[i + radius] = weight; total += weight; }
            for (int i = 0; i < weights.Length; i++) weights[i] /= total;
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) for (int c = 0; c < 4; c++)
            {
                double sum = 0;
                for (int k = -radius; k <= radius; k++) { int xx = Math.Clamp(x + k, 0, width - 1); sum += input[y * stride + xx * 4 + c] * weights[k + radius]; }
                temp[y * stride + x * 4 + c] = (byte)Math.Clamp((int)Math.Round(sum), 0, 255);
            }
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) for (int c = 0; c < 4; c++)
            {
                double sum = 0;
                for (int k = -radius; k <= radius; k++) { int yy = Math.Clamp(y + k, 0, height - 1); sum += temp[yy * stride + x * 4 + c] * weights[k + radius]; }
                output[y * stride + x * 4 + c] = (byte)Math.Clamp((int)Math.Round(sum), 0, 255);
            }
            Marshal.Copy(output, 0, data.Scan0, length);
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static void SetWallpaper(string path, int fit)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop");
        // Windows wallpaper style values: Fill 10, Fit 6, Stretch 2, Center 0, Tile 0 + TileWallpaper=1.
        var style = fit switch { 0 => ("10", "0"), 1 => ("6", "0"), 2 => ("2", "0"), 3 => ("0", "0"), _ => ("0", "1") };
        key.SetValue("WallpaperStyle", style.Item1); key.SetValue("TileWallpaper", style.Item2);
        if (!SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    private static void SetAccentFromImage(string path)
    {
        using var bmp = new Bitmap(path);
        // Sample a tiny 24x24 grid; no full-resolution scan and no retained bitmap.
        long r = 0, g = 0, b = 0, n = 0;
        for (int y = 0; y < 24; y++) for (int x = 0; x < 24; x++)
        {
            var c = bmp.GetPixel(x * Math.Max(1, bmp.Width - 1) / 23, y * Math.Max(1, bmp.Height - 1) / 23);
            if (c.GetBrightness() < .12f || c.GetBrightness() > .92f) continue;
            r += c.R; g += c.G; b += c.B; n++;
        }
        if (n == 0) return;
        int rr = (int)(r / n), gg = (int)(g / n), bb = (int)(b / n);
        // Personalize\AccentColor is DWORD AABBGGRR.
        uint color = 0xFF000000u | ((uint)bb << 16) | ((uint)gg << 8) | (uint)rr;
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\DWM");
        key.SetValue("AccentColor", unchecked((int)color), RegistryValueKind.DWord);
        // Keep the sampled accent color available for apps, but do not tint title bars or window borders.
        key.SetValue("ColorPrevalence", 0, RegistryValueKind.DWord);
    }
    private void CleanupCache(string? keep = null)
    {
        try
        {
            if (!Directory.Exists(_settings.CacheDirectory)) return;
            var files = new DirectoryInfo(_settings.CacheDirectory).GetFiles("*.jpg").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            var cutoff = DateTime.UtcNow.AddDays(-_settings.MaxAgeDays);
            foreach (var f in files)
                if (f.FullName != keep && f.LastWriteTimeUtc < cutoff) { try { f.Delete(); } catch { } }
            files = new DirectoryInfo(_settings.CacheDirectory).GetFiles("*.jpg").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            foreach (var f in files.Skip(_settings.MaxCacheCount)) if (f.FullName != keep) { try { f.Delete(); } catch { } }
            var generated = new DirectoryInfo(_settings.CacheDirectory).GetFiles("*_beautified*.png").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            var generatedCutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var f in generated) if (f.FullName != keep && f.LastWriteTimeUtc < generatedCutoff) { try { f.Delete(); } catch { } }
            foreach (var f in generated.Skip(10)) if (f.FullName != keep) { try { f.Delete(); } catch { } }
        }
        catch { }
    }
    public void Dispose()
    {
        lock (_timerLock)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
        _lifetime.Cancel();
        // Keep the CTS alive until any in-flight async continuations have observed cancellation.
        // Do not dispose the semaphore while an in-flight async operation may still release it.
    }
    private const int SPI_SETDESKWALLPAPER = 0x0014, SPIF_UPDATEINIFILE = 0x01, SPIF_SENDCHANGE = 0x02;
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SystemParametersInfo(int action, int param, string value, int winIni);
}
