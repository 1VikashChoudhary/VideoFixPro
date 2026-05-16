using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VideoFixPro.Models;

public class BatchMuxWizardRow : INotifyPropertyChanged
{
    private string _videoPath = string.Empty;
    private string _audioPath = string.Empty;
    private string _title = string.Empty;
    private string _language = string.Empty;
    private double _delaySeconds;
    private string _outputPath = string.Empty;
    private string _statusText = "Choose audio";

    public string VideoPath
    {
        get => _videoPath;
        set => SetField(ref _videoPath, value);
    }

    public string AudioPath
    {
        get => _audioPath;
        set => SetField(ref _audioPath, value);
    }

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string Language
    {
        get => _language;
        set => SetField(ref _language, value);
    }

    public double DelaySeconds
    {
        get => _delaySeconds;
        set => SetField(ref _delaySeconds, value);
    }

    public string OutputPath
    {
        get => _outputPath;
        set => SetField(ref _outputPath, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public bool IsReady => !string.IsNullOrWhiteSpace(AudioPath) && File.Exists(AudioPath);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(AudioPath))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReady)));
        }
    }
}
