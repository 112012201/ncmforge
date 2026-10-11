using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using NcmForge.App.Settings;
using NcmForge.Core.Pipeline;
using NcmForge.Core.Tools;

namespace NcmForge.App.ViewModels;

/// <summary>Drives the whole window: the queue, the settings, and the conversion run.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly HttpClient _http = NcmForgeHttp.CreateClient();
    private readonly ConversionPipeline _pipeline;
    private readonly RelayCommand _convertCommand;
    private readonly RelayCommand _removeCommand;

    private CancellationTokenSource? _cancellation;
    private string _outputDirectory = string.Empty;
    private string _ffmpegPath = string.Empty;
    private string _externalDecoderCommand = string.Empty;
    private string _statusText = "把 .ncm 文件拖进来，或点击「添加文件」";
    private string _progressText = string.Empty;
    private string _hardwareSummary = "尚未探测硬件编码器。";
    private string _warningText = string.Empty;
    private bool _isBusy;
    private double _progress;
    private int _bitrate = 320;
    private int _flacLevel = 5;
    private bool _extractElementaryStream;
    private bool _keepTemporaryFiles;

    public MainViewModel()
    {
        _pipeline = new ConversionPipeline(_http);

        Formats =
        [
            new FormatChoice("MP3（无损转码 / 直接重封装）", OutputFormat.Mp3),
            new FormatChoice("FLAC（多声道无损，最多 8 声道）", OutputFormat.Flac),
            new FormatChoice("自动兼容（同级编码，优先通用播放器可解）", OutputFormat.Compatible),
            new FormatChoice("原样无损提取（保留原始容器与声道）", OutputFormat.LosslessCopy),
        ];

        Surrounds =
        [
            new SurroundChoice("保持原声道数", SurroundMapping.Preserve),
            new SurroundChoice("降混为 7.1（8 声道）", SurroundMapping.DownmixTo71),
            new SurroundChoice("降混为 5.1（6 声道）", SurroundMapping.DownmixTo51),
            new SurroundChoice("降混为立体声（2 声道）", SurroundMapping.DownmixToStereo),
        ];

        Covers =
        [
            new CoverChoice("在线优先，失败时用内嵌封面", CoverMode.OnlineThenEmbedded),
            new CoverChoice("仅用内嵌封面", CoverMode.Embedded),
            new CoverChoice("仅在线获取（网易云 / iTunes / Deezer / CAA）", CoverMode.Online),
            new CoverChoice("不写入封面", CoverMode.None),
        ];

        DecoderInputs =
        [
            new DecoderInputChoice("裸基本流（如 .av3a，参考解码器需要）", ExternalDecoderInput.ElementaryStream),
            new DecoderInputChoice("解密后的原始负载", ExternalDecoderInput.Payload),
        ];

        LyricsModes =
        [
            new LyricsChoice("写入同目录 .lrc 歌词", LyricsMode.Sidecar),
            new LyricsChoice("不写入歌词", LyricsMode.None),
        ];

        _selectedLyrics = LyricsModes[0];

        // Populated from the real ffmpeg once a path is known.
        HardwareModes = [];
        _selectedHardware = new HardwareChoice("关闭（CPU 编码）", HardwareAcceleration.Disabled);
        HardwareModes.Add(_selectedHardware);

        _selectedFormat = Formats[0];
        _selectedSurround = Surrounds[0];
        _selectedCover = Covers[0];
        _selectedDecoderInput = DecoderInputs[0];

        // Default the output directory to the user's music folder when one exists.
        string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        _outputDirectory = Directory.Exists(music) ? Path.Combine(music, "NcmForge") : string.Empty;

        _convertCommand = new RelayCommand(() => _ = ConvertAllAsync(), () => !IsBusy && Tracks.Count > 0);
        _removeCommand = new RelayCommand(RemoveSelected, () => !IsBusy && Tracks.Any(t => t.IsSelected));
        RemoveSelectedCommand = _removeCommand;

        AddFilesCommand = new RelayCommand(AddFiles, () => !IsBusy);
        AddFolderCommand = new RelayCommand(AddFolder, () => !IsBusy);
        ClearCommand = new RelayCommand(ClearAll, () => !IsBusy && Tracks.Count > 0);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        OpenOutputCommand = new RelayCommand(OpenOutputFolder);
        CopyLogCommand = new RelayCommand(CopyLog);
        DetectFfmpegCommand = new RelayCommand(DetectFfmpeg);

        // Restore the saved ffmpeg path before detection, so the hardware list is built from the
        // build the user actually chose; the remaining options are applied afterwards, once the
        // hardware choices exist to match against.
        AppSettings saved = SettingsStore.Load(AppendLog);
        if (!string.IsNullOrWhiteSpace(saved.FfmpegPath))
        {
            FfmpegPath = saved.FfmpegPath!;
        }

        TrackSelectionChanged();
        DetectFfmpeg(silent: true);
        ApplySettings(saved);
    }

    /// <summary>Restores saved options, ignoring any value this build no longer offers.</summary>
    private void ApplySettings(AppSettings settings)
    {
        SelectedFormat = Formats.FirstOrDefault(f => f.Value == SettingsStore.Parse(settings.OutputFormat, OutputFormat.Mp3)) ?? Formats[0];
        SelectedSurround = Surrounds.FirstOrDefault(s => s.Value == SettingsStore.Parse(settings.Surround, SurroundMapping.Preserve)) ?? Surrounds[0];
        SelectedCover = Covers.FirstOrDefault(c => c.Value == SettingsStore.Parse(settings.Cover, CoverMode.OnlineThenEmbedded)) ?? Covers[0];
        SelectedLyrics = LyricsModes.FirstOrDefault(l => l.Value == SettingsStore.Parse(settings.Lyrics, LyricsMode.Sidecar)) ?? LyricsModes[0];

        Reencode = settings.Reencode;
        Bitrate = settings.Bitrate is >= 96 and <= 512 ? settings.Bitrate : 320;
        KeepTemporaryFiles = settings.KeepTemporaryFiles;

        HardwareChoice? hardware = HardwareModes.FirstOrDefault(h => h.Value == SettingsStore.Parse(settings.Hardware, HardwareAcceleration.Disabled));
        if (hardware is not null)
        {
            SelectedHardware = hardware;
        }

        if (!string.IsNullOrWhiteSpace(settings.OutputDirectory))
        {
            OutputDirectory = settings.OutputDirectory!;
        }
    }

    /// <summary>Snapshot of the options worth remembering, as a serialisable record.</summary>
    private AppSettings CaptureSettings() => new()
    {
        OutputDirectory = string.IsNullOrWhiteSpace(OutputDirectory) ? null : OutputDirectory,
        FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath) ? null : FfmpegPath,
        OutputFormat = SelectedFormat.Value.ToString(),
        Reencode = Reencode,
        Hardware = SelectedHardware.Value.ToString(),
        Surround = SelectedSurround.Value.ToString(),
        Cover = SelectedCover.Value.ToString(),
        Lyrics = SelectedLyrics.Value.ToString(),
        Bitrate = Bitrate,
        KeepTemporaryFiles = KeepTemporaryFiles,
    };

    // ---------- collections ----------

    public ObservableCollection<TrackItem> Tracks { get; } = [];

    public ObservableCollection<string> Log { get; } = [];

    public IReadOnlyList<FormatChoice> Formats { get; }

    public IReadOnlyList<SurroundChoice> Surrounds { get; }

    public IReadOnlyList<CoverChoice> Covers { get; }

    public IReadOnlyList<DecoderInputChoice> DecoderInputs { get; }

    public IReadOnlyList<LyricsChoice> LyricsModes { get; }

    /// <summary>Encoder families, rebuilt from what the configured ffmpeg actually supports.</summary>
    public ObservableCollection<HardwareChoice> HardwareModes { get; }

    // ---------- commands ----------

    public RelayCommand AddFilesCommand { get; }

    public RelayCommand AddFolderCommand { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand RemoveSelectedCommand { get; }

    public RelayCommand ConvertCommand => _convertCommand;

    public RelayCommand CancelCommand { get; }

    public RelayCommand OpenOutputCommand { get; }

    public RelayCommand CopyLogCommand { get; }

    public RelayCommand DetectFfmpegCommand { get; }

    // ---------- settings ----------

    private FormatChoice _selectedFormat;
    private SurroundChoice _selectedSurround;
    private CoverChoice _selectedCover;
    private DecoderInputChoice _selectedDecoderInput;
    private LyricsChoice _selectedLyrics;
    private HardwareChoice _selectedHardware;
    private bool _reencode;

    public FormatChoice SelectedFormat
    {
        get => _selectedFormat;
        set
        {
            if (SetProperty(ref _selectedFormat, value))
            {
                OnPropertyChanged(nameof(IsMp3Selected));
                OnPropertyChanged(nameof(IsFlacSelected));
                OnPropertyChanged(nameof(IsCompatibleSelected));
                OnPropertyChanged(nameof(ShowBitrate));
            }
        }
    }

    public SurroundChoice SelectedSurround
    {
        get => _selectedSurround;
        set => SetProperty(ref _selectedSurround, value);
    }

    public CoverChoice SelectedCover
    {
        get => _selectedCover;
        set => SetProperty(ref _selectedCover, value);
    }

    public DecoderInputChoice SelectedDecoderInput
    {
        get => _selectedDecoderInput;
        set => SetProperty(ref _selectedDecoderInput, value);
    }

    public LyricsChoice SelectedLyrics
    {
        get => _selectedLyrics;
        set => SetProperty(ref _selectedLyrics, value);
    }

    /// <summary>Master switch: re-encode instead of remuxing. The accelerator choice sits under it.</summary>
    public bool Reencode
    {
        get => _reencode;
        set
        {
            if (SetProperty(ref _reencode, value))
            {
                OnPropertyChanged(nameof(HardwareHint));
            }
        }
    }

    public HardwareChoice SelectedHardware
    {
        get => _selectedHardware;
        set
        {
            if (SetProperty(ref _selectedHardware, value))
            {
                OnPropertyChanged(nameof(HardwareHint));
            }
        }
    }

    /// <summary>Plain-language summary of what the current accelerator selection will do.</summary>
    public string HardwareHint
    {
        get
        {
            if (!Reencode)
            {
                return "未启用重新编码：负载已是目标编码时直接重封装，音频比特不变。";
            }

            HardwareAcceleration family = SelectedHardware.Value;

            if (family == HardwareAcceleration.Disabled)
            {
                return "使用 CPU 编码器（质量基准，任何环境都可用）。";
            }

            if (family == HardwareAcceleration.Auto)
            {
                return "自动：优先硬件/系统编码器；本机没有音频硬件编码器时回退 CPU。";
            }

            if (family == HardwareAcceleration.MediaFoundation)
            {
                return "Windows Media Foundation：ffmpeg 中唯一带音频编码器的硬件族（aac_mf / mp3_mf / ac3_mf）。";
            }

            bool audio = HardwareModes.FirstOrDefault(h => h.Value == family)?.Label.Contains("音频编码器") ?? false;
            return audio
                ? $"{SelectedHardware.Label}：本机可用，且带音频编码器。"
                : $"{SelectedHardware.Label}：本机可用，但只有视频编码器——音频仍由 CPU 编码。";
        }
    }

    public bool IsMp3Selected => SelectedFormat.Value == OutputFormat.Mp3;

    public bool IsFlacSelected => SelectedFormat.Value == OutputFormat.Flac;

    public bool IsCompatibleSelected => SelectedFormat.Value == OutputFormat.Compatible;

    /// <summary>Whether the bitrate slider is meaningful for the current target.</summary>
    public bool ShowBitrate => IsMp3Selected || IsCompatibleSelected;

    public string OutputDirectory
    {
        get => _outputDirectory;
        set => SetProperty(ref _outputDirectory, value);
    }

    public string FfmpegPath
    {
        get => _ffmpegPath;
        set => SetProperty(ref _ffmpegPath, value);
    }

    /// <summary>
    /// Command used for codecs no local decoder handles (AVS3-P3 / Audio Vivid).
    /// Placeholders: {input} {output} {channels} {sampleRate}.
    /// </summary>
    public string ExternalDecoderCommand
    {
        get => _externalDecoderCommand;
        set => SetProperty(ref _externalDecoderCommand, value);
    }

    public int Bitrate
    {
        get => _bitrate;
        set => SetProperty(ref _bitrate, value);
    }

    public int FlacLevel
    {
        get => _flacLevel;
        set => SetProperty(ref _flacLevel, value);
    }

    public bool ExtractElementaryStream
    {
        get => _extractElementaryStream;
        set => SetProperty(ref _extractElementaryStream, value);
    }

    public bool KeepTemporaryFiles
    {
        get => _keepTemporaryFiles;
        set => SetProperty(ref _keepTemporaryFiles, value);
    }

    // ---------- run state ----------

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                RefreshCommands();
            }
        }
    }

    public bool IsIdle => !IsBusy;

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    /// <summary>
    /// Warning shown in the footer when a conversion had to degrade — a codec fallback that cost
    /// channels, or a drop to the compatibility-only encoders.
    /// </summary>
    public string WarningText
    {
        get => _warningText;
        private set
        {
            if (SetProperty(ref _warningText, value))
            {
                OnPropertyChanged(nameof(HasWarning));
            }
        }
    }

    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);

    // ---------- queue management ----------

    public void AddPaths(IEnumerable<string> paths)
    {
        var existing = Tracks.Select(t => t.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<TrackItem>();

        foreach (string path in paths)
        {
            foreach (string file in Expand(path))
            {
                if (!existing.Add(file))
                {
                    continue;
                }

                var item = new TrackItem(file);
                item.Probe();
                added.Add(item);
            }
        }

        foreach (TrackItem item in added)
        {
            Tracks.Add(item);
        }

        if (added.Count > 0)
        {
            AppendLog($"已加入 {added.Count} 个文件");
            StatusText = $"队列中共 {Tracks.Count} 个文件";
        }
        else
        {
            AppendLog("没有新增文件（可能已在队列中，或不是 .ncm）");
        }

        RefreshCommands();
    }

    private static IEnumerable<string> Expand(string path)
    {
        if (File.Exists(path))
        {
            if (path.EndsWith(".ncm", StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.GetFullPath(path);
            }

            yield break;
        }

        if (!Directory.Exists(path))
        {
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(path, "*.ncm", SearchOption.AllDirectories))
        {
            yield return Path.GetFullPath(file);
        }
    }

    private void AddFiles()
    {
        // The picker is created here so the view model stays free of window references.
        var picker = new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "选择 .ncm 文件",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("网易云音乐加密文件")
                {
                    Patterns = ["*.ncm"],
                },
            ],
        };

        if (Avalonia.Application.Current?.ApplicationLifetime
            is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            return;
        }

        IReadOnlyList<Avalonia.Platform.Storage.IStorageFile> files = window.StorageProvider
            .OpenFilePickerAsync(picker).GetAwaiter().GetResult();

        AddPaths(files.Select(f => f.Path.LocalPath));
    }

    private void AddFolder()
    {
        var picker = new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "选择包含 .ncm 的文件夹",
            AllowMultiple = false,
        };

        if (Avalonia.Application.Current?.ApplicationLifetime
            is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            return;
        }

        IReadOnlyList<Avalonia.Platform.Storage.IStorageFolder> folders = window.StorageProvider
            .OpenFolderPickerAsync(picker).GetAwaiter().GetResult();

        AddPaths(folders.Select(f => f.Path.LocalPath));
    }

    private void RemoveSelected()
    {
        foreach (TrackItem item in Tracks.Where(t => t.IsSelected).ToList())
        {
            Tracks.Remove(item);
        }

        RefreshCommands();
    }

    private void ClearAll()
    {
        Tracks.Clear();
        Log.Clear();
        StatusText = "队列已清空";
        Progress = 0;
        ProgressText = string.Empty;
        RefreshCommands();
    }

    private void Cancel()
    {
        _cancellation?.Cancel();
        AppendLog("正在取消…");
    }

    public void TrackSelectionChanged() => RefreshCommands();

    private void RefreshCommands()
    {
        _convertCommand.RaiseCanExecuteChanged();
        _removeCommand.RaiseCanExecuteChanged();
        AddFilesCommand.RaiseCanExecuteChanged();
        AddFolderCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        OpenOutputCommand.RaiseCanExecuteChanged();
    }

    // ---------- conversion ----------

    private ConversionOptions BuildOptions() => new()
    {
        Format = SelectedFormat.Value,
        Surround = SelectedSurround.Value,
        Cover = SelectedCover.Value,
        Lyrics = SelectedLyrics.Value,
        ForceReencode = Reencode,
        Hardware = Reencode ? SelectedHardware.Value : HardwareAcceleration.Disabled,
        BitrateKbps = Bitrate,
        FlacCompressionLevel = FlacLevel,
        OutputDirectory = string.IsNullOrWhiteSpace(OutputDirectory) ? null : OutputDirectory,
        FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath) ? null : FfmpegPath,
        ExternalDecoderCommand = string.IsNullOrWhiteSpace(ExternalDecoderCommand) ? null : ExternalDecoderCommand,
        ExternalDecoderInput = SelectedDecoderInput.Value,
        ExtractElementaryStream = ExtractElementaryStream,
        KeepTemporaryFiles = KeepTemporaryFiles,
        Overwrite = true,
    };

    private async Task ConvertAllAsync()
    {
        if (IsBusy || Tracks.Count == 0)
        {
            return;
        }

        ConversionOptions options = BuildOptions();

        if (options.Format != OutputFormat.LosslessCopy && FfmpegTool.Locate(options.FfmpegPath) is null)
        {
            AppendLog("警告：未找到 ffmpeg，需要转码的文件会失败。请在设置里指定 ffmpeg 路径。");
        }

        IsBusy = true;
        _cancellation = new CancellationTokenSource();
        CancellationToken token = _cancellation.Token;

        int succeeded = 0;
        int failed = 0;
        int index = 0;

        try
        {
            foreach (TrackItem item in Tracks.ToList())
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                index++;
                item.Status = "转换中";
                item.Progress = 0;
                StatusText = $"({index}/{Tracks.Count}) {item.FileName}";
                AppendLog($"=== {item.FileName} ===");

                var progress = new Progress<double>(value => item.Progress = value * 100);

                try
                {
                    ConversionResult result = await _pipeline.ConvertAsync(
                        item.FilePath,
                        options,
                        message => Dispatcher.UIThread.Post(() => AppendLog("  " + message)),
                        progress,
                        token).ConfigureAwait(true);

                    item.Status = "完成";
                    item.Progress = 100;
                    item.Detail = $"{result.Codec} · {result.Plan.Action} → {Path.GetFileName(result.OutputPath)}";
                    AppendLog($"完成 → {result.OutputPath}");
                    succeeded++;

                    // Surface the degradation in the footer, as well as in the log.
                    if (result.HasWarnings)
                    {
                        WarningText = $"{item.FileName}：{string.Join("；", result.Warnings!)}";
                        AppendLog($"警告：{WarningText}");
                    }
                }
                catch (OperationCanceledException)
                {
                    item.Status = "已取消";
                    break;
                }
                catch (ConversionException ex)
                {
                    item.Status = "失败";
                    item.Progress = 0;
                    AppendLog($"失败：{ex.Message}");
                    failed++;
                }
                catch (Exception ex)
                {
                    item.Status = "失败";
                    item.Progress = 0;
                    AppendLog($"失败：{ex.GetType().Name}: {ex.Message}");
                    failed++;
                }

                Progress = (double)index / Tracks.Count * 100;
            }
        }
        finally
        {
            IsBusy = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }

        StatusText = $"完成 {succeeded} 个，失败 {failed} 个";
        ProgressText = StatusText;
        AppendLog(StatusText);
    }

    private void OpenOutputFolder()
    {
        string directory = OutputDirectory;

        if (string.IsNullOrWhiteSpace(directory))
        {
            TrackItem? first = Tracks.FirstOrDefault();
            if (first is null)
            {
                return;
            }

            directory = Path.GetDirectoryName(first.FilePath) ?? string.Empty;
        }

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppendLog($"无法打开目录：{ex.Message}");
        }
    }

    private void CopyLog()
    {
        string text = string.Join(Environment.NewLine, Log);

        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            _ = window.Clipboard?.SetTextAsync(text);
        }

        StatusText = "日志已复制到剪贴板";
    }

    public string HardwareSummary
    {
        get => _hardwareSummary;
        private set => SetProperty(ref _hardwareSummary, value);
    }

    /// <summary>
    /// Rebuilds the accelerator list from what the configured ffmpeg actually provides.
    /// </summary>
    /// <remarks>
    /// The list is detected rather than fixed, so a machine without an NVIDIA card simply does not
    /// offer NVENC, and a build that adds a family this app has never heard of still shows it.
    /// </remarks>
    private void RefreshHardware()
    {
        HardwareAcceleration previous = SelectedHardware.Value;

        HardwareModes.Clear();
        HardwareModes.Add(new HardwareChoice("关闭（CPU 编码，质量基准）", HardwareAcceleration.Disabled));
        HardwareModes.Add(new HardwareChoice("自动（优先硬件/系统编码器，失败回退 CPU）", HardwareAcceleration.Auto));

        FfmpegTool? tool = FfmpegTool.Locate(string.IsNullOrWhiteSpace(FfmpegPath) ? null : FfmpegPath);

        if (tool is null)
        {
            HardwareSummary = "未找到 ffmpeg：无法探测硬件编码器，仅可使用 CPU 编码。";
        }
        else
        {
            List<HardwareFamilyInfo> families = tool.GetHardwareFamilies().Where(f => f.IsAvailable).ToList();

            foreach (HardwareFamilyInfo family in families)
            {
                HardwareModes.Add(new HardwareChoice(HardwareLabel(family), family.Family));
            }

            HardwareSummary = families.Count == 0
                ? "本机 ffmpeg 未提供任何硬件编码器。"
                : "已探测到：" + string.Join("、", families.Select(f => $"{HardwareName(f.Family)}{(f.HasAudioEncoder ? "（含音频编码器）" : "（仅视频）")}"));
        }

        SelectedHardware = HardwareModes.FirstOrDefault(h => h.Value == previous) ?? HardwareModes[0];
    }

    private static string HardwareLabel(HardwareFamilyInfo family)
        => $"{HardwareName(family.Family)}（{(family.HasAudioEncoder ? "含音频编码器" : "仅视频编码器")}）";

    private static string HardwareName(HardwareAcceleration family) => family switch
    {
        HardwareAcceleration.MediaFoundation => "Media Foundation",
        HardwareAcceleration.Nvenc => "NVIDIA NVENC",
        HardwareAcceleration.NvCuda => "NVIDIA CUDA",
        HardwareAcceleration.Qsv => "Intel Quick Sync Video",
        HardwareAcceleration.Amf => "AMD AMF / VCE / UVD",
        HardwareAcceleration.VaApi => "Linux VA-API",
        HardwareAcceleration.VideoToolbox => "Apple VideoToolbox",
        HardwareAcceleration.MediaCodec => "Android MediaCodec",
        HardwareAcceleration.V4L2 => "嵌入式 VPU / V4L2",
        _ => family.ToString(),
    };

    private void DetectFfmpeg() => DetectFfmpeg(silent: false);

    private void DetectFfmpeg(bool silent)
    {
        FfmpegTool? tool = FfmpegTool.Locate(string.IsNullOrWhiteSpace(FfmpegPath) ? null : FfmpegPath);

        if (tool is null)
        {
            if (!silent)
            {
                AppendLog("未找到 ffmpeg。请安装后加入 PATH，或在下方填写可执行文件路径。");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(FfmpegPath))
        {
            FfmpegPath = tool.FfmpegPath;
        }

        if (!silent)
        {
            AppendLog($"已找到 ffmpeg：{tool.FfmpegPath}");
            AppendLog(tool.HasFfprobe ? "同时找到 ffprobe。" : "未找到 ffprobe（不影响转码）。");
        }

        RefreshHardware();

        if (!silent)
        {
            AppendLog(HardwareSummary);
        }
    }

    private void AppendLog(string message)
    {
        string stamped = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Log.Add(stamped);

        // Keep the list bounded so a long batch cannot grow without limit.
        while (Log.Count > 500)
        {
            Log.RemoveAt(0);
        }
    }

    public void Shutdown()
    {
        // Saving here rather than per-property keeps writes off the UI thread's hot path and still
        // captures whatever the user left the window in.
        SettingsStore.Save(CaptureSettings(), AppendLog);
        _http.Dispose();
    }
}
