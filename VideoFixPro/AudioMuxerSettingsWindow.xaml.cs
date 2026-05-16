using Microsoft.Win32;
using System.Windows;
using VideoFixPro.Models;

namespace VideoFixPro;

public partial class AudioMuxerSettingsWindow : Window
{
    public AudioMuxerSettings Settings { get; private set; }

    public AudioMuxerSettingsWindow(AudioMuxerSettings settings)
    {
        InitializeComponent();
        Settings = settings;
        FfmpegPathBox.Text = settings.FfmpegPath;
        FfprobePathBox.Text = settings.FfprobePath;
        DefaultCrfBox.Text = settings.DefaultCrf.ToString();
    }

    private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Locate ffmpeg executable",
            Filter = "Executable|*.exe|All files|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            FfmpegPathBox.Text = dlg.FileName;
        }
    }

    private void BrowseFfprobe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Locate ffprobe executable",
            Filter = "Executable|*.exe|All files|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            FfprobePathBox.Text = dlg.FileName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DefaultCrfBox.Text.Trim(), out var crf) || crf is < 0 or > 51)
        {
            MessageBox.Show(this, "Enter a valid CRF value between 0 and 51.", "Invalid CRF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Settings = new AudioMuxerSettings
        {
            FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPathBox.Text) ? "ffmpeg" : FfmpegPathBox.Text.Trim(),
            FfprobePath = string.IsNullOrWhiteSpace(FfprobePathBox.Text) ? "ffprobe" : FfprobePathBox.Text.Trim(),
            DefaultCrf = crf
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
