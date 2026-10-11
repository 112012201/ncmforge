# 第三方组件与许可证声明

NcmForge 自身以 **GPL-3.0-or-later** 发布（见 [LICENSE](LICENSE)）。它同时使用、并在发布包中随附以下第三方组件。

> 本文件按各组件**自己声明**的许可证整理。若与上游声明不一致，以上游为准。

---

## 一、随附的可执行程序（独立进程调用）

### FFmpeg

| 项目 | 内容 |
|---|---|
| 位置 | `tools/ffmpeg/ffmpeg.exe`（以及可选的 `ffprobe.exe`） |
| 版本标识 | `ffmpeg version - HDR Vivid encoder v1.0.1 a319c0f-ffmpeg-av3a` |
| 许可证 | **GNU General Public License v3**（全文见 `tools/ffmpeg/COPYING.GPLv3.txt`） |
| 著作权 | FFmpeg 项目及其贡献者 |
| 源码 | <https://ffmpeg.org/download.html> · <https://git.ffmpeg.org/ffmpeg.git> |

**调用方式：** NcmForge 以**独立进程**方式启动它，通过命令行参数交互，不链接、不修改其代码。因此二者属于"聚合"（mere aggregation）而非派生作品。

**为什么随附：** 这一构建带有 `libarcdav3a`（`AV3a decoder by ArcVideo`），能解码 AVS3-P3 / Audio Vivid 沉浸声；主流构建没有该解码器。

**合规义务：** 再分发本发布包时，需保留 `COPYING.GPLv3.txt` 全文，并提供对应源代码的获取方式（见 `tools/ffmpeg/NOTICE.txt`）。

**不需要时：** 直接删除 `tools/ffmpeg/` 目录即可，程序会回退到系统 PATH 上的 ffmpeg。

---

## 二、随附的字体

### Inter

| 项目 | 内容 |
|---|---|
| 位置 | 由 `Avalonia.Fonts.Inter` 嵌入 |
| 许可证 | **SIL Open Font License 1.1** (OFL-1.1) |
| 著作权 | The Inter Project Authors |
| 源码 | <https://github.com/rsms/inter> |

---

## 三、.NET 运行时（随自包含发布包分发）

| 项目 | 内容 |
|---|---|
| 组件 | .NET 8 Runtime（`coreclr.dll`、`hostfxr.dll`、`System.*.dll` 等） |
| 许可证 | **MIT** |
| 著作权 | .NET Foundation and Contributors |
| 源码 | <https://github.com/dotnet/runtime> |

自包含发布包中包含完整的 .NET 运行时，因此目标机器无需预装 .NET。

---

## 四、NuGet 依赖

### 界面框架

| 包 | 版本 | 许可证 | 著作权 |
|---|---|---|---|
| Avalonia | 11.3.22 | MIT | Avalonia Team |
| Avalonia.Desktop | 11.3.22 | MIT | Avalonia Team |
| Avalonia.Themes.Fluent | 11.3.22 | MIT | Avalonia Team |
| Avalonia.Fonts.Inter | 11.3.22 | MIT | Avalonia Team |
| Avalonia.Win32 | 11.3.22 | MIT | Avalonia Team |
| Avalonia.X11 | 11.3.22 | MIT | Avalonia Team |
| Avalonia.Native | 11.3.22 | MIT | Avalonia Team |
| Avalonia.Skia | 11.3.22 | MIT | Avalonia Team |
| Avalonia.FreeDesktop | 11.3.22 | MIT | Avalonia Team |
| Avalonia.Remote.Protocol | 11.3.22 | MIT | Avalonia Team |

源码：<https://github.com/AvaloniaUI/Avalonia>

### 图形与文本渲染

| 包 | 版本 | 许可证 | 著作权 |
|---|---|---|---|
| SkiaSharp | 2.88.9 | MIT | Microsoft / Mono Project |
| SkiaSharp.NativeAssets.Win32 / Linux / macOS | 2.88.9 | MIT | Microsoft / Mono Project |
| HarfBuzzSharp | 8.3.1.1 | MIT | Microsoft / Mono Project |
| HarfBuzzSharp.NativeAssets.Win32 / Linux / macOS | 8.3.1.1 | MIT | Microsoft / Mono Project |

源码：<https://github.com/mono/SkiaSharp>

> **关于 Skia 本体：** `libSkiaSharp` 封装的是 Google Skia，Skia 本身以 **BSD-3-Clause** 授权，著作权归 Google LLC。源码：<https://skia.org/>

> **关于 HarfBuzz 本体：** `libHarfBuzzSharp` 封装的是 HarfBuzz，其本身以 **MIT** 授权（旧版为 "MIT Old Style"），著作权归 HarfBuzz 贡献者。源码：<https://github.com/harfbuzz/harfbuzz>

### 其他

| 包 | 版本 | 许可证 | 用途 |
|---|---|---|---|
| Avalonia.Angle.Windows.Natives | 2.1.25547.20250602 | BSD-3-Clause (ANGLE) | Windows 上的 OpenGL ES 后端 |
| MicroCom.Runtime | 0.11.0 | MIT | COM 互操作 |
| Tmds.DBus.Protocol | 0.21.3 | MIT | Linux 桌面集成 |
| System.IO.Pipelines | 8.0.0 | MIT | 异步 I/O |

> `av_libglesv2.dll` 是 ANGLE（Almost Native Graphics Layer Engine），以 **BSD-3-Clause** 授权，著作权归 The ANGLE Project Authors。源码：<https://chromium.googlesource.com/angle/angle>

---

## 五、许可证全文获取

- **MIT**：<https://opensource.org/licenses/MIT>
- **BSD-3-Clause**：<https://opensource.org/licenses/BSD-3-Clause>
- **OFL-1.1**：<https://opensource.org/licenses/OFL-1.1>
- **GPL-3.0**：见本仓库 [LICENSE](LICENSE)

各 NuGet 包的完整许可证文本随包分发，可在本地 NuGet 缓存（`~/.nuget/packages/<包名>/<版本>/`）中查看。

---

## 六、商标声明

"Dolby"、"Dolby Atmos"、"Dolby Digital Plus"、"DTS"、"DTS-HD" 等是各自权利人的商标或注册商标。

**NcmForge 与上述权利人无任何关联，也未获得其赞助或认可。** 本软件仅通过 FFmpeg 生成对应格式的码流；其中：

- FFmpeg **无法**写入 Dolby Atmos 的对象元数据，因此"E-AC-3 / DD+"输出是 DD+ 码流，**不是**真正的 Atmos；
- FFmpeg **没有** DTS-HD MA 编码器，DTS 输出是**有损** DTS 核心（`dca`/dcaenc），**不是** DTS-HD MA。

"NCM"、"网易云音乐" 是网易公司的商标。本软件与网易公司无任何关联。
