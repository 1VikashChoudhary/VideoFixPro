using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
        private string? _customOutputDir;
        private bool _isRendering;
        private CancellationTokenSource? _cts;
        private Process? _ffmpegProcess;

        // Uses standard app directory logic for FFmpeg
        private string FFmpeg => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
        private string FFprobe => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffprobe.exe");

        public SocialFormatWindow(bool hasNvidia, bool hasAmd, bool hasIntel)
        {
            InitializeComponent();
            _hasNvidia = hasNvidia;
            _hasAmd = hasAmd;
            _hasIntel = hasIntel;
        }

        // --- Custom Title Bar Interactions ---
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
            Close();
        }
        private void MaxBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        // --- UI Events ---
        private void BgModeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (BlurIntensityPanel == null) return;
            BlurIntensityPanel.Visibility = BgModeBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // --- Drag & Drop ---
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
                MessageBox.Show("Error dropping file: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            PlayerBorder.Visibility = Visibility.Visible;
            Player.Source = new Uri(path);
            Player.Volume = 0; // Mute the background preview loop
            Player.Play();
            StartBtn.IsEnabled = true;

            await ProbeVideoAsync(path);
            SetStatus($"Loaded: {Path.GetFileName(path)}", "#3FB950");
        }

        private void Player_MediaEnded(object sender, RoutedEventArgs e)
        {
            Player.Position = TimeSpan.Zero;
            Player.Play();
        }

        private async Task ProbeVideoAsync(string path)
        {
            if (!File.Exists(FFprobe)) return;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = FFprobe,
                    Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{path}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using var proc = new Process { StartInfo = psi };
                proc.Start();
                string output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                if (double.TryParse(output.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double dur))
                {
                    _durationSeconds = dur;
                    Log($"[PROBE] Duration: {_durationSeconds:F2} seconds");
                }
            }
            catch { }
        }

        // --- Output Selection ---
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

        // --- Processing Logic ---
        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_isRendering || string.IsNullOrEmpty(_filePath)) return;

            try
            {
                _isRendering = true;
                _cts?.Dispose();
                _cts = new CancellationTokenSource();

                string dir = _customOutputDir ?? Path.GetDirectoryName(_filePath) ?? ".";
                string ext = Path.GetExtension(_filePath);
                string suffix = RatioBox.SelectedIndex switch
                {
                    0 => "_9x16",
                    1 => "_1x1",
                    2 => "_4x5",
                    3 => "_16x9",
                    _ => "_social"
                };
                string outputPath = OutputDirBox.Text == "Same as source file"
                    ? Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(_filePath)}{suffix}.mp4")
                    : OutputDirBox.Text;
                
                outputPath = GetUniqueFilePath(outputPath);

                SetRenderingUI(true);
                SetStatus("Rendering video for social media...", "#388BFD");
                Log($"\n[FORMATTER] Input: {Path.GetFileName(_filePath)}");

                // Determine Target Size
                int outW = 1080, outH = 1920;
                switch (RatioBox.SelectedIndex)
                {
                    case 0: outW = 1080; outH = 1920; break;
                    case 1: outW = 1080; outH = 1080; break;
                    case 2: outW = 1080; outH = 1350; break;
                    case 3: outW = 1920; outH = 1080; break;
                }

                // Construct Filter Complex
                string filter = "";
                if (BgModeBox.SelectedIndex == 0) // Blurred Video Background
                {
                    int blur = (int)BlurSlider.Value;
                    filter = $"[0:v]split=2[bg][fg]; [bg]scale={outW}:{outH}:force_original_aspect_ratio=increase,crop={outW}:{outH},boxblur={blur}:5[bg_blurred]; [fg]scale={outW}:{outH}:force_original_aspect_ratio=decrease[fg_scaled]; [bg_blurred][fg_scaled]overlay=(W-w)/2:(H-h)/2[outv]";
                }
                else // Solid Color Background
                {
                    string color = BgModeBox.SelectedIndex == 1 ? "black" : "white";
                    filter = $"[0:v]scale={outW}:{outH}:force_original_aspect_ratio=decrease[fg_scaled]; color=c={color}:s={outW}x{outH}[bg]; [bg][fg_scaled]overlay=(W-w)/2:(H-h)/2[outv]";
                }

                // Codec Args
                bool useGpu = GpuCheck.IsChecked == true;
                bool isAv1 = Av1Check.IsChecked == true;
                string vCodecArgs;
                if (isAv1)
                {
                    vCodecArgs = useGpu && _hasNvidia ? "-c:v av1_nvenc -preset p5 -cq 22 -pix_fmt yuv420p" :
                                 useGpu && _hasAmd ? "-c:v av1_amf -qp_i 22 -qp_p 22 -qp_b 22 -pix_fmt yuv420p" :
                                 useGpu && _hasIntel ? "-c:v av1_qsv -global_quality 22 -pix_fmt nv12" :
                                 "-c:v libsvtav1 -preset 8 -crf 22 -pix_fmt yuv420p";
                }
                else
                {
                    vCodecArgs = useGpu && _hasNvidia ? "-c:v h264_nvenc -preset p4 -cq 22 -pix_fmt yuv420p" :
                                 useGpu && _hasAmd ? "-c:v h264_amf -qp_i 22 -qp_p 22 -qp_b 22 -pix_fmt yuv420p" :
                                 useGpu && _hasIntel ? "-c:v h264_qsv -global_quality 22 -pix_fmt nv12" :
                                 "-c:v libx264 -preset fast -crf 20 -pix_fmt yuv420p";
                }

                string args = $"-y -i \"{_filePath}\" -filter_complex \"{filter}\" -map \"[outv]\" -map 0:a? {vCodecArgs} -c:a aac -b:a 192k \"{outputPath}\"";
                
                Log($"[CMD] ffmpeg {args}");
                bool success = await RunFFmpegAsync(args, _durationSeconds, _cts.Token);

                if (!success && !_cts.Token.IsCancellationRequested && useGpu)
                {
                    Log("[WARN] GPU encoding failed. Retrying on CPU (libx264)...");
                    SetStatus("Retrying on CPU...", "#D29922");
                    args = $"-y -i \"{_filePath}\" -filter_complex \"{filter}\" -map \"[outv]\" -map 0:a? -c:v libx264 -preset fast -crf 20 -pix_fmt yuv420p -c:a aac -b:a 192k \"{outputPath}\"";
                    Log($"[CMD Fallback] ffmpeg {args}");
                    success = await RunFFmpegAsync(args, _durationSeconds, _cts.Token);
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
                MessageBox.Show("An error occurred: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            try { _ffmpegProcess?.Kill(); } catch { }
        }

        private async Task<bool> RunFFmpegAsync(string args, double totalDuration, CancellationToken token)
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
                    Dispatcher.InvokeAsync(() => SetRenderProgress(percent));
                }
                else if (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                         e.Data.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                         e.Data.Contains("fatal", StringComparison.OrdinalIgnoreCase))
                {
                    Dispatcher.InvokeAsync(() => Log($"[FFMPEG ERR] {e.Data}"));
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

        // --- Helpers ---
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



