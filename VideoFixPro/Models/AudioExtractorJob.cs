using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace VideoFixPro.Models;

public class AudioExtractorJob : INotifyPropertyChanged
{
    private double _progress;
    private JobStatus _status = JobStatus.Waiting;
    private string _statusText = "Waiting";
    private string _outputPath = string.Empty;

    public string InputPath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(InputPath);
    
    // Extracted streams list (if ffprobe was run). Might be multiple if "Extract all tracks" is selected.
    public List<AudioStreamInfo> Streams { get; set; } = new();

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

    public string StatusColor => Status switch
    {
        JobStatus.Done => "#3FB950",
        JobStatus.Failed => "#F85149",
        JobStatus.Running => "#388BFD",
        JobStatus.Cancelled => "#D29922",
        _ => "#8B949E"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class AudioStreamInfo
{
    public int Index { get; set; } // The stream index e.g. 0:a:1 -> index 1 in audio streams, or global stream index
    public string Codec { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Bitrate { get; set; } = string.Empty;
    public int Channels { get; set; }
    
    // For mapping in ffmpeg (e.g., "-map 0:1")
    public int GlobalIndex { get; set; }

    public string DisplayName => $"Track {Index} - {Language} ({Codec}) {(string.IsNullOrEmpty(Title) ? "" : "- " + Title)}";
}
