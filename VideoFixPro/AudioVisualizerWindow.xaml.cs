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
using Microsoft.Win32;

namespace VideoFixPro
{
    public partial class AudioVisualizerWindow : Window
    {
        private readonly bool _hasNvidia;
        private readonly bool _hasAmd;
        private readonly bool _hasIntel;

        private string? _audioPath;
        private string? _bgImagePath;
        private string? _outputPath;
        private string? _customOutputDir;
        private string? _lastOutputFolder;
        private double _durationSeconds;
        private CancellationTokenSource? _cts;
        private Process? _ffmpegProcess;
        private bool _isProcessing;

        private static string AppDir => AppDomain.CurrentDomain.BaseDirectory;
        private static string FFmpeg => GetBinPath("ffmpeg.exe");
        private static string FFprobe => GetBinPath("ffprobe.exe");

        private static string GetBinPath(string name)
        {
            var appBin = Path.Combine(AppDir, "ffmpeg", name);
            if (File.Exists(appBin)) return appBin;
            var localBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoFixPro", "ffmpeg", name);
            return File.Exists(localBin) ? localBin : appBin;
        }

        public AudioVisualizerWindow(bool hasNvidia, bool hasAmd, bool hasIntel) : this(null, hasNvidia, hasAmd, hasIntel) { }

        public AudioVisualizerWindow(string? preloadPath, bool hasNvidia, bool hasAmd, bool hasIntel)
        {
            InitializeComponent();
            _hasNvidia = hasNvidia;
            _hasAmd = hasAmd;
            _hasIntel = hasIntel;

            if (!string.IsNullOrWhiteSpace(preloadPath) && File.Exists(preloadPath))
            {
                LoadAudio(preloadPath);
            }
        }

