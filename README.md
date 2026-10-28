# 自动切换壁纸（Windows）

轻量级 WPF 桌面应用，使用官方 Unsplash API 获取横屏壁纸，带自定义托盘图标、缓存清理、开机自启和基于壁纸的强调色设置。

## 目录

- [运行环境](#运行环境)
- [安装与启动](#安装与启动)
- [配置 Unsplash](#配置-unsplash)
- [构建](#构建)
- [单文件发布与体积说明](#单文件发布与体积说明)
- [功能](#功能)
- [低占用说明](#低占用说明)
- [Unsplash 高清策略](#unsplash-高清策略)
- [预取队列与离线回退](#预取队列与离线回退)
- [Unsplash IP 覆盖（可选）](#unsplash-ip-覆盖可选)
- [纵向图片填充优化](#纵向图片填充优化)
- [缓存与文件命名](#缓存与文件命名)
- [故障排查](#故障排查)
- [开发与发布前检查](#开发与发布前检查)
- [许可证](#许可证)

## 运行环境

- Windows 10/11 x64
- .NET 8 SDK（构建时需要）
- 如果使用**自包含发布版**，通常无需单独安装 .NET 运行时。
- 如果使用**依赖框架发布版**，目标电脑需要安装与应用匹配的 .NET 8 Desktop Runtime。

## 安装与启动

### 使用已发布版本

1. 从 GitHub Releases 下载应用压缩包。
2. 将压缩包解压到一个固定目录，例如 `C:\Apps\AutoWallpaper`。
3. 如果压缩包中包含多个 DLL 或运行时文件，请将它们与 `AutoWallpaper.exe` 保存在同一目录中，不要只复制 EXE。
4. 双击 `AutoWallpaper.exe` 启动。
5. 打开设置窗口，配置 Unsplash Access Key、关键词及其他选项。

> **重要：** 单文件发布并不保证所有依赖都会合并进 EXE。若发布目录中包含 `wpfgfx_cor3.dll`、`PresentationNative_cor3.dll` 等文件，请保留它们。不要为了减少文件数量而直接删除依赖库。

### 从源码构建

先安装 .NET 8 SDK，并获取本仓库源码。在包含 `AutoWallpaper.csproj` 的目录中执行“构建”章节的命令。如果项目文件位于子目录，请先进入该目录再运行命令。

## 配置 Unsplash

1. 前往 https://unsplash.com/developers 注册/登录并创建应用。
2. 复制应用的 **Access Key**。
3. 运行应用，在 Unsplash API 区域粘贴 Access Key。关键词可留空，或输入 `nature`、`architecture` 等。
4. 点击“保存设置”，再点“立即换一张”。

请遵守 Unsplash API 的使用条款及归因要求。公开分发时建议实现官方要求的下载事件跟踪，并展示摄影师和 Unsplash 归因链接。

## 构建

在此目录打开 PowerShell：

```powershell
dotnet clean
dotnet restore

dotnet publish .\AutoWallpaper.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None `
  -p:DebugSymbols=false
```

如果目标电脑已安装匹配的 .NET 8 Desktop
Runtime，也可以尝试框架依赖发布：

```powershell
dotnet publish .\AutoWallpaper.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false
```

发布结果通常在 `bin\Release\net8.0-windows\win-x64\publish\`。

## 单文件发布与体积说明

项目可以尝试发布为 Windows x64 单文件程序。自包含发布会将 .NET 运行时等依赖一并打包，因此 EXE 可能比较大；例如约 154 MB 的文件大小并不必然意味着构建出错。可在发布命令中额外加入 `-p:EnableCompressionInSingleFile=true` 尝试压缩单文件中的程序集，可能减少体积，但会带来一定启动解压开销。

**发布目录中的文件不要随意删除。** WPF 可能仍会输出 `wpfgfx_cor3.dll`、`PenImc_cor3.dll`、`vcruntime140_cor3.dll`、`PresentationNative_cor3.dll`、`D3DCompiler_47_cor3.dll` 等原生组件。如果这些文件存在且应用依赖它们，应随程序一起分发。

不要在没有完整测试的情况下对 WPF 项目启用激进裁剪。

## 功能

- 自定义壁纸主题托盘图标；托盘菜单包含打开设置、立即换图和退出。
- 关闭设置窗口默认隐藏到系统托盘；启动参数 `--background` 可直接后台启动。
- 退出时先移除托盘图标，再取消进行中的网络请求并释放服务，避免后台任务拖住进程。
- 一次性计时器；网络请求串行执行，避免重入。
- Unsplash 查询固定 `orientation=landscape`，下载后在后台线程检查宽高，拒绝竖图。
- 下载、图片解码、取色和缓存清理不阻塞 WPF UI 线程。
- 支持 Fill / Fit / Stretch / Center / Tile。
- 缓存按最大保存天数和最大张数清理。
- 开机自启写入当前用户 Run 注册表项，不需要管理员权限。
- 自动强调色会从壁纸采样颜色，但将 Windows DWM `ColorPrevalence` 设为 `0`，避免把强调色应用到标题栏和窗口边框。
- 配置保存在 `%LOCALAPPDATA%\AutoWallpaper\settings.json`。

## 低占用说明

程序不运行后台动画或高频轮询；使用 `--background` 启动时不会创建 WPF 设置窗口，点击托盘才按需创建。网络和图片处理在后台执行，下载完成后及时释放图像资源。WPF/.NET 运行时仍有基础常驻内存，具体占用需在 Windows 实机测量。应用启动时不会自动立即下载第一张，首次使用请点“立即换一张”。

### Unsplash 高清策略

- 每次请求最多 30 张随机候选，而不是只取一张。
- 优先筛选横屏且原始尺寸至少为 1920×1080 的图片，再从候选中随机选取。
- 使用 Unsplash 返回的 `urls.raw`，请求 `w=3840&q=85&fm=jpg&fit=max`，避免使用通常只有 1080px 宽的 `urls.regular`。
- 通过 `Authorization: Client-ID ...` 发送 API Key，并调用照片返回的 `links.download_location` 进行下载统计。
- 如果这一批没有达到 1920×1080 的候选，会退化选择可用横屏原图中像素面积较大的图片。

## 预取队列与离线回退

- 后台最多预下载并缓存 2 张候选壁纸；手动或定时切换时优先使用已下载的图片，减少等待。
- 网络请求失败时，自动从本地图库（缓存目录中的 JPG）选择最近的可用图片继续切换。
- 即使未配置 Access Key，只要本地缓存中已有壁纸，也可以继续切换到缓存图片。
- 预取失败不会阻断应用运行；切换时会再次尝试网络，成功后继续补充队列。
- 缓存数量与保留天数仍由设置中的本地缓存参数控制。

### Unsplash IP 覆盖（可选）

设置页支持分别为 `api.unsplash.com`（API）和 `images.unsplash.com`（图片 CDN）指定 IPv4。点击各自的“刷新 IP”会通过 Google Public DNS 和 Cloudflare DNS 查询 A 记录，并尝试对目标域名进行 TLS 握手验证。指定 IP 仅用于 TCP 连接；HTTP Host、TLS SNI 和证书验证仍保留原始域名。将任一输入框清空即可让该域名恢复系统 DNS。DNS 返回的地址可能受 CDN 调度、地理位置和网络运营商影响，并不保证当前网络可用。

### 纵向图片填充优化

当图片方向为纵向且 Windows 填充方式选择 Fill 时，程序会在应用壁纸前自动生成 1920×1080 的 PNG 横屏壁纸：使用原图生成柔焦背景、轻微暗化与自适应色调、暗角，并将清晰的纵向图片以圆角卡片形式叠放在前景，带轻微阴影。参数参考 `portrait_landscape_beautifier.html` 的给定配置。横向图片和其他 Windows 填充方式保持原样。生成图缓存最多保留 10 张，并清理超过 7 天的旧生成图。

## 缓存与文件命名

缓存数量与保留天数由设置中的本地缓存参数控制；生成图缓存最多保留 10 张，并清理超过 7 天的旧生成图。

应用会在缓存目录中保存下载图片和可能生成的美化图片。请以当前版本设置窗口或源码中的实际缓存路径为准。

示例命名：

- 原图：`20261028_110000_xxxxxxx.jpg`
- 美化图：`20261028_110000_xxxxxxx_beautified.png`

美化图沿用源图片的时间戳和 Unsplash 图片 ID，并添加 `_beautified` 后缀，便于识别对应关系。部分版本可能还会在文件名后添加数字后缀以避免覆盖已有文件。

如需清理磁盘空间，优先使用应用提供的缓存管理功能；手动删除缓存前请先退出应用，避免正在使用的文件被删除或重新下载。

## 故障排查

### 双击 EXE 没有反应

- 如果 EXE 在 `publish` 目录可以启动，但复制到其他目录后无法启动，请优先把整个 `publish` 目录复制过去进行测试。
- 检查程序是否依赖同目录 DLL、配置文件或资源文件。
- 检查代码是否使用相对路径读取设置、缓存或资源。
- 查看 Windows“事件查看器”中的 Windows 日志 → 应用程序，查找启动错误。
- 不要直接删除发布目录中的 WPF 原生 DLL。

### 壁纸没有自动更换

- 确认网络连接正常。
- 检查 Unsplash Access Key、关键词及相关参数是否有效。
- 确认应用没有被退出，并检查系统托盘图标。
- 检查缓存目录是否可写，以及当前设置的方向和适配模式。

### 纵向图片没有生成美化图

- 确认实际图片为纵向，且 Windows 填充方式选择 Fill。
- 如果使用固定 `orientation=landscape` 的 Unsplash 查询，竖图会在下载后被拒绝，不会进入美化流程；请确认当前版本/图片来源是否支持纵向图片。
- 检查缓存目录权限，并查看应用日志（如果当前版本提供日志功能）。

### EXE 体积较大

自包含发布会包含运行时。可以尝试单文件压缩、关闭调试符号，或改为依赖目标电脑已安装的 .NET Desktop Runtime 发布。不要在没有完整测试的情况下对 WPF 项目启用激进裁剪。

## 开发与发布前检查

推荐使用：

- Visual Studio 2022（支持 .NET 8 的版本）或 .NET 8 SDK
- Windows x64
- WPF

提交发布版本前，建议至少验证：

- 程序能正常启动和退出
- 设置保存与重新启动后的配置恢复
- 托盘菜单和后台运行
- 图片下载、切换和缓存
- 纵向图片美化及横向图片保持原样
- 开机自启动
- 在未安装 .NET 运行时的 Windows x64 环境中测试自包含发布包