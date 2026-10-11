using NcmForge.Core.Ncm;
using NcmForge.Core.Pipeline;

namespace NcmForge.App.ViewModels;

/// <summary>One queued .ncm file, with the details the UI shows and the per-file progress.</summary>
public sealed class TrackItem : ObservableObject
{
    private string _status = "等待";
    private string _detail = string.Empty;
    private double _progress;
    private bool _isSelected;

    public TrackItem(string path)
    {
        FilePath = path;
        FileName = Path.GetFileName(path);
        FileSize = TryGetSize(path);
    }

    public string FilePath { get; }

    public string FileName { get; }

    public long FileSize { get; }

    public string FileSizeDisplay => FormatSize(FileSize);

    /// <summary>Codec and channel summary, filled in when the container is probed.</summary>
    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Reads the container just far enough to describe it. Failure is recorded rather than thrown so
    /// one bad file cannot stop a batch.
    /// </summary>
    public void Probe()
    {
        try
        {
            using NcmFile ncm = NcmFile.Open(FilePath);
            string channels = ConversionPipeline.ResolveChannelCount(ncm) is { } count and > 0
                ? $"{count} 声道"
                : string.Empty;

            Detail = string.Join(" · ", new[]
            {
                ncm.CodecDisplayName,
                channels,
                ncm.EmbeddedImage is not null ? "内嵌封面" : null,
                ncm.Layout == NcmPayloadLayout.AfterEmbeddedImage ? "沉浸声布局" : null,
            }.Where(part => !string.IsNullOrEmpty(part)));

            Status = "就绪";
        }
        catch (NcmFormatException ex)
        {
            Detail = ex.Message;
            Status = "不是有效的 .ncm";
        }
        catch (Exception ex)
        {
            Detail = $"{ex.GetType().Name}: {ex.Message}";
            Status = "无法读取";
        }
    }

    private static long TryGetSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1 << 30 => $"{bytes / (double)(1 << 30):F2} GB",
        >= 1 << 20 => $"{bytes / (double)(1 << 20):F1} MB",
        >= 1 << 10 => $"{bytes / (double)(1 << 10):F0} KB",
        _ => $"{bytes} B",
    };
}
