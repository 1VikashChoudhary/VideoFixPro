using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace VideoFixPro
{
    public partial class SocialFormatWindow : Window
    {
        private readonly bool _hasNvidia;
        private readonly bool _hasAmd;
        private readonly bool _hasIntel;

        private string? _filePath;
        private double _durationSeconds;
        private int _sourceWidth;
        private int _sourceHeight;
        private string? _customOutputDir;
        private bool _isRendering;
        private CancellationTokenSource? _cts;
        private Process? _ffmpegProcess;

        // Player & Live Preview
        private readonly System.Windows.Threading.DispatcherTimer _playheadTimer = new();
        private bool _isPlayerPlaying;
        private bool _isSeeking;
        private bool _isMuted;

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
            return File.Exists(localBin) ? localBin : Path.Combine(AppDir, name);
        }

        public SocialFormatWindow(bool hasNvidia, bool hasAmd, bool hasIntel)
        {
            InitializeComponent();
            _hasNvidia = hasNvidia;
            _hasAmd = hasAmd;
            _hasIntel = hasIntel;

            GpuCheck.IsChecked = _hasNvidia || _hasAmd || _hasIntel;

            _playheadTimer.Interval = TimeSpan.FromMilliseconds(40);
            _playheadTimer.Tick += PlayheadTimer_Tick;

            Loaded += (_, _) =>
            {
                string gpuInfo = _hasAmd ? "AMD AMF (Radeon RX Hardware Encoder)" :
                                 _hasNvidia ? "NVIDIA NVENC Hardware Encoder" :
                                 _hasIntel ? "Intel QSV Hardware Encoder" : "CPU (Software)";
                Log($"[INIT] GPU Acceleration: {gpuInfo}");
            };
        }

        // ─────────────────────────────────────────────────────────────
        //  TITLE BAR & WINDOW CONTROLS
        // ─────────────────────────────────────────────────────────────
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isRendering)
            {
                var res = MessageBox.Show("Rendering is in progress. Are you sure you want to exit?",
                                          "Confirm Exit", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
                _cts?.Cancel();
                try { _ffmpegProcess?.Kill(); } catch { }
            }
            _playheadTimer.Stop();
            try { Player.Source = null; BgPlayer.Source = null; } catch { }
            Close();
        }
        private void MaxBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space && !IsKeyboardFocusedOnInput())
            {
                e.Handled = true;
                TogglePlay_Click(sender, e);
            }
        }

        private bool IsKeyboardFocusedOnInput()
        {
            return Keyboard.FocusedElement is TextBox or ComboBox;
        }

        // ─────────────────────────────────────────────────────────────
        //  DRAG & DROP / FILE LOADING
        // ─────────────────────────────────────────────────────────────
        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
        }
        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
            else e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0)
                    {
                        await LoadFileAsync(files[0]);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading dropped file: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void DropZone_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Video Files|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.flv;*.webm;*.ts;*.m4v|All Files|*.*"
                };
                if (dlg.ShowDialog() == true)
                {
                    await LoadFileAsync(dlg.FileName);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error opening file: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadFileAsync(string path)
        {
            _filePath = path;
            TitleFileName.Text = Path.GetFileName(path);
            DropZone.Visibility = Visibility.Collapsed;
            PreviewHost.Visibility = Visibility.Visible;
            SeekPanel.Visibility = Visibility.Visible;

            var uri = new Uri(path);
            Player.Source = uri;
            BgPlayer.Source = uri;
            Player.Volume = VolumeSlider.Value;
            BgPlayer.IsMuted = true;

            // Start loading & decoding
            Player.Play();
            BgPlayer.Play();
            _isPlayerPlaying = true;

            StartBtn.IsEnabled = true;

            await ProbeVideoAsync(path);
            UpdateCanvasLayout();
            UpdateBackgroundMode();
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
                using var proc = new Process { StartInfo = psi };
                proc.Start();
                string output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                var root = JsonNode.Parse(output);
                if (root?["format"]?["duration"]?.GetValue<string>() is string durStr &&
                    double.TryParse(durStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double dur))
                {
                    _durationSeconds = dur;
                    SeekSlider.Maximum = dur;
                }

                var streams = root?["streams"]?.AsArray();
                if (streams != null)
                {
                    foreach (var stream in streams)
                    {
                        if (stream?["codec_type"]?.GetValue<string>() == "video")
                        {
                            _sourceWidth = stream?["width"]?.GetValue<int>() ?? 0;
                            _sourceHeight = stream?["height"]?.GetValue<int>() ?? 0;
                            break;
                        }
                    }
                }
                Log($"[PROBE] Duration: {_durationSeconds:F2}s | Resolution: {_sourceWidth}x{_sourceHeight}");
            }
            catch (Exception ex)
            {
                Log($"[WARN] Probe info: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  LIVE INTERACTIVE CANVAS & REAL-TIME PREVIEW
        // ─────────────────────────────────────────────────────────────
        private void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateCanvasLayout();
        }

        private void RatioBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateCanvasLayout();
        }

        private void UpdateCanvasLayout()
        {
            if (PreviewHost == null || CanvasFrame == null || PreviewBadgeText == null) return;

            double containerW = PreviewHost.ActualWidth;
            double containerH = PreviewHost.ActualHeight;
            if (containerW <= 20 || containerH <= 20) return;

            double availW = containerW - 24;
            double availH = containerH - 24;

            // Target Ratios: 0: 9:16 (0.5625), 1: 1:1 (1.0), 2: 4:5 (0.8), 3: 16:9 (1.7778)
            double targetRatio = 9.0 / 16.0;
            string ratioLabel = "9:16 (1080×1920)";

            switch (RatioBox?.SelectedIndex ?? 0)
            {
                case 0: targetRatio = 9.0 / 16.0; ratioLabel = "9:16 (1080×1920)"; break;
                case 1: targetRatio = 1.0 / 1.0;  ratioLabel = "1:1 (1080×1080)"; break;
                case 2: targetRatio = 4.0 / 5.0;  ratioLabel = "4:5 (1080×1350)"; break;
                case 3: targetRatio = 16.0 / 9.0; ratioLabel = "16:9 (1920×1080)"; break;
            }

            PreviewBadgeText.Text = $"⚡ LIVE PREVIEW · {ratioLabel}";

            double frameW, frameH;
            if (availW / availH > targetRatio)
            {
                frameH = availH;
                frameW = availH * targetRatio;
            }
            else
            {
                frameW = availW;
                frameH = availW / targetRatio;
            }

            CanvasFrame.Width = Math.Max(40, frameW);
            CanvasFrame.Height = Math.Max(40, frameH);
        }

        private void BgModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateBackgroundMode();
        }

        private void UpdateBackgroundMode()
        {
            if (BgModeBox == null || BgSolidLayer == null || BgPlayer == null || BlurIntensityPanel == null) return;

            if (BgModeBox.SelectedIndex == 0) // Dynamic Blurred Video
            {
                BgPlayer.Visibility = Visibility.Visible;
                BgSolidLayer.Visibility = Visibility.Collapsed;
                BlurIntensityPanel.Visibility = Visibility.Visible;
                if (BgBlurEffect != null && BlurSlider != null)
                    BgBlurEffect.Radius = BlurSlider.Value;
            }
            else if (BgModeBox.SelectedIndex == 1) // Solid Black
            {
                BgPlayer.Visibility = Visibility.Collapsed;
                BgSolidLayer.Visibility = Visibility.Visible;
                BgSolidLayer.Background = Brushes.Black;
                BlurIntensityPanel.Visibility = Visibility.Collapsed;
            }
            else // Solid White
            {
                BgPlayer.Visibility = Visibility.Collapsed;
                BgSolidLayer.Visibility = Visibility.Visible;
                BgSolidLayer.Background = Brushes.White;
                BlurIntensityPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void BlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BgBlurEffect != null && BlurSlider != null)
            {
                BgBlurEffect.Radius = BlurSlider.Value;
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  PLAYBACK & SCRUBBING CONTROLS
        // ─────────────────────────────────────────────────────────────
        private void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
            try
            {
                // Pause upon loading first frame so user sees clean preview
                Player.Pause();
                BgPlayer.Pause();
                _isPlayerPlaying = false;
                _playheadTimer.Stop();
                UpdatePlayPauseUI();
            }
            catch { }
        }

        private void Player_MediaEnded(object sender, RoutedEventArgs e)
        {
            try
            {
                Player.Position = TimeSpan.Zero;
                BgPlayer.Position = TimeSpan.Zero;
                Player.Pause();
                BgPlayer.Pause();
                _isPlayerPlaying = false;
                _playheadTimer.Stop();
                UpdatePlayPauseUI();
            }
            catch { }
        }

        private void Player_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
        {
            Log($"[WARN] Video preview rendering warning: {e.ErrorException.Message}");
        }

        private void TogglePlay_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_filePath)) return;

            if (_isPlayerPlaying)
            {
                try
                {
                    Player.Pause();
                    BgPlayer.Pause();
                }
                catch { }
                _isPlayerPlaying = false;
                _playheadTimer.Stop();
            }
            else
            {
                try
                {
                    if (_durationSeconds > 0 && Player.Position.TotalSeconds >= _durationSeconds - 0.1)
                    {
                        Player.Position = TimeSpan.Zero;
                        BgPlayer.Position = TimeSpan.Zero;
                    }
                    Player.Play();
                    if (BgPlayer.Visibility == Visibility.Visible)
                        BgPlayer.Play();
                }
                catch { }
                _isPlayerPlaying = true;
                _playheadTimer.Start();
            }
            UpdatePlayPauseUI();
        }

        private void UpdatePlayPauseUI()
        {
            if (SeekPlayBtn != null)
                SeekPlayBtn.Content = _isPlayerPlaying ? "❚❚" : "▶";

            if (PlayOverlayBtn != null)
                PlayOverlayBtn.Visibility = _isPlayerPlaying ? Visibility.Collapsed : Visibility.Visible;
        }

        private void PlayheadTimer_Tick(object? sender, EventArgs e)
        {
            if (_isSeeking || Player == null) return;
            try
            {
                double pos = Player.Position.TotalSeconds;
                SeekSlider.Value = pos;
                SeekTimeText.Text = $"{FormatTime(pos)} / {FormatTime(_durationSeconds)}";

                // Keep background player tightly synced
                if (BgPlayer != null && BgPlayer.Visibility == Visibility.Visible)
                {
                    double bgPos = BgPlayer.Position.TotalSeconds;
                    if (Math.Abs(bgPos - pos) > 0.15)
                    {
                        BgPlayer.Position = Player.Position;
                    }
                }
            }
            catch { }
        }

        private void SeekSlider_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isSeeking = true;
        }

        private void SeekSlider_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isSeeking = false;
            SeekTo(SeekSlider.Value);
        }

        private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isSeeking)
            {
                SeekTo(SeekSlider.Value);
                SeekTimeText.Text = $"{FormatTime(SeekSlider.Value)} / {FormatTime(_durationSeconds)}";
            }
        }

        private void SeekTo(double seconds)
        {
            try
            {
                var ts = TimeSpan.FromSeconds(seconds);
                Player.Position = ts;
                BgPlayer.Position = ts;
            }
            catch { }
        }

        private void MuteToggle_Click(object sender, RoutedEventArgs e)
        {
            _isMuted = !_isMuted;
            Player.IsMuted = _isMuted;
            MuteBtn.Content = _isMuted ? "🔇" : "🔊";
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (Player != null)
            {
                Player.Volume = VolumeSlider.Value;
                if (Player.Volume > 0 && _isMuted)
                {
                    _isMuted = false;
                    Player.IsMuted = false;
                    if (MuteBtn != null) MuteBtn.Content = "🔊";
                }
            }
        }

        private static string FormatTime(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.Hours > 0
                ? $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        // ─────────────────────────────────────────────────────────────
        //  OUTPUT DIRECTORY SELECTION
        // ─────────────────────────────────────────────────────────────
        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "MP4 File|*.mp4|MKV File|*.mkv",
                FileName = _filePath != null ? $"{Path.GetFileNameWithoutExtension(_filePath)}_social.mp4" : "output.mp4"
            };
            if (dlg.ShowDialog() == true)
            {
                _customOutputDir = Path.GetDirectoryName(dlg.FileName);
                OutputDirBox.Text = dlg.FileName;
            }
        }

        private void ResetOutput_Click(object sender, RoutedEventArgs e)
        {
            _customOutputDir = null;
            OutputDirBox.Text = "Same as source file";
        }

        private string GetUniqueFilePath(string filePath)
        {
            if (!File.Exists(filePath)) return filePath;
            string dir = Path.GetDirectoryName(filePath) ?? "";
            string name = Path.GetFileNameWithoutExtension(filePath);
            string ext = Path.GetExtension(filePath);
            int count = 1;
            while (File.Exists(filePath))
            {
                filePath = Path.Combine(dir, $"{name} ({count}){ext}");
                count++;
            }
            return filePath;
        }

        // ─────────────────────────────────────────────────────────────
        //  FFMPEG RENDERING ENGINE (GPU ACCELERATED & OPTIMIZED)
        // ─────────────────────────────────────────────────────────────
        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_isRendering || string.IsNullOrEmpty(_filePath)) return;

            try
            {
                // Pause preview during rendering
                try { Player.Pause(); BgPlayer.Pause(); _isPlayerPlaying = false; _playheadTimer.Stop(); UpdatePlayPauseUI(); } catch { }

                _isRendering = true;
                _cts?.Dispose();
                _cts = new CancellationTokenSource();

                string dir = _customOutputDir ?? Path.GetDirectoryName(_filePath) ?? ".";
                string suffix = RatioBox.SelectedIndex switch
                {
                    0 => "_9x16_reel",
                    1 => "_1x1_square",
                    2 => "_4x5_portrait",
                    3 => "_16x9_landscape",
                    _ => "_social"
                };
                string outputPath = OutputDirBox.Text == "Same as source file"
                    ? Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(_filePath)}{suffix}.mp4")
                    : OutputDirBox.Text;

                outputPath = GetUniqueFilePath(outputPath);

                SetRenderingUI(true);
                SetStatus("Rendering video for social media...", "#388BFD");
                Log($"\n[FORMATTER] Input: {Path.GetFileName(_filePath)}");

                // Determine Target Dimensions
                int outW = 1080, outH = 1920;
                switch (RatioBox.SelectedIndex)
                {
                    case 0: outW = 1080; outH = 1920; break;
                    case 1: outW = 1080; outH = 1080; break;
                    case 2: outW = 1080; outH = 1350; break;
                    case 3: outW = 1920; outH = 1080; break;
                }

                // Construct Ultra-Fast High Performance Filter Complex
                string filter = "";
                if (BgModeBox.SelectedIndex == 0) // Dynamic Blurred Video
                {
                    int blurVal = (int)BlurSlider.Value;
                    int bgDownW = outW / 3;
                    int bgDownH = outH / 3;
                    if (bgDownW % 2 != 0) bgDownW++;
                    if (bgDownH % 2 != 0) bgDownH++;

                    int blurRadius = Math.Clamp(blurVal / 3, 2, 16);
                    filter = $"[0:v]split=2[bg_in][fg_in]; [bg_in]scale={bgDownW}:{bgDownH}:force_original_aspect_ratio=increase,crop={bgDownW}:{bgDownH},boxblur={blurRadius}:1,scale={outW}:{outH}:flags=bicubic[bg_blurred]; [fg_in]scale={outW}:{outH}:force_original_aspect_ratio=decrease[fg_scaled]; [bg_blurred][fg_scaled]overlay=(W-w)/2:(H-h)/2[outv]";
                }
                else // Solid Color Background
                {
                    string color = BgModeBox.SelectedIndex == 1 ? "black" : "white";
                    filter = $"[0:v]scale={outW}:{outH}:force_original_aspect_ratio=decrease[fg_scaled]; color=c={color}:s={outW}x{outH}:d={_durationSeconds.ToString(CultureInfo.InvariantCulture)}[bg]; [bg][fg_scaled]overlay=(W-w)/2:(H-h)/2:shortest=1[outv]";
                }

                // Codec Options & GPU Acceleration Selection
                bool useGpu = GpuCheck.IsChecked == true;
                bool isAv1 = Av1Check.IsChecked == true;
                string vCodecArgs;
                string gpuEngineName;

                if (isAv1)
                {
                    if (useGpu && _hasNvidia) { vCodecArgs = "-c:v av1_nvenc -preset p5 -cq 22 -pix_fmt yuv420p"; gpuEngineName = "NVIDIA NVENC (AV1)"; }
                    else if (useGpu && _hasAmd) { vCodecArgs = "-c:v av1_amf -rc cqp -qp_i 22 -qp_p 22 -qp_b 22 -quality speed -pix_fmt yuv420p"; gpuEngineName = "AMD AMF (AV1)"; }
                    else if (useGpu && _hasIntel) { vCodecArgs = "-c:v av1_qsv -global_quality 22 -pix_fmt nv12"; gpuEngineName = "Intel QSV (AV1)"; }
                    else { vCodecArgs = "-c:v libsvtav1 -preset 8 -crf 22 -pix_fmt yuv420p"; gpuEngineName = "CPU (libsvtav1)"; }
                }
                else
                {
                    if (useGpu && _hasNvidia) { vCodecArgs = "-c:v h264_nvenc -preset p4 -cq 20 -pix_fmt yuv420p"; gpuEngineName = "NVIDIA NVENC (H.264)"; }
                    else if (useGpu && _hasAmd) { vCodecArgs = "-c:v h264_amf -rc cqp -qp_i 20 -qp_p 20 -qp_b 20 -quality speed -pix_fmt yuv420p"; gpuEngineName = "AMD AMF (H.264)"; }
                    else if (useGpu && _hasIntel) { vCodecArgs = "-c:v h264_qsv -global_quality 20 -pix_fmt nv12"; gpuEngineName = "Intel QSV (H.264)"; }
                    else { vCodecArgs = "-c:v libx264 -preset fast -crf 20 -pix_fmt yuv420p"; gpuEngineName = "CPU (libx264)"; }
                }

                Log($"[ENCODER] Active Hardware Engine: {gpuEngineName}");
                string args = $"-y -i \"{_filePath}\" -filter_complex \"{filter}\" -map \"[outv]\" -map 0:a? {vCodecArgs} -c:a aac -b:a 192k \"{outputPath}\"";

                Log($"[CMD] ffmpeg {args}");
                bool success = await RunFFmpegAsync(args, _durationSeconds, gpuEngineName, _cts.Token);

                if (!success && !_cts.Token.IsCancellationRequested && useGpu)
                {
                    Log("[WARN] GPU encoding failed. Retrying on CPU (libx264)...");
                    SetStatus("Retrying on CPU fallback...", "#D29922");
                    args = $"-y -i \"{_filePath}\" -filter_complex \"{filter}\" -map \"[outv]\" -map 0:a? -c:v libx264 -preset fast -crf 20 -pix_fmt yuv420p -c:a aac -b:a 192k \"{outputPath}\"";
                    Log($"[CMD Fallback] ffmpeg {args}");
                    success = await RunFFmpegAsync(args, _durationSeconds, "CPU Fallback", _cts.Token);
                }

                _isRendering = false;
                SetRenderingUI(false);

                if (_cts.Token.IsCancellationRequested)
                {
                    SetStatus("Cancelled", "#D29922");
                    try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                }
                else if (success && File.Exists(outputPath))
                {
                    var fi = new FileInfo(outputPath);
                    SetRenderProgress(100);
                    SetStatus($"Done! Output: {fi.Length / (1024.0 * 1024.0):F1} MB", "#3FB950");
                    Log($"[SUCCESS] Saved to: {outputPath} ({fi.Length / (1024.0 * 1024.0):F1} MB)");
                }
                else
                {
                    SetStatus("Formatting failed", "#F85149");
                }
            }
            catch (Exception ex)
            {
                _isRendering = false;
                SetRenderingUI(false);
                SetStatus("Error occurred", "#F85149");
                MessageBox.Show("An error occurred during formatting: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            try { _ffmpegProcess?.Kill(); } catch { }
        }

        private async Task<bool> RunFFmpegAsync(string args, double totalDuration, string engineName, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = FFmpeg,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            if (_hasNvidia) GpuHelper.InjectNvCudaPath(psi);

            var tcs = new TaskCompletionSource<bool>();
            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _ffmpegProcess = proc;

            var timeRegex = new Regex(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);
            var fpsRegex = new Regex(@"fps=\s*([\d\.]+)", RegexOptions.Compiled);
            var speedRegex = new Regex(@"speed=\s*([\d\.]+)x", RegexOptions.Compiled);

            proc.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                var match = timeRegex.Match(e.Data);
                if (match.Success && totalDuration > 0)
                {
                    double h = double.Parse(match.Groups[1].Value);
                    double m = double.Parse(match.Groups[2].Value);
                    double sec = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                    double currentSeconds = (h * 3600) + (m * 60) + sec;
                    double percent = Math.Clamp((currentSeconds / totalDuration) * 100, 0, 100);

                    var fpsMatch = fpsRegex.Match(e.Data);
                    var speedMatch = speedRegex.Match(e.Data);
                    string speedInfo = speedMatch.Success ? $" · {speedMatch.Groups[1].Value}x" : "";
                    string fpsInfo = fpsMatch.Success ? $" ({fpsMatch.Groups[1].Value} fps)" : "";

                    Dispatcher.InvokeAsync(() =>
                    {
                        SetRenderProgress(percent);
                        SetStatus($"Encoding [{engineName}] — {percent:F0}%{fpsInfo}{speedInfo}", "#388BFD");
                    });
                }
                else if (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                         e.Data.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                         e.Data.Contains("fatal", StringComparison.OrdinalIgnoreCase))
                {
                    Dispatcher.InvokeAsync(() => Log($"[FFMPEG] {e.Data}"));
                }
            };

            proc.Exited += (s, e) => tcs.TrySetResult(proc.ExitCode == 0);

            if (token.IsCancellationRequested) return false;
            using var reg = token.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
                tcs.TrySetCanceled();
            });

            try
            {
                proc.Start();
                proc.BeginErrorReadLine();
                return await tcs.Task;
            }
            catch (Exception ex)
            {
                Log($"[CRITICAL] FFmpeg process failed to start: {ex.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  HELPERS
        // ─────────────────────────────────────────────────────────────
        private void Log(string message)
        {
            LogBox.AppendText(message + Environment.NewLine);
            LogBox.ScrollToEnd();
        }

        private void SetStatus(string message, string hexColor)
        {
            StatusText.Text = message;
            StatusDot.Fill = (SolidColorBrush)new BrushConverter().ConvertFrom(hexColor);
        }

        private void SetRenderingUI(bool rendering)
        {
            StartBtn.IsEnabled = !rendering;
            CancelBtn.IsEnabled = rendering;
            RatioBox.IsEnabled = !rendering;
            BgModeBox.IsEnabled = !rendering;
            BlurSlider.IsEnabled = !rendering;
            DropZone.IsHitTestVisible = !rendering;

            if (rendering)
            {
                TaskbarProgress.ProgressState = TaskbarItemProgressState.Normal;
                ProgressBar.Visibility = Visibility.Visible;
                ProgressPercentText.Visibility = Visibility.Visible;
                ProgressBar.Value = 0;
                ProgressPercentText.Text = "0%";
            }
            else
            {
                TaskbarProgress.ProgressState = TaskbarItemProgressState.None;
                ProgressBar.Visibility = Visibility.Collapsed;
                ProgressPercentText.Visibility = Visibility.Collapsed;
            }
        }

        private void SetRenderProgress(double percent)
        {
            ProgressBar.Value = percent;
            ProgressPercentText.Text = $"{percent:F0}%";
            TaskbarProgress.ProgressValue = percent / 100.0;
        }
    }
}
