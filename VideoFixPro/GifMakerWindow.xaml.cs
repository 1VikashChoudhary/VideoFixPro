using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using WinForms = System.Windows.Forms;

namespace VideoFixPro;

public partial class GifMakerWindow : Window
{
    private string _filePath = string.Empty;
    private double _durationSeconds;
    private int _sourceWidth;
    private int _sourceHeight;
    private double _videoRotation = 0;

    private double _startTimeSeconds = 0.0;
    private double _endTimeSeconds = 5.0;

    // Player state
    private readonly System.Windows.Threading.DispatcherTimer _playheadTimer = new();
    private bool _isPlayerPlaying;
    private bool _isSeeking;
    private bool _isPlayingRangeOnly;

    // Rendering
    private CancellationTokenSource? _cts;
    private Process? _ffmpegProcess;
    private bool _isRendering;
    private string _customOutputFolder = string.Empty;
    private string _lastOutputFolder = string.Empty;

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

    public GifMakerWindow(string? preloadPath = null)
    {
        InitializeComponent();
        Loaded += (_, _) => UiTextSanitizer.Apply(this);

        _playheadTimer.Interval = TimeSpan.FromMilliseconds(40);
        _playheadTimer.Tick += (_, _) => { if (!_isSeeking) UpdateSeekFromPlayer(); };

        if (!string.IsNullOrEmpty(preloadPath) && File.Exists(preloadPath))
            _ = LoadFileAsync(preloadPath);
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
            if (files != null && files.Length > 0 && File.Exists(files[0]))
                _ = LoadFileAsync(files[0]);
        }
    }
    private void DropZone_Click(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) ChangeFile_Click(s, e);
    }
    private void ChangeFile_Click(object s, RoutedEventArgs e)
    {
        var dlg = new WinForms.OpenFileDialog
        {
            Title = "Select Video to Create GIF/WebP",
            Filter = "Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.ts|All Files|*.*"
        };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            _ = LoadFileAsync(dlg.FileName);
    }
    private void RemoveFile_Click(object s, RoutedEventArgs e) => UnloadVideo();

    private void UnloadVideo()
    {
        _playheadTimer.Stop();
        try { Player?.Stop(); } catch { }
        if (Player != null) Player.Source = null;

        _filePath = string.Empty;
        _durationSeconds = 0;

        if (TitleFileName != null) TitleFileName.Text = "No file loaded";
        if (DropZone != null) DropZone.Visibility = Visibility.Visible;
        if (FileHeader != null) FileHeader.Visibility = Visibility.Collapsed;
        if (PlayerBorder != null) PlayerBorder.Visibility = Visibility.Collapsed;
        if (SeekPanel != null) SeekPanel.Visibility = Visibility.Collapsed;
        if (TrimRangePanel != null) TrimRangePanel.Visibility = Visibility.Collapsed;
        SetStatus("Ready", "#8B949E");
    }

    private async Task LoadFileAsync(string path)
    {
        if (!File.Exists(path)) return;
        _filePath = path;

        if (TitleFileName != null) TitleFileName.Text = Path.GetFileName(path);
        if (HeaderFileName != null) HeaderFileName.Text = Path.GetFileName(path);

        if (DropZone != null) DropZone.Visibility = Visibility.Collapsed;
        if (FileHeader != null) FileHeader.Visibility = Visibility.Visible;
        if (PlayerBorder != null) PlayerBorder.Visibility = Visibility.Visible;
        if (SeekPanel != null) SeekPanel.Visibility = Visibility.Visible;
        if (TrimRangePanel != null) TrimRangePanel.Visibility = Visibility.Visible;

        SetStatus($"Loading {Path.GetFileName(path)}...", "#388BFD");

        try
        {
            Player.Source = new Uri(path);
            Player.Play();
            Player.Pause();
            _isPlayerPlaying = false;
            if (SeekPlayBtn != null) SeekPlayBtn.Content = "▶";
            if (PlayPauseBtn != null) PlayPauseBtn.Content = "▶";
        }
        catch { }

        await ProbeVideoAsync(path);

        _startTimeSeconds = 0.0;
        _endTimeSeconds = Math.Min(5.0, _durationSeconds > 0 ? _durationSeconds : 5.0);
        UpdateRangeBoxes();

        if (OpenFolderBtn != null) OpenFolderBtn.IsEnabled = true;
        SetStatus($"Loaded: {Path.GetFileName(path)}", "#3FB950");
    }

    private async Task ProbeVideoAsync(string path)
    {
        if (!File.Exists(FFprobe)) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FFprobe,
                Arguments = $"-v quiet -print_format json -show_streams -show_format \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;
            string output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var root = JsonNode.Parse(output);
            if (root?["format"]?["duration"]?.GetValue<string>() is string durStr &&
                double.TryParse(durStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double dur))
            {
                _durationSeconds = dur;
            }

            var streams = root?["streams"]?.AsArray();
            if (streams != null)
            {
                foreach (var stream in streams)
                {
                    if (stream?["codec_type"]?.GetValue<string>() == "video")
                    {
                        int w = stream?["width"]?.GetValue<int>() ?? 0;
                        int h = stream?["height"]?.GetValue<int>() ?? 0;

                        int rot = 0;
                        var sideRot = stream?["side_data_list"]?.AsArray();
                        if (sideRot != null)
                        {
                            foreach (var sd in sideRot)
                            {
                                if (sd?["rotation"]?.GetValue<int>() is int r) { rot = r; break; }
                            }
                        }
                        if (rot == 0)
                        {
                            var tags = stream?["tags"];
                            if (tags?["rotate"]?.GetValue<string>() is string rotStr && int.TryParse(rotStr, out int tr))
                                rot = tr;
                        }

                        _videoRotation = 0;
                        if (rot != 0)
                        {
                            _videoRotation = ((-rot % 360) + 360) % 360;
                            if (rot > 0 && (sideRot == null || sideRot.Count == 0))
                                _videoRotation = rot % 360;
                        }

                        if (_videoRotation == 90 || _videoRotation == 270)
                        {
                            _sourceWidth = h;
                            _sourceHeight = w;
                        }
                        else
                        {
                            _sourceWidth = w;
                            _sourceHeight = h;
                        }

                        Dispatcher.Invoke(() =>
                        {
                            ApplyPlayerDimensionsAndRotation();
                        });
                        break;
                    }
                }
            }

            Dispatcher.Invoke(() =>
            {
                if (HeaderDuration != null) HeaderDuration.Text = TimeSpan.FromSeconds(_durationSeconds).ToString(@"hh\:mm\:ss");
                if (HeaderResolution != null) HeaderResolution.Text = $"{_sourceWidth}x{_sourceHeight}";
            });
        }
        catch { }
    }

    private void ApplyPlayerDimensionsAndRotation()
    {
        int rawW = (Player?.NaturalVideoWidth > 0) ? Player.NaturalVideoWidth : (_videoRotation == 90 || _videoRotation == 270 ? _sourceHeight : _sourceWidth);
        int rawH = (Player?.NaturalVideoHeight > 0) ? Player.NaturalVideoHeight : (_videoRotation == 90 || _videoRotation == 270 ? _sourceWidth : _sourceHeight);

        if (rawW <= 0) rawW = 1920;
        if (rawH <= 0) rawH = 1080;

        if (Player != null) { Player.Width = rawW; Player.Height = rawH; }

        if (PlayerRotator != null)
        {
            PlayerRotator.Width = rawW;
            PlayerRotator.Height = rawH;
            if (_videoRotation != 0)
                PlayerRotator.LayoutTransform = new RotateTransform(_videoRotation);
            else
                PlayerRotator.LayoutTransform = Transform.Identity;
        }
    }

    // ── Player Controls ───────────────────────────────────────────────────────
    private void Player_MediaOpened(object s, RoutedEventArgs e)
    {
        ApplyPlayerDimensionsAndRotation();
        if (Player.NaturalDuration.HasTimeSpan)
        {
            double sec = Player.NaturalDuration.TimeSpan.TotalSeconds;
            if (sec > 0 && _durationSeconds <= 0)
            {
                _durationSeconds = sec;
                _endTimeSeconds = Math.Min(5.0, _durationSeconds);
                UpdateRangeBoxes();
            }
        }
    }
    private void Player_MediaEnded(object s, RoutedEventArgs e)
    {
        Player.Position = TimeSpan.FromSeconds(_startTimeSeconds);
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
            _isPlayingRangeOnly = false;
            Player.Play();
            _isPlayerPlaying = true;
            _playheadTimer.Start();
            if (SeekPlayBtn != null) SeekPlayBtn.Content = "⏸";
            if (PlayPauseBtn != null) PlayPauseBtn.Content = "⏸";
        }
    }

    private void PlayRange_Click(object s, RoutedEventArgs e)
    {
        Player.Position = TimeSpan.FromSeconds(_startTimeSeconds);
        _isPlayingRangeOnly = true;
        Player.Play();
        _isPlayerPlaying = true;
        _playheadTimer.Start();
        if (SeekPlayBtn != null) SeekPlayBtn.Content = "⏸";
        if (PlayPauseBtn != null) PlayPauseBtn.Content = "⏸";
    }

    private void SeekSlider_MouseDown(object s, MouseButtonEventArgs e) => _isSeeking = true;
    private void SeekSlider_MouseUp(object s, MouseButtonEventArgs e)
    {
        _isSeeking = false;
        if (_durationSeconds > 0)
        {
            double pos = (SeekSlider.Value / 100.0) * _durationSeconds;
            Player.Position = TimeSpan.FromSeconds(pos);
        }
    }
    private void SeekSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSeeking && _durationSeconds > 0)
        {
            double pos = (SeekSlider.Value / 100.0) * _durationSeconds;
            Player.Position = TimeSpan.FromSeconds(pos);
            UpdateSeekTimeDisplay(pos);
        }
    }
    private void UpdateSeekFromPlayer()
    {
        if (_durationSeconds <= 0) return;
        double cur = Player.Position.TotalSeconds;

        if (_isPlayingRangeOnly && cur >= _endTimeSeconds)
        {
            Player.Position = TimeSpan.FromSeconds(_startTimeSeconds);
            return;
        }

        SeekSlider.Value = Math.Clamp((cur / _durationSeconds) * 100.0, 0, 100);
        UpdateSeekTimeDisplay(cur);
    }
    private void UpdateSeekTimeDisplay(double curSec)
    {
        if (SeekTimeText != null)
        {
            var cur = TimeSpan.FromSeconds(curSec);
            var tot = TimeSpan.FromSeconds(_durationSeconds);
            SeekTimeText.Text = $"{cur:mm\\:ss\\.ff} / {tot:mm\\:ss\\.ff}";
        }
    }

    // ── In/Out Range Controls ─────────────────────────────────────────────────
    private void SetStart_Click(object s, RoutedEventArgs e)
    {
        if (_durationSeconds <= 0) return;
        _startTimeSeconds = Math.Clamp(Player.Position.TotalSeconds, 0, _endTimeSeconds - 0.2);
        UpdateRangeBoxes();
    }
    private void SetEnd_Click(object s, RoutedEventArgs e)
    {
        if (_durationSeconds <= 0) return;
        _endTimeSeconds = Math.Clamp(Player.Position.TotalSeconds, _startTimeSeconds + 0.2, _durationSeconds);
        UpdateRangeBoxes();
    }

    private void StartTimeBox_LostFocus(object s, RoutedEventArgs e)
    {
        if (TimeSpan.TryParseExact(StartTimeBox.Text, @"mm\:ss\.ff", CultureInfo.InvariantCulture, out var ts))
        {
            _startTimeSeconds = Math.Clamp(ts.TotalSeconds, 0, _endTimeSeconds - 0.2);
            UpdateRangeBoxes();
        }
    }
    private void EndTimeBox_LostFocus(object s, RoutedEventArgs e)
    {
        if (TimeSpan.TryParseExact(EndTimeBox.Text, @"mm\:ss\.ff", CultureInfo.InvariantCulture, out var ts))
        {
            _endTimeSeconds = Math.Clamp(ts.TotalSeconds, _startTimeSeconds + 0.2, _durationSeconds > 0 ? _durationSeconds : 3600);
            UpdateRangeBoxes();
        }
    }

    private void UpdateRangeBoxes()
    {
        if (StartTimeBox != null) StartTimeBox.Text = TimeSpan.FromSeconds(_startTimeSeconds).ToString(@"mm\:ss\.ff");
        if (EndTimeBox != null) EndTimeBox.Text = TimeSpan.FromSeconds(_endTimeSeconds).ToString(@"mm\:ss\.ff");
        double diff = Math.Max(0.1, _endTimeSeconds - _startTimeSeconds);
        if (HeaderClipDuration != null) HeaderClipDuration.Text = $"Clip: {diff:F1}s";
    }

    // ── Output Management ─────────────────────────────────────────────────────
    private void BrowseOutput_Click(object s, RoutedEventArgs e)
    {
        var dlg = new WinForms.FolderBrowserDialog
        {
            Description = "Select Output Folder for Animated GIF/WebP",
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
            OutputPathText.Text = "Same as source file";
            OutputPathText.Foreground = (Brush)FindResource("MutedBrush");
        }
    }
    private void OpenFolder_Click(object s, RoutedEventArgs e)
    {
        try
        {
            string dir = !string.IsNullOrEmpty(_lastOutputFolder) ? _lastOutputFolder :
                         !string.IsNullOrEmpty(_customOutputFolder) ? _customOutputFolder :
                         Path.GetDirectoryName(_filePath) ?? "";
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Process.Start("explorer.exe", dir);
            else
                MessageBox.Show("Output folder not found or no video loaded yet.", "VideoFixPro", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ToggleLog_Click(object s, RoutedEventArgs e)
    {
        if (LogBox == null || ToggleLogBtn == null) return;
        if (LogBox.Visibility == Visibility.Visible)
        {
            LogBox.Visibility = Visibility.Collapsed;
            ToggleLogBtn.Content = "Show";
        }
        else
        {
            LogBox.Visibility = Visibility.Visible;
            ToggleLogBtn.Content = "Hide";
        }
    }

    // ── Generation Execution ──────────────────────────────────────────────────
    private async void Generate_Click(object s, RoutedEventArgs e)
    {
            try
            {
        if (_isRendering) return;
        if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
        {
            MessageBox.Show("Please load a video file first.", "No File", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ExecuteGenerateAsync();
    }
            catch (Exception ex)
            {
                MessageBox.Show("An unexpected error occurred: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

    private void Cancel_Click(object s, RoutedEventArgs e)
    {
        _cts?.Cancel();
        try { _ffmpegProcess?.Kill(); } catch { }
    }

    private async Task ExecuteGenerateAsync()
    {
        _isRendering = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        bool isWebP = FormatBox.SelectedIndex == 1;
        string ext = isWebP ? ".webp" : ".gif";

        string dir = !string.IsNullOrEmpty(_customOutputFolder) ? _customOutputFolder : Path.GetDirectoryName(_filePath) ?? ".";
        string safeName = Path.GetFileNameWithoutExtension(_filePath);
        string outputPath = Path.Combine(dir, $"{safeName}_animated{ext}");
        outputPath = GetUniqueFilePath(outputPath);
        _lastOutputFolder = dir;

        if (_durationSeconds <= 0)
        {
            await ProbeVideoAsync(_filePath);
            if (_durationSeconds <= 0) _durationSeconds = 1.0;
            if (_endTimeSeconds <= 0) _endTimeSeconds = Math.Min(10.0, _durationSeconds);
        }

        SetRenderingUI(true);
        SetStatus($"Generating {(isWebP ? "WebP" : "GIF")} animation...", "#388BFD");

        int fps = FpsBox.SelectedIndex switch { 0 => 12, 1 => 15, 2 => 24, 3 => 30, 4 => 60, _ => 24 };
        int width = WidthBox.SelectedIndex switch { 0 => 320, 1 => 480, 2 => 640, 3 => 720, _ => -1 };
        string scaleFilter = width > 0 ? $"scale={width}:-2:flags=lanczos" : "scale=trunc(iw/2)*2:trunc(ih/2)*2:flags=lanczos";

        string ditherParam = DitherBox.SelectedIndex switch
        {
            0 => "dither=bayer:bayer_scale=5:diff_mode=rectangle",
            1 => "dither=floyd_steinberg:diff_mode=rectangle",
            2 => "dither=sierra2:diff_mode=rectangle",
            _ => "dither=none"
        };

        int loopCount = LoopBox.SelectedIndex == 1 ? -1 : 0;
        double clipDuration = Math.Max(0.1, _endTimeSeconds - _startTimeSeconds);

        string ss = _startTimeSeconds.ToString("F2", CultureInfo.InvariantCulture);
        string to = _endTimeSeconds.ToString("F2", CultureInfo.InvariantCulture);

        bool success = false;
        string tempPalette = Path.Combine(Path.GetTempPath(), $"vfp_palette_{Guid.NewGuid():N}.png");

        try
        {
            if (!isWebP)
            {
                // ── 2-Pass GIF Palette Engine ──
                Log($"\n[GIF] Pass 1/2: Generating dynamic color palette...");
                SetStatus("Pass 1/2 (Palette Extraction)...", "#388BFD");

                string pass1Args = $"-y -ss {ss} -to {to} -i \"{_filePath}\" -vf \"fps={fps},{scaleFilter},palettegen=stats_mode=diff\" \"{tempPalette}\"";
                Log($"[CMD Pass 1] ffmpeg {pass1Args}");
                bool p1 = await RunFFmpegAsync(pass1Args, clipDuration, _cts.Token, pass: 1);

                if (p1 && File.Exists(tempPalette) && !_cts.Token.IsCancellationRequested)
                {
                    Log($"[GIF] Pass 2/2: Rendering animated GIF with {ditherParam}...");
                    SetStatus("Pass 2/2 (Rendering GIF)...", "#388BFD");

                    string pass2Args = $"-y -ss {ss} -to {to} -i \"{_filePath}\" -i \"{tempPalette}\" -filter_complex \"[0:v]fps={fps},{scaleFilter}[x];[x][1:v]paletteuse={ditherParam}\" -loop {loopCount} \"{outputPath}\"";
                    Log($"[CMD Pass 2] ffmpeg {pass2Args}");
                    success = await RunFFmpegAsync(pass2Args, clipDuration, _cts.Token, pass: 2);
                }
            }
            else
            {
                // ── Animated WebP Engine ──
                Log($"\n[WEBP] Encoding animated WebP (lossless/HQ)...");
                int webpLoop = LoopBox.SelectedIndex == 1 ? 1 : 0;
                string webpArgs = $"-y -ss {ss} -to {to} -i \"{_filePath}\" -vf \"fps={fps},{scaleFilter}\" -vcodec libwebp -lossless 0 -qscale 75 -loop {webpLoop} -an \"{outputPath}\"";
                Log($"[CMD] ffmpeg {webpArgs}");
                success = await RunFFmpegAsync(webpArgs, clipDuration, _cts.Token, pass: 0);
            }
        }
        finally
        {
            try { if (File.Exists(tempPalette)) File.Delete(tempPalette); } catch { }
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
            SetStatus($"Done! Output: {finalMb:F2} MB", "#3FB950");
            Log($"[SUCCESS] Saved animation: {outputPath} ({finalMb:F2} MB)");
            ShowNotification("Animation Complete", $"Saved: {Path.GetFileName(outputPath)} ({finalMb:F2} MB)");
        }
        else
        {
            SetStatus("Animation failed", "#F85149");
        }
    }

    private async Task<bool> RunFFmpegAsync(string args, double totalDuration, CancellationToken token, int pass = 0)
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
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _ffmpegProcess = proc;

        var timeRegex = new Regex(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

        proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            string line = e.Data;
            Dispatcher.Invoke(() => Log(line));

            var match = timeRegex.Match(line);
            if (match.Success && totalDuration > 0)
            {
                if (double.TryParse(match.Groups[1].Value, out double h) &&
                    double.TryParse(match.Groups[2].Value, out double m) &&
                    double.TryParse(match.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double s))
                {
                    double curSec = h * 3600 + m * 60 + s;
                    double percent = Math.Clamp((curSec / totalDuration) * 100.0, 0, 99.5);
                    if (pass == 1) percent *= 0.5;
                    else if (pass == 2) percent = 50.0 + (percent * 0.5);

                    Dispatcher.Invoke(() => SetRenderProgress(percent));
                }
            }
        };

        proc.Exited += (_, _) =>
        {
            ProcessGuard.Unwatch(proc);
            tcs.TrySetResult(proc.ExitCode == 0);
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
        finally
        {
            ProcessGuard.Unwatch(proc);
        }
    }

    private void SetRenderingUI(bool rendering)
    {
        if (RenderProgressPanel != null) RenderProgressPanel.Visibility = rendering ? Visibility.Visible : Visibility.Collapsed;
        if (CancelBtn != null) CancelBtn.Visibility = rendering ? Visibility.Visible : Visibility.Collapsed;
        if (GenerateBtn != null) GenerateBtn.IsEnabled = !rendering;
        if (OpenFolderBtn != null) OpenFolderBtn.IsEnabled = !rendering && (!string.IsNullOrEmpty(_lastOutputFolder) || !string.IsNullOrEmpty(_filePath) || !string.IsNullOrEmpty(_customOutputFolder));

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
        if (LogBox == null) return;
        LogBox.AppendText(text + "\n");
        LogBox.ScrollToEnd();
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
