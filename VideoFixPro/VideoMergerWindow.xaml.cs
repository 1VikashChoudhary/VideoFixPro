using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using WinForms = System.Windows.Forms;

namespace VideoFixPro;

public class MergerClipItem
{
    public int Index { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(FilePath);
    public double Duration { get; set; }
    public string DurationText => TimeSpan.FromSeconds(Duration).ToString(@"hh\:mm\:ss");
    public int Width { get; set; }
    public int Height { get; set; }
    public string ResolutionText => Width > 0 && Height > 0 ? $"{Width}x{Height}" : "-";
    public string VideoCodec { get; set; } = "-";
    public string AudioCodec { get; set; } = "-";
    public string CodecText => VideoCodec;
    public long SizeBytes { get; set; }
    public string SizeText => $"{SizeBytes / (1024.0 * 1024.0):F1} MB";
}

public partial class VideoMergerWindow : Window
{
    private readonly ObservableCollection<MergerClipItem> _clips = new();

    // Player state
    private readonly System.Windows.Threading.DispatcherTimer _playheadTimer = new();
    private bool _isPlayerPlaying;
    private bool _isSeeking;
    private double _currentPreviewDuration;

    // Rendering & Cancellation
    private CancellationTokenSource? _cts;
    private Process? _ffmpegProcess;
    private bool _isRendering;
    private string _customOutputFolder = string.Empty;
    private string _lastOutputFolder = string.Empty;

    // GPU support
    private bool _hasNvidia;
    private bool _hasAmd;
    private bool _hasIntel;

    private static string AppDir => AppDomain.CurrentDomain.BaseDirectory;
    private static string FFmpeg => GetBinPath("ffmpeg.exe");
    private static string FFprobe => GetBinPath("ffprobe.exe");

    private static string GetBinPath(string name)
    {
        var appBin = Path.Combine(AppDir, "ffmpeg", name);
        if (File.Exists(appBin)) return appBin;
        var localBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoFixPro", "ffmpeg", name);
        return File.Exists(localBin) ? localBin : appBin;
    }

    private static readonly HashSet<string> VideoExts =
        new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".m2ts" };

    public VideoMergerWindow(string? preloadPath = null, bool hasNvidia = false, bool hasAmd = false, bool hasIntel = false)
    {
        InitializeComponent();
        Loaded += (_, _) => UiTextSanitizer.Apply(this);
        _hasNvidia = hasNvidia;
        _hasAmd = hasAmd;
        _hasIntel = hasIntel;

        PlaylistGrid.ItemsSource = _clips;

        _playheadTimer.Interval = TimeSpan.FromMilliseconds(50);
        _playheadTimer.Tick += (_, _) => { if (!_isSeeking) UpdateSeekFromPlayer(); };

        if (!string.IsNullOrEmpty(preloadPath) && File.Exists(preloadPath))
            _ = AddFilesAsync(new[] { preloadPath });
    }