        // Window Controls
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }
        private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void MaxBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.O && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                BrowseAudio_Click(sender, e);
            }
        }

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (_isProcessing) return;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length > 0)
                {
                    string ext = Path.GetExtension(files[0]).ToLowerInvariant();
                    if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp")
                    {
                        SetBackgroundImage(files[0]);
                    }
                    else
                    {
                        LoadAudio(files[0]);
                    }
                }
            }
        }

        private void DropZone_Click(object sender, MouseButtonEventArgs e)
        {
            BrowseAudio_Click(sender, e);
        }

        private void BrowseAudio_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                var res = MessageBox.Show("Generating visualizer is in progress. Cancel current render and load a new audio file?",
                                          "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
                _cts?.Cancel();
            }

            var ofd = new OpenFileDialog
            {
                Title = "Select Audio File",
                Filter = "Audio Files|*.mp3;*.wav;*.aac;*.m4a;*.flac;*.ogg;*.wma|All Files|*.*"
            };
            if (ofd.ShowDialog() == true) LoadAudio(ofd.FileName);
        }

        private void LoadAudio(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            _audioPath = path;
            InputAudioBox.Text = path;
            TitleFileName.Text = Path.GetFileName(path);
            UpdateOutputPath();
            ProgressBar.Value = 0;
            ProgressPercentText.Text = "0%";

            Task.Run(() => {
                try {
                    var psi = new ProcessStartInfo {
                        FileName = FFprobe,
                        Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{path}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };
                    var proc = Process.Start(psi);
                    if (proc != null) {
                        ProcessGuard.Watch(proc);
                        try
                        {
                            string outStr = proc.StandardOutput.ReadToEnd();
                            proc.WaitForExit();
                            if (double.TryParse(outStr.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d)) _durationSeconds = d;
                        }
                        finally
                        {
                            ProcessGuard.Unwatch(proc);
                        }
                    }
                } catch { }
            });

            GenerateBtn.IsEnabled = true;
            OpenFolderBtn.IsEnabled = true;
            SetStatus($"Loaded audio: {Path.GetFileName(path)}", "#3FB950");
            Log($"Loaded audio: {Path.GetFileName(path)}");
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? target = null;
                if (!string.IsNullOrEmpty(_lastOutputFolder) && Directory.Exists(_lastOutputFolder))
                    target = _lastOutputFolder;
                else if (!string.IsNullOrEmpty(_customOutputDir) && Directory.Exists(_customOutputDir))
                    target = _customOutputDir;
                else if (!string.IsNullOrEmpty(_audioPath) && File.Exists(_audioPath))
                    target = Path.GetDirectoryName(_audioPath);

                if (!string.IsNullOrEmpty(target) && Directory.Exists(target))
                {
                    Process.Start("explorer.exe", target);
                }
                else
                {
                    MessageBox.Show("Output folder not found or no audio loaded yet.", "VideoFixPro", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SetBackgroundImage(string path)
        {
            _bgImagePath = path;
            BackgroundImageBox.Text = path;
            Log($"Loaded background image: {Path.GetFileName(path)}");
        }

        private void BrowseBg_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog {
                Title = "Select Background Image",
                Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All Files|*.*"
            };
            if (ofd.ShowDialog() == true) SetBackgroundImage(ofd.FileName);
        }

        private void ClearBg_Click(object sender, RoutedEventArgs e)
        {
            _bgImagePath = null;
            BackgroundImageBox.Text = "";
        }

        private void UpdateOutputPath()
        {
            if (string.IsNullOrEmpty(_audioPath)) return;
            string dir = !string.IsNullOrEmpty(_customOutputDir) ? _customOutputDir : (Path.GetDirectoryName(_audioPath) ?? "");
            string name = Path.GetFileNameWithoutExtension(_audioPath) + "_visualizer.mp4";
            _outputPath = Path.Combine(dir, name);
            OutputDirBox.Text = !string.IsNullOrEmpty(_customOutputDir) ? _customOutputDir : "Same as source audio folder";
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var fbd = new OpenFolderDialog { Title = "Select Output Folder" };
            if (fbd.ShowDialog() == true)
            {
                _customOutputDir = fbd.FolderName;
                UpdateOutputPath();
            }
        }

        private void ResetOutput_Click(object sender, RoutedEventArgs e)
        {
            _customOutputDir = null;
            UpdateOutputPath();
        }

        private async void Generate_Click(object sender, RoutedEventArgs e)
        
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_audioPath) || string.IsNullOrWhiteSpace(_outputPath)) return;

            _isProcessing = true;
            GenerateBtn.IsEnabled = false;
            CancelBtn.IsEnabled = true;
            _cts = new CancellationTokenSource();
            ProgressBar.Visibility = Visibility.Visible;
            ProgressPercentText.Visibility = Visibility.Visible;
            ProgressBar.Value = 0;
            ProgressPercentText.Text = "0%";
            SetStatus("Generating waveform animation...", "#388BFD");

            try
            {
                bool isAv1 = CodecBox.SelectedIndex == 1;
                string vCodecArgs;
                if (isAv1)
                {
                    vCodecArgs = _hasNvidia ? "-c:v av1_nvenc -preset p5 -cq 22 -pix_fmt yuv420p" :
                                 _hasAmd ? "-c:v av1_amf -qp_i 22 -qp_p 22 -qp_b 22 -pix_fmt yuv420p" :
                                 _hasIntel ? "-c:v av1_qsv -global_quality 22 -pix_fmt nv12" :
                                 "-c:v libsvtav1 -preset 8 -crf 22 -pix_fmt yuv420p";
                }
                else
                {
                    vCodecArgs = _hasNvidia ? "-c:v h264_nvenc -preset p4 -cq 22 -pix_fmt yuv420p" :
                                 _hasAmd ? "-c:v h264_amf -qp_i 22 -qp_p 22 -qp_b 22 -pix_fmt yuv420p" :
                                 _hasIntel ? "-c:v h264_qsv -global_quality 22 -pix_fmt nv12" :
                                 "-c:v libx264 -preset fast -pix_fmt yuv420p";
                }

                string filterGraph;
                string inputs;

                bool isFreqBars = StyleBox.SelectedIndex == 1;
                string visFilter = isFreqBars
                    ? "showfreqs=s=1280x720:mode=bar:ascale=log:fscale=log:colors=cyan|magenta"
                    : "showwaves=s=1280x720:mode=cline:colors=white|cyan";

                if (string.IsNullOrWhiteSpace(_bgImagePath) || !File.Exists(_bgImagePath))
                {
                    inputs = $"-i \"{_audioPath}\"";
                    filterGraph = $"[0:a]{visFilter}[v]";
                }
                else
                {
                    inputs = $"-loop 1 -framerate 30 -i \"{_bgImagePath}\" -i \"{_audioPath}\"";
                    filterGraph = $"[1:a]{visFilter},format=yuva420p,colorkey=black:0.1:0.1[wave];[0:v]scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2[bg];[bg][wave]overlay=format=auto:shortest=1[v]";
                }

                string audioMap = string.IsNullOrWhiteSpace(_bgImagePath) ? "-map 0:a" : "-map 1:a";
                string args = $"-y {inputs} -filter_complex \"{filterGraph}\" -map \"[v]\" {audioMap} {vCodecArgs} -c:a aac -b:a 192k -shortest \"{_outputPath}\"";

                Log("[CMD] ffmpeg " + args);

                var psi = new ProcessStartInfo
                {
                    FileName = FFmpeg,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = false
                };
                if (_hasNvidia) GpuHelper.InjectNvCudaPath(psi);
                
                _ffmpegProcess = new Process { StartInfo = psi };
                ProcessGuard.Watch(_ffmpegProcess);

                var tcs = new TaskCompletionSource<bool>();
                var timeRegex = new Regex(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

                _ffmpegProcess.ErrorDataReceived += (s, ev) =>
                {
                    if (ev.Data == null) return;
                    var m = timeRegex.Match(ev.Data);
                    if (m.Success && _durationSeconds > 0)
                    {
                        if (int.TryParse(m.Groups[1].Value, out int h) &&
                            int.TryParse(m.Groups[2].Value, out int min) &&
                            double.TryParse(m.Groups[3].Value, out double sec))
                        {
                            double cur = h * 3600 + min * 60 + sec;
                            double pct = Math.Clamp((cur / _durationSeconds) * 100.0, 0, 100);
                            Dispatcher.Invoke(() => {
                                ProgressBar.Value = pct;
                                ProgressPercentText.Text = $"{(int)pct}%";
                            });
                        }
                    }
                };

                _ffmpegProcess.Exited += (s, ev) => tcs.TrySetResult(_ffmpegProcess.ExitCode == 0);
                _ffmpegProcess.EnableRaisingEvents = true;

                _ffmpegProcess.Start();
                _ffmpegProcess.BeginErrorReadLine();

                await using (_cts.Token.Register(() => {
                    try { if (!_ffmpegProcess.HasExited) _ffmpegProcess.Kill(); } catch { }
                    tcs.TrySetResult(false);
                }))
                {
                    bool ok = await tcs.Task;
                    ProcessGuard.Unwatch(_ffmpegProcess);

                    if (ok && !_cts.Token.IsCancellationRequested)
                    {
                        _lastOutputFolder = Path.GetDirectoryName(_outputPath);
                        OpenFolderBtn.IsEnabled = true;
                        ProgressBar.Value = 100;
                        ProgressPercentText.Text = "100%";
                        SetStatus("Visualizer video created successfully!", "#3FB950");
                        Log("Visualizer completed successfully: " + _outputPath);
                        MessageBox.Show(this, $"Video Visualizer generated!\nSaved to: {_outputPath}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Error: " + ex.Message);
                SetStatus("Error generating video", "#F85149");
            }
            finally
            {
                _isProcessing = false;
                GenerateBtn.IsEnabled = true;
                CancelBtn.IsEnabled = false;
                OpenFolderBtn.IsEnabled = !string.IsNullOrEmpty(_lastOutputFolder) || !string.IsNullOrEmpty(_audioPath) || !string.IsNullOrEmpty(_customOutputDir);
                if (_cts?.Token.IsCancellationRequested == true)
                {
                    Log("Cancelled by user.");
                    SetStatus("Cancelled", "#D29922");
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Visualizer error: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            CancelBtn.IsEnabled = false;
        }

        private void SetStatus(string text, string colorHex)
        {
            Dispatcher.Invoke(() => {
                StatusText.Text = text;
                try { StatusDot.Fill = (SolidColorBrush)new BrushConverter().ConvertFromString(colorHex)!; } catch { }
            });
        }

        private void Log(string msg)
        {
            Dispatcher.Invoke(() => {
                LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
                LogBox.ScrollToEnd();
            });
        }
    }
}
