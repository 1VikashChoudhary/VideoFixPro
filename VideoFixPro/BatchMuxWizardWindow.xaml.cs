using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VideoFixPro.Models;
using WinForms = System.Windows.Forms;

namespace VideoFixPro;

public partial class BatchMuxWizardWindow : Window
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".flv", ".ts", ".m2ts", ".wmv", ".webm", ".m4v"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac", ".m4a", ".mp3", ".wav", ".ogg", ".ac3", ".flac", ".opus", ".wma", ".mka"
    };

    private readonly string _outputExtension;

    public ObservableCollection<BatchMuxWizardRow> Rows { get; } = new();

    public IReadOnlyList<BatchMuxWizardRow> ReadyRows =>
        Rows.Where(r => r.IsReady)
            .Select(CloneRow)
            .ToList();

    public BatchMuxWizardWindow(string outputExtension)
    {
        InitializeComponent();
        Loaded += (_, _) => UiTextSanitizer.Apply(this);
        DataContext = this;
        _outputExtension = outputExtension.StartsWith('.') ? outputExtension : "." + outputExtension;
        UpdateSummary();
    }

    private BatchMuxWizardRow? SelectedRow => BatchGrid.SelectedItem as BatchMuxWizardRow;

    private void AddVideos_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select videos for batch audio muxing",
            Multiselect = true,
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.flv;*.ts;*.m2ts;*.wmv;*.webm;*.m4v|All files|*.*"
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        AddVideos(dlg.FileNames);
    }

    private void AddVideos(IEnumerable<string> files)
    {
        var added = 0;
        foreach (var file in files.Where(File.Exists))
        {
            if (!VideoExtensions.Contains(Path.GetExtension(file)))
            {
                continue;
            }

            if (Rows.Any(r => string.Equals(r.VideoPath, file, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var row = new BatchMuxWizardRow
            {
                VideoPath = file,
                OutputPath = Path.Combine(Path.GetDirectoryName(file) ?? string.Empty, Path.GetFileNameWithoutExtension(file) + "_multi_audio" + _outputExtension),
                StatusText = "Choose audio"
            };
            row.PropertyChanged += Row_PropertyChanged;
            Rows.Add(row);
            added++;
        }

        if (added > 0)
        {
            BatchGrid.SelectedItem = Rows.Last();
        }

        UpdateSummary();
    }

    private void ChooseAudioForSelected_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row == null)
        {
            MessageBox.Show(this, "Select a video row first.", "No row selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ChooseAudioForRow(row);
    }

    private void ChooseAudioForRow(BatchMuxWizardRow row)
    {
        var dlg = new OpenFileDialog
        {
            Title = $"Choose audio for {Path.GetFileName(row.VideoPath)}",
            Filter = "Audio files|*.aac;*.m4a;*.mp3;*.wav;*.ogg;*.ac3;*.flac;*.opus;*.wma;*.mka|All files|*.*"
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        row.AudioPath = dlg.FileName;
        if (string.IsNullOrWhiteSpace(row.Title))
        {
            row.Title = Path.GetFileNameWithoutExtension(dlg.FileName);
        }

        row.StatusText = "Ready";
        BatchGrid.Items.Refresh();
        UpdateSummary();
    }

    private void AutoMatchFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Rows.Count == 0)
        {
            MessageBox.Show(this, "Add the videos first, then auto-match audio files for them.", "No videos added", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        using var dlg = new WinForms.FolderBrowserDialog
        {
            Description = "Choose the folder that contains the matching audio files"
        };

        if (dlg.ShowDialog() != WinForms.DialogResult.OK || string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            return;
        }

        var audioFiles = Directory.GetFiles(dlg.SelectedPath)
            .Where(path => AudioExtensions.Contains(Path.GetExtension(path)))
            .ToList();

        var exactMap = audioFiles
            .GroupBy(path => NormalizeStem(Path.GetFileNameWithoutExtension(path)))
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var matched = 0;
        foreach (var row in Rows)
        {
            if (row.IsReady)
            {
                continue;
            }

            var normalizedVideo = NormalizeStem(Path.GetFileNameWithoutExtension(row.VideoPath));
            if (!exactMap.TryGetValue(normalizedVideo, out var audio))
            {
                audio = audioFiles.FirstOrDefault(path =>
                {
                    var normalizedAudio = NormalizeStem(Path.GetFileNameWithoutExtension(path));
                    return normalizedAudio.Contains(normalizedVideo, StringComparison.OrdinalIgnoreCase) ||
                           normalizedVideo.Contains(normalizedAudio, StringComparison.OrdinalIgnoreCase);
                });
            }

            if (audio == null)
            {
                row.StatusText = "No match";
                continue;
            }

            row.AudioPath = audio;
            if (string.IsNullOrWhiteSpace(row.Title))
            {
                row.Title = Path.GetFileNameWithoutExtension(audio);
            }
            row.StatusText = "Ready";
            matched++;
        }

        BatchGrid.Items.Refresh();
        UpdateSummary();
        MessageBox.Show(this, $"Matched audio for {matched} video(s).", "Auto-match complete", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ClearSelectedAudio_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row == null)
        {
            MessageBox.Show(this, "Select a video row first.", "No row selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        row.AudioPath = string.Empty;
        row.StatusText = "Choose audio";
        BatchGrid.Items.Refresh();
        UpdateSummary();
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row == null)
        {
            return;
        }

        Rows.Remove(row);
        UpdateSummary();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (Rows.Count == 0)
        {
            return;
        }

        if (MessageBox.Show(this, "Remove all videos from the batch wizard?", "Clear all", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        Rows.Clear();
        UpdateSummary();
    }

    private void BatchGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedRow != null)
        {
            ChooseAudioForRow(SelectedRow);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void QueueReady_Click(object sender, RoutedEventArgs e)
    {
        var readyCount = Rows.Count(r => r.IsReady);
        if (readyCount == 0)
        {
            MessageBox.Show(this, "Choose an audio file for at least one video before queueing jobs.", "Nothing ready", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void UpdateSummary()
    {
        var total = Rows.Count;
        var ready = Rows.Count(r => r.IsReady);
        var missing = total - ready;

        SummaryText.Text = total == 0
            ? "No videos added yet."
            : $"{ready} of {total} video(s) are ready to queue. {missing} still need an audio file.";
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BatchMuxWizardRow.AudioPath) or nameof(BatchMuxWizardRow.StatusText))
        {
            UpdateSummary();
        }
    }

    private static string NormalizeStem(string value)
    {
        return new string(value
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    private static BatchMuxWizardRow CloneRow(BatchMuxWizardRow row)
    {
        return new BatchMuxWizardRow
        {
            VideoPath = row.VideoPath,
            AudioPath = row.AudioPath,
            Title = row.Title,
            Language = row.Language,
            DelaySeconds = row.DelaySeconds,
            OutputPath = row.OutputPath,
            StatusText = row.StatusText
        };
    }
}
