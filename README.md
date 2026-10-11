<img src="docs/icon-256.png" width="88" align="right" alt="NcmForge">

# NcmForge

**网易云音乐 `.ncm` 解密与转码工具**

[![CI](https://github.com/112012201/ncmforge/actions/workflows/ci.yml/badge.svg)](https://github.com/112012201/ncmforge/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/112012201/ncmforge?include_prereleases&sort=semver)](https://github.com/112012201/ncmforge/releases)
[![License: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)

> **English** — NcmForge decrypts NetEase Cloud Music `.ncm` containers and writes properly tagged
> **MP3, FLAC, AAC, ALAC, WavPack, Opus** and **Matroska** output, including **AVS3-P3 (Audio Vivid)**
> immersive tracks. Ships as a self-contained binary for Windows, Linux and macOS — no .NET install
> required. Built with .NET 8 and Avalonia. Documentation is in Chinese; see
> [`docs/ncm-format.md`](docs/ncm-format.md) for the reverse-engineered container format.

> ⚠️ **免责声明** — 本软件仅用于个人对**自己合法拥有**的音频文件做格式转换与备份。它不提供、
> 不托管、不分发任何音乐作品，也不绕过任何付费或会员权限。音频版权归原始权利人所有。
> 使用前请阅读 **[DISCLAIMER.md](DISCLAIMER.md)**。

---

## 特性

- **完整容器解析** —— AES-128-ECB 密钥层 + 元数据层 + 周期 256 字节的音频密钥流，支持三种负载布局
- **编码识别** —— MP3 / FLAC / **AVS3-P3（Audio Vivid）** / E-AC-3 / AC-3 / PCM / AAC
- **标签自己写，不调外部工具** —— ID3v2.3（含 APIC、USLT）、FLAC VORBIS_COMMENT + PICTURE、
  MP4 `udta/meta/ilst`（**合并**已有原子并修正 `stco`/`co64` 偏移）、Matroska
- **封面** —— 网易云官方优先，iTunes / Deezer / Cover Art Archive 兜底
- **歌词** —— 读取同名 `.lrc`，支持网易云 JSON 混合格式与 GBK 检测
- **重新编码开关** —— 关闭时只重封装（音频比特不变），开启后走编码降级梯
- **编码降级梯** —— 从 WavPack 到 MP3 逐档尝试，并**校验编码结果**而不是只信退出码
- **硬件加速** —— 实测探测本机编码器族，并诚实区分音频可用与仅视频的加速器
- **图形界面 + 命令行** —— 同一个核心库，两种用法

---

## 下载

到 [**Releases**](https://github.com/112012201/ncmforge/releases) 下载对应平台的包。
除 Linux 发行版包外都是自包含的，**目标机器无需安装 .NET**。

| 平台 | 架构 | 包 |
|---|---|---|
| Windows | x64 / x86 / ARM64 | `NcmForge-<版本>-win-*.zip` |
| macOS | Apple Silicon (arm64) / Intel (x64) | `NcmForge-<版本>-osx-*.tar.gz` |
| Linux | x64 / arm64 | `NcmForge-<版本>-linux-*.tar.gz` |
| Linux | amd64 / arm64 | `.deb` · `.rpm` · `.snap` |
| 源码 | — | `NcmForge-<版本>-src.tar.gz` |

每个 Windows / macOS 压缩包里同时有**图形界面**和**命令行**两个程序：

| | Windows | Linux / macOS |
|---|---|---|
| 图形界面 | `NcmForge.exe` | `NcmForge` |
| 命令行 | `ncmforge.exe` | `ncmforge` |

**校验下载完整性**：发布页附带 `SHA256SUMS`。

```bash
sha256sum -c SHA256SUMS --ignore-missing
```

### 安装 Linux 包

```bash
sudo dpkg -i ncmforge_*_amd64.deb            # Debian / Ubuntu
sudo dnf install ./ncmforge-*.x86_64.rpm     # Fedora / RHEL
sudo snap install --dangerous ./ncmforge_*.snap   # Snap
```

deb / rpm / snap 安装后提供 `/usr/bin/ncmforge`（命令行）与 `/usr/bin/ncmforge-gui`（图形界面），
并注册桌面菜单项。

> snap 使用 `classic` 隔离级别——本工具需要读写任意路径下的音乐文件。本地安装需要 `--dangerous`，
> 从 Snap Store 安装则不需要。

> 发布包**不包含 ffmpeg**（见下方「关于沉浸声」）。普通曲目无需任何额外配置。

---

## 快速上手

### 图形界面

把 `.ncm` 文件或文件夹拖进窗口，选好输出格式，点「开始转换」。支持：

- 输出格式：MP3 / FLAC / 自动兼容 / 原样无损提取
- 重新编码开关（开启后才显示硬件加速选项）
- 多声道处理：保持原样 / 降混 7.1 / 5.1 / 立体声
- 封面来源、歌词写入、码率
- 输出目录与 ffmpeg 路径

设置会自动保存在程序目录（便携模式）或用户配置目录，下次启动自动恢复。

### 命令行

```bash
ncmforge doctor                                  # 本机 ffmpeg 能力 + AVS3 检测 + 文件位置
ncmforge survey  <目录> [--recursive] [--json]   # 批量侦察：每个文件的布局 / 编码 / 声道
ncmforge inspect <file.ncm> [--dump <dir>]       # 单文件详情 + 内嵌封面
ncmforge convert <file.ncm | 目录> [选项]
ncmforge extract-es <file.m4a> [out]             # 抽出裸基本流
ncmforge crack   <file.ncm>                      # 暴力定位负载起点（复核新布局）
ncmforge pack    <音频> <out.ncm>                # 造测试样本
```

`convert` 常用选项：

| 选项 | 说明 |
|---|---|
| `--format <mp3\|flac\|auto\|copy>` | 目标格式，默认 `mp3`；`auto` 走编码降级梯 |
| `--reencode` | 强制重新编码（默认只在必要时转码，否则直接重封装） |
| `--hw <auto\|off\|mf\|nvenc\|nvcuda\|qsv\|amf\|vaapi\|…>` | 优先使用的编码器族，仅 `--reencode` 下生效 |
| `--surround <preserve\|stereo\|51\|71>` | 声道处理，默认 `preserve` |
| `--cover <none\|embedded\|online\|online+embedded>` | 封面来源 |
| `--lyrics <sidecar\|none>` | 是否写入同名 `.lrc`，默认 `sidecar` |
| `--bitrate <kbps>` | 有损码率（MP3 / AAC），默认 320 |
| `--out <目录>` | 输出目录 |
| `--ffmpeg <路径>` | 指定 ffmpeg（可指向带 AVS3 解码器的构建） |
| `--decoder "<命令>"` | 外部解码器模板，占位符 `{input}` `{output}` `{channels}` `{sampleRate}` |
| `--extract-es` | 输出裸基本流而非原始容器 |
| `--recursive` | 递归子目录 |

```bash
# 普通曲目 → 320k MP3，封面在线优先，写入歌词
ncmforge convert song.ncm --format mp3 --bitrate 320 --cover online+embedded

# 沉浸声 → 编码降级梯自动选择（首位 WavPack，完整保留 12 声道）
ncmforge convert immersive.ncm --format auto --reencode

# 沉浸声 → 原样无损提取（音频比特不变，只重写标签与封面）
ncmforge convert immersive.ncm --format copy

# 沉浸声 → 兼容性最好的 5.1 AAC
ncmforge convert immersive.ncm --format auto --reencode --surround 51
```

---

## 关于沉浸声：AVS3-P3（Audio Vivid / 菁彩声）

社区里流传「网易云的环绕音乐是把 E-AC-3 写进了 FLAC」。实测结论是：**文件确实被错标成 `.flac`，
但里面的编码不是 E-AC-3，而是 AVS3-P3。**

```
magic: 00 00 00 1C 66 74 79 70 69 73 6F 6D   [....ftypisom]
codec_tag_string=av3a   channels=12   sample_rate=44100
format_name=mov,mp4,m4a,3gp,3g2,mj2
```

**主流 ffmpeg 构建没有 `av3a` 解码器**，所以开箱状态下本工具会把这些文件**无损提取**为 `.m4a`
（标签、封面、歌词照常写入），而不是转码。要真正转码，二选一：

```bash
# 1) 指向一个带 av3a 解码器的 ffmpeg
ncmforge convert immersive.ncm --format auto --reencode --ffmpeg "D:\patched-ffmpeg\bin"

# 2) 提供外部解码器（要求输出 WAV）
ncmforge convert immersive.ncm --format flac --surround 71 \
  --decoder "python av3a_decoder.py \"{input}\" \"{output}\""
```

用 `ncmforge doctor` 确认是否可用——它会显示 `yes  av3a`。也可以把一个带解码器的 ffmpeg 放进
程序目录的 `tools/ffmpeg/`，程序会**优先**使用它。

> 播放这类文件的三个选择：网易云音乐客户端（自带解码器）、带 AV3A 的播放器
> （Windows 可用 [mpv-Yaozhi](https://github.com/Yaozhil/mpv-Yaozhi)，macOS 可用
> [nilaoda/iina-avs](https://github.com/nilaoda/iina-avs/releases)），或用本工具转成通用格式。

---

## 选择输出格式

输出格式下拉框里可以直接指定编码，也可以交给「自动兼容」逐档尝试。每一项都写明了它的
**有无损、最多多少声道、哪些播放器能放**：

| 格式 | `--format` | 类型 | 实测最多声道 | 兼容性 |
|---|---|---|---|---|
| **WavPack** | `wavpack` | 无损 | **64** | foobar2000 / VLC / mpv；系统自带播放器大多不行 |
| Dolby TrueHD | `truehd` | 无损 | 6 | VLC / PotPlayer / mpv；Windows Media Player 不支持 |
| E-AC-3 (DD+) | `eac3` | 有损 | 6 | 电视、功放与主流播放器广泛支持 |
| DTS | `dts` | 有损 | 6 | 家庭影院设备与 VLC / PotPlayer |
| FLAC | `flac` | 无损 | 8 | 全平台通用 |
| ALAC | `alac` | 无损 | 8 | Apple 生态原生；Windows Media Player 不支持 |
| Opus | `opus` | 有损 | 8 | 现代浏览器与移动端；老硬件播放器不支持 |
| AAC-LC (MP4) | `aac` | 有损 | 6 | 兼容性最好：Windows、移动端、各播放器通吃 |
| MP3 | `mp3` | 有损 | 2 | 所有播放器都支持 |

```bash
ncmforge convert song.ncm --format wavpack   # 12 声道原样保留，不降混
ncmforge convert song.ncm --format alac      # 8 声道，Apple 生态
ncmforge convert song.ncm --format auto      # 交给降级梯自己选
```

> **声道数是实测值，不是格式规范的上限。** 两者差距很大：WavPack 的容器规范允许 256 声道，
> 而 ffmpeg 的编码器实测停在 64；TrueHD 规范允许 16，编码器停在 6。上面写的是**这个工具实际
> 能交付的数字**，因为那才是选格式时需要的。若某个格式的上限达到或超过 255，界面上会按 255 显示。

点 `wavpack`、`flac` 这类明确的编码即表示要重新编码，不必再去勾「启用重新编码」。
而下拉框里的 `MP3` / `FLAC` 两个**模式**不同：负载已经是该格式时它们只做重封装，不会无谓地
二次有损压缩。

---

## 编码降级梯

选择「自动兼容」并开启「重新编码」后，按下列顺序逐档尝试，**失败一档降一档**，全部失败才会报错。
每一档的可用性都是**实测探测**本机 ffmpeg 得出的，而不是写死的。

```
WavPack → Dolby TrueHD → E-AC-3 (DD+) → DTS → FLAC → ALAC → Opus → AAC-LC (MP4) → MP3
```

**WavPack 排第一**：实测只有它能**无损承载全部 12 声道**，所以 12 声道的沉浸声第一次尝试就能
不降混地转出来。下面每一档都至少会丢掉一部分声道。

| 编码 | 实测声道上限 | 说明 |
|---|---|---|
| **WavPack** | **64** | 唯一能完整承载 12 声道（7.1.4）的选择 |
| Dolby TrueHD | 6 | ffmpeg 的 TrueHD 编码器超过 6 声道**直接报错** |
| E-AC-3 (DD+) | 6 | ffmpeg **无法**写入 Atmos 对象元数据，所以输出是 DD+ 而非真 Atmos |
| DTS | 6 | ffmpeg **没有** DTS-HD MA 编码器，`dca` 产出的是有损 DTS 核心 |
| FLAC | 8 | 规范上限 |
| ALAC | 8 | — |
| Opus | 8 | 标准声道映射上限 |
| AAC-LC | 6 | 兼容性兜底 |
| MP3 | 2 | 兼容性兜底 |

### 为什么不只信退出码

**ffmpeg 会「成功但静默降混」。** 实测：要求 12 声道时，E-AC-3 与 DTS 会安静地输出 5.0，
ALAC 会输出 6.1，而**退出码都是 0**。

因此本项目在每次编码后会**读回实际声道数**校验，不符就显式指定 `-ac` 重试，仍不符才降档。
没有这一步，降级梯会停在第一个「看起来成功」的档位上，交给你一个残缺的混音。

发生降混或降到底部兜底编码时，界面底部会给出明确警告，同时写入日志。

---

## 硬件加速：一个必须说清的结论

工具会实测本机 ffmpeg 提供了哪些编码器族：

```bash
ncmforge doctor
```

```
=== hardware encoders ===
  mf       audio + video   aac_mf, ac3_mf, av1_mf, h264_mf, …
  nvenc    video only      av1_nvenc, h264_nvenc, hevc_nvenc
  cuda     video only      cuda
  qsv      video only      av1_qsv, h264_qsv, hevc_qsv, mjpeg_qsv, …
  amf      video only      av1_amf, h264_amf, hevc_amf
  vaapi    video only      av1_vaapi, h264_vaapi, hevc_vaapi, mjpeg_vaapi, …
```

**NVENC / NVCUDA / QSV / VA-API / VCE(UVD) / VideoToolbox / MediaCodec / V4L2 全部是视频编码器**，
对音频输出不起作用。ffmpeg 里唯一带**音频**编码器的是 Windows Media Foundation：

| 加速族 | 音频编码器 | 效果 |
|---|---|---|
| **Media Foundation**（`--hw mf`） | `aac_mf` / `mp3_mf` / `ac3_mf` | ✅ 真正加速音频编码 |
| 其余全部 | 无 | ❌ 仅视频；界面会明确提示「音频仍需 CPU 编码」 |

`--hw auto` 会自动挑出唯一能加速音频的那一族。加速编码失败时（驱动、编码档次、声道布局等）
会自动**回退 CPU 编码器重试一次**，不会因为一个「优先项」而让转换失败。

---

## 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```bash
git clone https://github.com/112012201/ncmforge.git
cd ncmforge
dotnet build NcmForge.sln -c Release
dotnet test  NcmForge.sln -c Release
```

发布自包含包（Windows 为例）：

```powershell
# 多文件布局（默认，启动更快，无需可写的 %TEMP%）
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Rids win-x64

# 若本地有一个带 av3a 解码器的 ffmpeg，一并打包进去
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Rids win-x64 `
    -FfmpegDir "D:\ffmpeg-avs" -FfmpegInCli

# 单文件 exe（可选）
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -SingleFile
```

应用图标由脚本生成，改设计不需要设计工具：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\make-icon.ps1
```

---

## 项目结构

```
src/NcmForge.Core/          无 UI 依赖的核心库
  Ncm/                      容器读写：解析、负载定位、解密流、密钥层、写入器
  Audio/                    签名嗅探与容器探测（MP4 盒子遍历、FLAC 元数据）
  Tagging/                  ID3v2.3 / VORBIS_COMMENT / MP4 ilst 写入
  Lyrics/                   网易云混合格式歌词解析（BOM → UTF-8 → GB18030）
  Covers/                   多来源封面抓取
  Pipeline/                 转换流水线、编码选择、编码降级梯、声道校验
  Tools/                    ffmpeg 定位与调用、HTTP
  AppPaths.cs               程序使用位置（便携优先）

src/NcmForge.Cli/           命令行前端
src/NcmForge.App/           Avalonia 图形界面
tests/NcmForge.Tests/       单元测试
scripts/                    发布与图标生成脚本
docs/                       文档（含 .ncm 格式说明）
```

### 三个刻意的设计决定

1. **标签自己写，不 shell 到 ffmpeg。** 这样才能控制 MP4 原子合并与 `stco` 偏移修正，也才能在
   没有 ffmpeg 的环境里完成重封装。
2. **自带写入器。** `NcmWriter` 让「写入 → 读回」可以互相验证，格式理解不再只靠单向推断。
3. **不启用裁剪（trimming）。** Avalonia 通过反射解析 XAML 类型，裁剪器看不到这些引用，会导致运行时崩溃。

---

## 测试

```bash
dotnet test NcmForge.sln -c Release
```

覆盖：容器解析与布局识别、密钥流（含分块与整体解密一致性）、三种标签写入、MP4 原子重写与偏移修正、
编码识别、编码降级梯的档位与声道上限、程序路径。

CI 在 Windows / Linux / macOS 三平台构建并运行测试，另有一个「干净机器上没有 ffmpeg 时
`doctor` 能否优雅报错」的检查。

---

## 已知限制

1. **Dolby Atmos 做不出来** —— ffmpeg 无法写入对象音频元数据。E-AC-3 只是载体，输出是 DD+ 而非真 Atmos。
2. **DTS-HD MA 做不出来** —— ffmpeg 没有该编码器，`dca` 产出的是有损 DTS 核心。
3. **TrueHD / DTS 用的是实验性编码器**（需 `-strict -2`），且上限只有 6 声道。
4. **FLAC 上限 8 声道**；要完整保留 12 声道请用 WavPack，或用「原样无损提取」。
5. **音频没有真正的硬件编码器**（除 Media Foundation），见上。
6. `libfdk_aac` 在常见构建中不可用，AAC 使用 ffmpeg 原生编码器。
7. 部分格式（TrueHD、DTS、WavPack）**不被所有播放器支持**，这是格式生态的客观限制。

---

## 相关文档

- [`.ncm` 容器格式说明](docs/ncm-format.md) —— 逐字段的格式逆向结果
- [开发任务列表](docs/任务列表.md) —— 已完成 / 待办 / 验证证据
- [免责声明](DISCLAIMER.md)
- [第三方组件与许可证](THIRD-PARTY-NOTICES.md)

---

## 许可证

以 **GNU General Public License v3.0（或更新版本）** 发布，全文见 [LICENSE](LICENSE)。

你可以自由使用、修改和再分发，但**再分发时必须同样开源**，且不得附加额外限制。

## 致谢

- [FFmpeg](https://ffmpeg.org/) —— 解码与转码
- [Avalonia](https://avaloniaui.net/) —— 跨平台界面
- [SkiaSharp](https://github.com/mono/SkiaSharp) / [HarfBuzzSharp](https://github.com/mono/SkiaSharp) —— 图形与文本渲染
- [Inter](https://github.com/rsms/inter) —— 界面字体

本项目与网易公司、Dolby、DTS 均无关联，也未获得其赞助或认可。
