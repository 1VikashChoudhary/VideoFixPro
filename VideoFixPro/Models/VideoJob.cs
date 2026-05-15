using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace VideoFixPro.Models;

public enum JobStatus
{
    Waiting,
    Running,
    Done,
    Failed,
    Cancelled
}

public enum RepairMode
{
    Auto,
    StreamCopy,
    ReEncode,
    DeepRecover
}

/// <summary>
/// Represents one video file in the repair queue.
/// </summary>
public class VideoJob : INotifyPropertyChanged
{
    private const string UnknownValue = "-";

    private double _progress;
    private JobStatus _status = JobStatus.Waiting;
    private string _statusText = "Waiting";
    private string _outputPath = string.Empty;
    private string _thumbnailPath = string.Empty;
    private RepairMode _preferredMode = RepairMode.Auto;
    private string _eta = string.Empty;

    public string FilePath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(FilePath);
    public string FileSize { get; set; } = string.Empty;

    public string Duration { get; set; } = UnknownValue;
    public string VideoCodec { get; set; } = UnknownValue;
    public string AudioCodec { get; set; } = UnknownValue;
    public string Resolution { get; set; } = UnknownValue;
    public string Bitrate { get; set; } = UnknownValue;
    public string FrameRate { get; set; } = UnknownValue;

    public double DurationSeconds { get; set; }

    public double TrimStart { get; set; }
    public double TrimEnd { get; set; }
    public bool HasTrim => TrimEnd > TrimStart && TrimEnd > 0;

    public double Progress
    {
        get => _progress;
        set
        {
            _progress = value;
            OnPropertyChanged();
        }
    }

    public JobStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(StatusIcon));
        }
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public string OutputPath
    {
        get => _outputPath;
        set
        {
            _outputPath = value;
            OnPropertyChanged();
        }
    }

    public string ThumbnailPath
    {
        get => _thumbnailPath;
        set
        {
            _thumbnailPath = value;
            OnPropertyChanged();
        }
    }

    public RepairMode PreferredMode
    {
        get => _preferredMode;
        set
        {
            _preferredMode = value;
            OnPropertyChanged();
        }
    }

    public string ETA
    {
        get => _eta;
        set
        {
            _eta = value;
            OnPropertyChanged();
        }
    }

    public string StatusColor => Status switch
    {
        JobStatus.Done => "#3FB950",
        JobStatus.Failed => "#F85149",
        JobStatus.Running => "#388BFD",
        JobStatus.Cancelled => "#D29922",
        _ => "#8B949E"
    };

    public string StatusIcon => Status switch
    {
        JobStatus.Done => "+",
        JobStatus.Failed => "x",
        JobStatus.Running => ">",
        JobStatus.Cancelled => "!",
        _ => "."
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