    // ── Window Chrome ─────────────────────────────────────────────────────────
    private void TitleBar_MouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            if (e.ClickCount == 2) MaxBtn_Click(s, e);
            else DragMove();
        }
    }
    private void MinBtn_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaxBtn_Click(object s, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        if (MaxBtn != null) MaxBtn.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }
    private void CloseBtn_Click(object s, RoutedEventArgs e)
    {
        _playheadTimer.Stop();
        try { Player?.Stop(); } catch { }
        Close();
    }

    // ── Drag & Drop ───────────────────────────────────────────────────────────
    private void Window_DragEnter(object s, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
    }
    private void Window_DragOver(object s, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
    }
    private void Window_Drop(object s, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0)
                _ = AddFilesAsync(files);
        }
    }

    // ── Playlist Management ───────────────────────────────────────────────────
    private void AddFiles_Click(object s, RoutedEventArgs e)
    {
        var dlg = new WinForms.OpenFileDialog
        {
            Title = "Select Video Clips to Merge",
            Filter = "Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.ts|All Files|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK && dlg.FileNames.Length > 0)
            _ = AddFilesAsync(dlg.FileNames);
    }

    private void AddFolder_Click(object s, RoutedEventArgs e)
    {
        var dlg = new WinForms.FolderBrowserDialog
        {
            Description = "Select Folder with Video Clips",
            UseDescriptionForTitle = true
        };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK && Directory.Exists(dlg.SelectedPath))
        {
            var files = Directory.GetFiles(dlg.SelectedPath)
                .Where(f => VideoExts.Contains(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (files.Length > 0)
                _ = AddFilesAsync(files);
        }
    }

    private async Task AddFilesAsync(string[] paths)
    {
        SetStatus("Analyzing added clips...", "#388BFD");
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var dirFiles = Directory.GetFiles(path).Where(f => VideoExts.Contains(Path.GetExtension(f))).ToArray();
                await AddFilesAsync(dirFiles);
                continue;
            }

            if (!File.Exists(path) || !VideoExts.Contains(Path.GetExtension(path))) continue;
            if (_clips.Any(c => c.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;

            var fi = new FileInfo(path);
            var item = new MergerClipItem
            {
                Index = _clips.Count + 1,
                FilePath = path,
                SizeBytes = fi.Length
            };

            await ProbeClipAsync(item);
            _clips.Add(item);
        }

        RenumberClips();
        UpdatePlaylistStats();
        CheckMergeStrategy();

        if (PlaylistGrid.SelectedItem == null && _clips.Count > 0)
            PlaylistGrid.SelectedIndex = 0;

        SetStatus($"Ready — {_clips.Count} clips loaded", "#3FB950");
    }

    private async Task ProbeClipAsync(MergerClipItem item)
    {
        if (!File.Exists(FFprobe)) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FFprobe,
                Arguments = $"-v error -show_entries format=duration:stream=width,height,codec_name,codec_type -of default=noprint_wrappers=1 \"{item.FilePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;
            string output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split('=');
                if (parts.Length != 2) continue;
                string k = parts[0].Trim(), v = parts[1].Trim();

                if (k == "duration" && double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                    item.Duration = d;
                else if (k == "width" && int.TryParse(v, out int w))
                    item.Width = w;
                else if (k == "height" && int.TryParse(v, out int h))
                    item.Height = h;
                else if (k == "codec_name")
                {
                    if (item.VideoCodec == "-") item.VideoCodec = v.ToUpperInvariant();
                    else if (item.AudioCodec == "-") item.AudioCodec = v.ToUpperInvariant();
                }
            }
        }
        catch { }
    }

    private void MoveUp_Click(object s, RoutedEventArgs e)
    {
        int idx = PlaylistGrid.SelectedIndex;
        if (idx > 0)
        {
            _clips.Move(idx, idx - 1);
            RenumberClips();
            PlaylistGrid.SelectedIndex = idx - 1;
            CheckMergeStrategy();
        }
    }

    private void MoveDown_Click(object s, RoutedEventArgs e)
    {
        int idx = PlaylistGrid.SelectedIndex;
        if (idx >= 0 && idx < _clips.Count - 1)
        {
            _clips.Move(idx, idx + 1);
            RenumberClips();
            PlaylistGrid.SelectedIndex = idx + 1;
            CheckMergeStrategy();
        }
    }

    private void RemoveSelected_Click(object s, RoutedEventArgs e)
    {
        int idx = PlaylistGrid.SelectedIndex;
        if (idx >= 0 && idx < _clips.Count)
        {
            _clips.RemoveAt(idx);
            RenumberClips();
            UpdatePlaylistStats();
            CheckMergeStrategy();
            if (_clips.Count > 0)
                PlaylistGrid.SelectedIndex = Math.Clamp(idx, 0, _clips.Count - 1);
            else
                UnloadPreview();
        }
    }

    private void ClearClips_Click(object s, RoutedEventArgs e)
    {
        _clips.Clear();
        UnloadPreview();
        UpdatePlaylistStats();
        CheckMergeStrategy();
    }

    private void RenumberClips()
    {
        for (int i = 0; i < _clips.Count; i++)
            _clips[i].Index = i + 1;
        PlaylistGrid.Items.Refresh();
    }

    private void UpdatePlaylistStats()
    {
        double totalSec = _clips.Sum(c => c.Duration);
        if (TitleClipCount != null) TitleClipCount.Text = $"{_clips.Count} clips in playlist";
        if (TotalDurationBadge != null) TotalDurationBadge.Text = $"Total: {TimeSpan.FromSeconds(totalSec):hh\\:mm\\:ss}";
    }

    private void CheckMergeStrategy()
    {
        if (_clips.Count < 2)
        {
            if (MergeStrategyBadge != null)
            {
                MergeStrategyBadge.Text = "Add 2 or more clips to merge";
                MergeStrategyBadge.Foreground = (Brush)FindResource("MutedBrush");
            }
            if (MergeStrategyHint != null)
                MergeStrategyHint.Text = "Arrange clips in your preferred sequence order.";
            return;
        }

        bool forceReencode = ForceReencodeCheck?.IsChecked == true;
        var first = _clips[0];
        bool allMatch = _clips.All(c => c.VideoCodec == first.VideoCodec &&
                                       c.Width == first.Width &&
                                       c.Height == first.Height &&
                                       Path.GetExtension(c.FilePath).Equals(Path.GetExtension(first.FilePath), StringComparison.OrdinalIgnoreCase));

        if (allMatch && !forceReencode)
        {
            if (MergeStrategyBadge != null)
            {
                MergeStrategyBadge.Text = "⚡ Fast Lossless Concat (0 Re-encoding)";
                MergeStrategyBadge.Foreground = (Brush)FindResource("SuccessBrush");
            }
            if (MergeStrategyHint != null)
                MergeStrategyHint.Text = "All clips share identical formats & resolutions. Merging will take ~2 seconds with zero quality loss.";
        }
        else
        {
            if (MergeStrategyBadge != null)
            {
                MergeStrategyBadge.Text = "⚙️ Smart Normalization (Auto Re-encode)";
                MergeStrategyBadge.Foreground = (Brush)FindResource("AccentBrush");
            }
            if (MergeStrategyHint != null)
                MergeStrategyHint.Text = $"Clips have different resolutions/codecs. Will normalize and blend into {first.ResolutionText} ({first.VideoCodec}).";
        }
    }

    private void ForceReencode_Changed(object s, RoutedEventArgs e) => CheckMergeStrategy();

    // ── Preview Player ────────────────────────────────────────────────────────
    private void PlaylistGrid_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (PlaylistGrid.SelectedItem is MergerClipItem item && File.Exists(item.FilePath))
        {
            if (PreviewClipTitle != null) PreviewClipTitle.Text = item.FileName;
            if (PreviewClipInfo != null) PreviewClipInfo.Text = $"{item.ResolutionText} • {item.DurationText} • {item.SizeText} • {item.VideoCodec}";

            _currentPreviewDuration = item.Duration;
            try
            {
                Player.Source = new Uri(item.FilePath);
                Player.Play();
                Player.Pause();
                _isPlayerPlaying = false;
                if (SeekPlayBtn != null) SeekPlayBtn.Content = "▶";
                if (PlayPauseBtn != null) PlayPauseBtn.Content = "▶";
            }
            catch { }
        }
    }

    private void UnloadPreview()
    {
        _playheadTimer.Stop();
        try { Player?.Stop(); } catch { }
        if (Player != null) Player.Source = null;
        if (PreviewClipTitle != null) PreviewClipTitle.Text = "Select a clip from playlist to preview";
        if (PreviewClipInfo != null) PreviewClipInfo.Text = "No video selected";
    }

    private void Player_MediaOpened(object s, RoutedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan && _currentPreviewDuration <= 0)
            _currentPreviewDuration = Player.NaturalDuration.TimeSpan.TotalSeconds;
    }
    private void Player_MediaEnded(object s, RoutedEventArgs e)
    {
        Player.Position = TimeSpan.Zero;
        _isPlayerPlaying = false;
        if (SeekPlayBtn != null) SeekPlayBtn.Content = "▶";
        if (PlayPauseBtn != null) PlayPauseBtn.Content = "▶";
        _playheadTimer.Stop();
    }
    private void TogglePlay_Click(object s, RoutedEventArgs e)
    {
        if (_isPlayerPlaying)
        {
            Player.Pause();
            _isPlayerPlaying = false;
            _playheadTimer.Stop();
            if (SeekPlayBtn != null) SeekPlayBtn.Content = "▶";
            if (PlayPauseBtn != null) PlayPauseBtn.Content = "▶";
        }
        else
        {
            Player.Play();
            _isPlayerPlaying = true;
            _playheadTimer.Start();
            if (SeekPlayBtn != null) SeekPlayBtn.Content = "⏸";
            if (PlayPauseBtn != null) PlayPauseBtn.Content = "⏸";
        }
    }
    private void SeekSlider_MouseDown(object s, MouseButtonEventArgs e) => _isSeeking = true;
    private void SeekSlider_MouseUp(object s, MouseButtonEventArgs e)
    {
        _isSeeking = false;
        if (_currentPreviewDuration > 0)
        {
            double pos = (SeekSlider.Value / 100.0) * _currentPreviewDuration;
            Player.Position = TimeSpan.FromSeconds(pos);
        }
    }
    private void SeekSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSeeking && _currentPreviewDuration > 0)
        {
            double pos = (SeekSlider.Value / 100.0) * _currentPreviewDuration;
            Player.Position = TimeSpan.FromSeconds(pos);
            UpdateSeekTimeDisplay(pos);
        }
    }
    private void UpdateSeekFromPlayer()
    {
        if (_currentPreviewDuration <= 0) return;
        double cur = Player.Position.TotalSeconds;
        SeekSlider.Value = Math.Clamp((cur / _currentPreviewDuration) * 100.0, 0, 100);
        UpdateSeekTimeDisplay(cur);
    }
    private void UpdateSeekTimeDisplay(double curSec)
    {
        if (SeekTimeText != null)
        {
            var cur = TimeSpan.FromSeconds(curSec);
            var tot = TimeSpan.FromSeconds(_currentPreviewDuration);
            SeekTimeText.Text = $"{cur:mm\\:ss} / {tot:mm\\:ss}";
        }
    }

    // ── Output Management ─────────────────────────────────────────────────────
    private void BrowseOutput_Click(object s, RoutedEventArgs e)
    {
        var dlg = new WinForms.FolderBrowserDialog
        {
            Description = "Select Output Folder for Merged Video",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
        {
            _customOutputFolder = dlg.SelectedPath;
            if (OutputPathText != null)
            {
                OutputPathText.Text = _customOutputFolder;
                OutputPathText.Foreground = (Brush)FindResource("TextBrush");
            }
        }
    }
    private void ResetOutput_Click(object s, RoutedEventArgs e)
    {
        _customOutputFolder = string.Empty;
        if (OutputPathText != null)
        {
            OutputPathText.Text = "Same as first clip";
            OutputPathText.Foreground = (Brush)FindResource("MutedBrush");
        }
    }
    private void OpenFolder_Click(object s, RoutedEventArgs e)
    {
        string dir = !string.IsNullOrEmpty(_lastOutputFolder) ? _lastOutputFolder :
                     !string.IsNullOrEmpty(_customOutputFolder) ? _customOutputFolder :
                     _clips.Count > 0 ? Path.GetDirectoryName(_clips[0].FilePath) ?? "" : "";
        if (Directory.Exists(dir))
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    // ── Merge Execution ───────────────────────────────────────────────────────
    private async void Merge_Click(object s, RoutedEventArgs e)
    {
        if (_isRendering) return;
        if (_clips.Count < 2)
        {
            MessageBox.Show("Please add at least 2 video clips to merge.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ExecuteMergeAsync();
    }

    private void Cancel_Click(object s, RoutedEventArgs e)
    {
        _cts?.Cancel();
        try { _ffmpegProcess?.Kill(); } catch { }
    }

    private async Task ExecuteMergeAsync()
    {
        _isRendering = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        string firstPath = _clips[0].FilePath;
        string dir = !string.IsNullOrEmpty(_customOutputFolder) ? _customOutputFolder : Path.GetDirectoryName(firstPath) ?? ".";
        string ext = Path.GetExtension(firstPath);
        string outputPath = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(firstPath)}_merged{ext}");
        outputPath = GetUniqueFilePath(outputPath);
        _lastOutputFolder = dir;

        SetRenderingUI(true);
        SetStatus($"Merging {_clips.Count} clips...", "#388BFD");

        double totalDuration = _clips.Sum(c => c.Duration);
        var first = _clips[0];
        bool forceReencode = ForceReencodeCheck?.IsChecked == true;
        bool allMatch = _clips.All(c => c.VideoCodec == first.VideoCodec &&
                                       c.Width == first.Width &&
                                       c.Height == first.Height &&
                                       Path.GetExtension(c.FilePath).Equals(ext, StringComparison.OrdinalIgnoreCase));

        bool success = false;
        string tempConcatFile = Path.Combine(Path.GetTempPath(), $"vfp_concat_{Guid.NewGuid():N}.txt");

        try
        {
            if (allMatch && !forceReencode)
            {
                // ── Fast Lossless Concat Demuxer Mode ──
                var sb = new StringBuilder();
                foreach (var clip in _clips)
                {
                    string safePath = clip.FilePath.Replace("'", "'\\''");
                    sb.AppendLine($"file '{safePath}'");
                }
                await File.WriteAllTextAsync(tempConcatFile, sb.ToString(), Encoding.UTF8);

                string args = $"-y -f concat -safe 0 -i \"{tempConcatFile}\" -c copy \"{outputPath}\"";
                success = await RunFFmpegAsync(args, totalDuration, _cts.Token);
            }
            else
            {
                // ── Smart Re-encode & Scale Filter Mode ──
                int masterW = first.Width > 0 ? first.Width : 1920;
                int masterH = first.Height > 0 ? first.Height : 1080;
                masterW = (masterW / 2) * 2;
                masterH = (masterH / 2) * 2;

                var sbInputs = new StringBuilder();
                var sbFilters = new StringBuilder();
                var sbConcatMap = new StringBuilder();

                bool anyAudio = _clips.Any(c => c.AudioCodec != "-");

                for (int i = 0; i < _clips.Count; i++)
                {
                    sbInputs.Append($"-i \"{_clips[i].FilePath}\" ");
                    sbFilters.Append($"[{i}:v]scale={masterW}:{masterH}:force_original_aspect_ratio=decrease,pad={masterW}:{masterH}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30[v{i}]; ");

                    if (anyAudio)
                    {
                        if (_clips[i].AudioCodec != "-")
                            sbFilters.Append($"[{i}:a]aformat=sample_fmts=fltp:sample_rates=44100:channel_layouts=stereo[a{i}]; ");
                        else
                            sbFilters.Append($"aevalsrc=0:d={_clips[i].Duration.ToString("F3", CultureInfo.InvariantCulture)}:s=44100:c=stereo[a{i}]; ");

                        sbConcatMap.Append($"[v{i}][a{i}]");
                    }
                    else
                    {
                        sbConcatMap.Append($"[v{i}]");
                    }
                }

                if (anyAudio)
                    sbFilters.Append($"{sbConcatMap}concat=n={_clips.Count}:v=1:a=1[outv][outa]");
                else
                    sbFilters.Append($"{sbConcatMap}concat=n={_clips.Count}:v=1:a=0[outv]");

                bool useGpu = _hasNvidia || _hasAmd || _hasIntel;
                string vCodecArgs = useGpu && _hasAmd ? "-c:v h264_amf -pix_fmt yuv420p" :
                                    useGpu && _hasNvidia ? "-c:v h264_nvenc -pix_fmt yuv420p" :
                                    useGpu && _hasIntel ? "-c:v h264_qsv -pix_fmt nv12" :
                                    "-c:v libx264 -preset fast -crf 20 -pix_fmt yuv420p";

                string audioMapArgs = anyAudio ? "-map \"[outa]\" -c:a aac -b:a 192k " : "";
                string args = $"-y {sbInputs}-filter_complex \"{sbFilters}\" -map \"[outv]\" {audioMapArgs}{vCodecArgs} \"{outputPath}\"";
                Log($"[CMD] ffmpeg {args}");
                success = await RunFFmpegAsync(args, totalDuration, _cts.Token);

                // Auto CPU Fallback if GPU fails
                if (!success && !_cts.Token.IsCancellationRequested && useGpu)
                {
                    Log("[WARN] GPU merger encoding failed. Retrying on CPU (libx264)...");
                    SetStatus("Retrying on CPU...", "#D29922");
                    args = $"-y {sbInputs}-filter_complex \"{sbFilters}\" -map \"[outv]\" {audioMapArgs}-c:v libx264 -preset fast -crf 20 -pix_fmt yuv420p \"{outputPath}\"";
                    Log($"[CMD Fallback] ffmpeg {args}");
                    success = await RunFFmpegAsync(args, totalDuration, _cts.Token);
                }
            }
        }
        finally
        {
            try { if (File.Exists(tempConcatFile)) File.Delete(tempConcatFile); } catch { }
        }

        _isRendering = false;
        SetRenderingUI(false);

        if (_cts.Token.IsCancellationRequested)
        {
            SetStatus("Cancelled", "#D29922");
            try { File.Delete(outputPath); } catch { }
        }
        else if (success && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
        {
            var finalFi = new FileInfo(outputPath);
            double finalMb = finalFi.Length / (1024.0 * 1024.0);
            SetRenderProgress(100);
            SetStatus($"Done! Output: {finalMb:F1} MB", "#3FB950");
            ShowNotification("Video Merger Complete", $"Merged {_clips.Count} clips into: {Path.GetFileName(outputPath)}");
        }
        else
        {
            SetStatus("Merge failed", "#F85149");
        }
    }

    private async Task<bool> RunFFmpegAsync(string args, double totalDuration, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FFmpeg,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false
        };

        var tcs = new TaskCompletionSource<bool>();
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _ffmpegProcess = proc;

        var timeRegex = new Regex(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

        proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            string line = e.Data;

            var match = timeRegex.Match(line);
            if (match.Success && totalDuration > 0)
            {
                if (double.TryParse(match.Groups[1].Value, out double h) &&
                    double.TryParse(match.Groups[2].Value, out double m) &&
                    double.TryParse(match.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double s))
                {
                    double curSec = h * 3600 + m * 60 + s;
                    double percent = Math.Clamp((curSec / totalDuration) * 100.0, 0, 99.5);
                    Dispatcher.Invoke(() => SetRenderProgress(percent));
                }
            }
        };

        proc.Exited += (_, _) =>
        {
            ProcessGuard.Unwatch(proc);
            tcs.TrySetResult(proc.ExitCode == 0);
            proc.Dispose();
        };

        try
        {
            proc.Start();
            ProcessGuard.Watch(proc);
            proc.BeginErrorReadLine();
            using (token.Register(() => { try { proc.Kill(); } catch { } }))
            {
                return await tcs.Task;
            }
        }
        catch { return false; }
    }

    private void SetRenderingUI(bool rendering)
    {
        if (RenderProgressPanel != null) RenderProgressPanel.Visibility = rendering ? Visibility.Visible : Visibility.Collapsed;
        if (CancelBtn != null) CancelBtn.Visibility = rendering ? Visibility.Visible : Visibility.Collapsed;
        if (MergeBtn != null) MergeBtn.IsEnabled = !rendering;
        if (OpenFolderBtn != null) OpenFolderBtn.IsEnabled = !rendering && !string.IsNullOrEmpty(_lastOutputFolder);

        if (TaskbarProgress != null)
            TaskbarProgress.ProgressState = rendering ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.None;

        if (rendering) SetRenderProgress(0);
    }

    private void SetRenderProgress(double percent)
    {
        if (RenderProgressBar != null) RenderProgressBar.Value = percent;
        if (RenderProgressText != null) RenderProgressText.Text = $"{(int)percent}%";
        if (TaskbarProgress != null) TaskbarProgress.ProgressValue = percent / 100.0;
    }

    private void SetStatus(string text, string hexColor)
    {
        if (StatusText != null) StatusText.Text = text;
        if (StatusDot != null)
        {
            try { StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString(hexColor)!; }
            catch { }
        }
    }

    private void Log(string text)
    {
        Debug.WriteLine(text);
    }

    private static string GetUniqueFilePath(string dest)
    {
        if (!File.Exists(dest)) return dest;
        string dir = Path.GetDirectoryName(dest) ?? ".";
        string name = Path.GetFileNameWithoutExtension(dest);
        string ext = Path.GetExtension(dest);
        int i = 1;
        while (File.Exists(Path.Combine(dir, $"{name}_{i}{ext}"))) i++;
        return Path.Combine(dir, $"{name}_{i}{ext}");
    }

    private void ShowNotification(string title, string message)
    {
        try
        {
            var notif = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Information,
                Visible = true,
                BalloonTipTitle = title,
                BalloonTipText = message
            };
            notif.ShowBalloonTip(3000);
            Task.Delay(4000).ContinueWith(_ => notif.Dispose());
        }
        catch { }
    }
}
