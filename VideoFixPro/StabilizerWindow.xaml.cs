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
using Microsoft.Win32;

namespace VideoFixPro
{
    public partial class StabilizerWindow : Window
    {
        private readonly bool _hasNvidia;
        private readonly bool _hasAmd;
        private readonly bool _hasIntel;

        private string? _videoPath;
        private string? _outputPath;
        private string? _customOutputDir;
        private string? _lastOutputFolder;
        private double _durationSeconds;
        private int _sourceWidth;
        private int _sourceHeight;
        private double _videoRotation = 0;
        private CancellationTokenSource? _cts;
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

        public StabilizerWindow(string? preloadPath, bool hasNvidia, bool hasAmd, bool hasIntel)
        {
            InitializeComponent();
            _hasNvidia = hasNvidia;
            _hasAmd = hasAmd;
            _hasIntel = hasIntel;

            if (!string.IsNullOrWhiteSpace(preloadPath) && File.Exists(preloadPath))
            {
                LoadFile(preloadPath);
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
                OpenVideo_Click(sender, e);
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
                if (files.Length > 0) LoadFile(files[0]);
            }
        }

        private void DropZone_Click(object sender, MouseButtonEventArgs e)
        {
            OpenVideo_Click(sender, e);
        }

        private void OpenVideo_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                var res = MessageBox.Show("Stabilization is in progress. Cancel current job and load a new video?",
                                          "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
                _cts?.Cancel();
            }

            var ofd = new OpenFileDialog
            {
                Title = "Select Video to Stabilize",
                Filter = "Video Files|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.flv;*.webm|All Files|*.*"
            };
            if (ofd.ShowDialog() == true) LoadFile(ofd.FileName);
        }

        private void LoadFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            try
            {
                Player.Stop();
                Player.Source = null;
            }
            catch { }

            _videoPath = path;
            UpdateOutputPath();
            
            TitleFileName.Text = Path.GetFileName(path);
            HeaderFileName.Text = Path.GetFileName(path);
            HeaderDuration.Text = "00:00:00";
            FileHeader.Visibility = Visibility.Visible;
            DropZone.Visibility = Visibility.Collapsed;
            PlayerBorder.Visibility = Visibility.Visible;
            ProgressBar.Value = 0;
            ProgressPercentText.Text = "0%";

            try
            {
                Player.Source = new Uri(path);
                Player.Play();
                Player.Pause();
            }
            catch { }

            Task.Run(async () => {
                try {
                    var psi = new ProcessStartInfo {
                        FileName = FFprobe,
                        Arguments = $"-v quiet -print_format json -show_streams -show_format \"{path}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null) {
                        string outStr = await proc.StandardOutput.ReadToEndAsync();
                        await proc.WaitForExitAsync();
                        
                        var root = JsonNode.Parse(outStr);
                        if (root?["format"]?["duration"]?.GetValue<string>() is string durStr &&
                            double.TryParse(durStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                        {
                            _durationSeconds = d;
                            var ts = TimeSpan.FromSeconds(d);
                            string formatted = ts.Hours > 0 ? $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}" : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
                            Dispatcher.Invoke(() => HeaderDuration.Text = formatted);
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
                    }
                } catch { }
            });

            StartBtn.IsEnabled = true;
            OpenFolderBtn.IsEnabled = true;
            SetStatus($"Loaded: {Path.GetFileName(path)}", "#3FB950");
            Log("Loaded: " + Path.GetFileName(path));
        }

        private void RemoveFile_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                var res = MessageBox.Show("Stabilization is in progress. Stop and remove video?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
                _cts?.Cancel();
            }

            try
            {
                Player.Stop();
                Player.Source = null;
            }
            catch { }

            _videoPath = null;
            TitleFileName.Text = "No file loaded";
            FileHeader.Visibility = Visibility.Collapsed;
            PlayerBorder.Visibility = Visibility.Collapsed;
            DropZone.Visibility = Visibility.Visible;
            StartBtn.IsEnabled = false;
            OpenFolderBtn.IsEnabled = !string.IsNullOrEmpty(_lastOutputFolder);
            ProgressBar.Value = 0;
            ProgressPercentText.Text = "0%";
            SetStatus("Ready — Drag & drop a video file to stabilize", "#3FB950");
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
                else if (!string.IsNullOrEmpty(_videoPath) && File.Exists(_videoPath))
                    target = Path.GetDirectoryName(_videoPath);

                if (!string.IsNullOrEmpty(target) && Directory.Exists(target))
                {
                    Process.Start("explorer.exe", target);
                }
                else
                {
                    MessageBox.Show("Output folder not found or no video loaded yet.", "VideoFixPro", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateOutputPath()
        {
            if (string.IsNullOrEmpty(_videoPath)) return;
            string dir = !string.IsNullOrEmpty(_customOutputDir) ? _customOutputDir : (Path.GetDirectoryName(_videoPath) ?? "");
            string name = Path.GetFileNameWithoutExtension(_videoPath) + "_stabilized.mp4";
            _outputPath = Path.Combine(dir, name);
            OutputDirBox.Text = !string.IsNullOrEmpty(_customOutputDir) ? _customOutputDir : "Same as source file";
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

        private void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
            ApplyPlayerDimensionsAndRotation();
        }

        private void Player_MediaEnded(object sender, RoutedEventArgs e)
        {
            Player.Position = TimeSpan.Zero;
            Player.Play();
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_videoPath) || string.IsNullOrWhiteSpace(_outputPath)) return;

            _isProcessing = true;
            StartBtn.IsEnabled = false;
            CancelBtn.IsEnabled = true;
            _cts = new CancellationTokenSource();

            int shakiness = (int)ShakinessSlider.Value;
            int smoothing = (int)SmoothingSlider.Value;
            int zoomMode = ZoomCheck.IsChecked == true ? 1 : 0;

            string trfFile = Path.Combine(Path.GetTempPath(), $"transform_{Guid.NewGuid():N}.trf");
            ProgressBar.Visibility = Visibility.Visible;
            ProgressPercentText.Visibility = Visibility.Visible;
            SetStatus("Pass 1/2: Analyzing camera movement...", "#388BFD");
            
            try
            {
                Log("[PASS 1] Analyzing camera motion vectors...");
                ProgressBar.Value = 0;
                string escapedTrf1 = trfFile.Replace("\\", "/").Replace(":", "\\:").Replace("'", @"'\''");
                string pass1Args = $"-y -i \"{_videoPath}\" -vf vidstabdetect=shakiness={shakiness}:result='{escapedTrf1}' -f null -";
                
                bool p1 = await RunFFmpegAsync(pass1Args, _durationSeconds, _cts.Token, 1);
                
                if (p1 && !_cts.Token.IsCancellationRequested)
                {
                    Log("[PASS 2] Applying stabilization transforms & encoding...");
                    SetStatus("Pass 2/2: Applying transforms & rendering...", "#388BFD");
                    ProgressBar.Value = 50;

                    string escapedTrf = trfFile.Replace("\\", "/").Replace(":", "\\:").Replace("'", @"'\''");
                    
                    bool isAv1 = Av1Check.IsChecked == true;
                    string vCodecArgs;
                    if (isAv1)
                    {
                        vCodecArgs = "-c:v libsvtav1 -preset 8 -crf 22 -pix_fmt yuv420p";
                        if (GpuCheck.IsChecked == true)
                        {
                            if (_hasNvidia) vCodecArgs = "-c:v av1_nvenc -preset p5 -cq 22 -pix_fmt yuv420p";
                            else if (_hasAmd) vCodecArgs = "-c:v av1_amf -qp_i 22 -qp_p 22 -qp_b 22 -pix_fmt yuv420p";
                            else if (_hasIntel) vCodecArgs = "-c:v av1_qsv -global_quality 22 -pix_fmt nv12";
                        }
                    }
                    else
                    {
                        vCodecArgs = "-c:v libx264 -preset fast -crf 20 -pix_fmt yuv420p";
                        if (GpuCheck.IsChecked == true)
                        {
                            if (_hasNvidia) vCodecArgs = "-c:v h264_nvenc -preset p4 -cq 22 -pix_fmt yuv420p";
                            else if (_hasAmd) vCodecArgs = "-c:v h264_amf -qp_i 22 -qp_p 22 -qp_b 22 -pix_fmt yuv420p";
                            else if (_hasIntel) vCodecArgs = "-c:v h264_qsv -global_quality 22 -pix_fmt nv12";
                        }
                    }

                    string pass2Args = $"-y -i \"{_videoPath}\" -vf vidstabtransform=input='{escapedTrf}':smoothing={smoothing}:zoom={zoomMode} {vCodecArgs} -c:a aac -b:a 192k \"{_outputPath}\"";
                    
                    bool p2 = await RunFFmpegAsync(pass2Args, _durationSeconds, _cts.Token, 2);
                    if (p2 && !_cts.Token.IsCancellationRequested)
                    {
                        _lastOutputFolder = Path.GetDirectoryName(_outputPath);
                        OpenFolderBtn.IsEnabled = true;
                        Log("Stabilization completed successfully!");
                        ProgressBar.Value = 100;
                        ProgressPercentText.Text = "100%";
                        SetStatus("Stabilization complete!", "#3FB950");
                        MessageBox.Show(this, $"Stabilization complete!\nSaved to: {_outputPath}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Error: " + ex.Message);
                SetStatus("Error during stabilization", "#F85149");
            }
            finally
            {
                if (File.Exists(trfFile))
                {
                    try { File.Delete(trfFile); } catch { }
                }
                _isProcessing = false;
                StartBtn.IsEnabled = true;
                CancelBtn.IsEnabled = false;
                OpenFolderBtn.IsEnabled = !string.IsNullOrEmpty(_lastOutputFolder) || !string.IsNullOrEmpty(_videoPath) || !string.IsNullOrEmpty(_customOutputDir);
                if (_cts?.Token.IsCancellationRequested == true)
                {
                    Log("Operation cancelled.");
                    SetStatus("Cancelled", "#D29922");
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Stabilizer error: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            CancelBtn.IsEnabled = false;
        }

        private async Task<bool> RunFFmpegAsync(string args, double durationSeconds, CancellationToken token, int pass)
        {
            var tcs = new TaskCompletionSource<bool>();
            var psi = new ProcessStartInfo
            {
                FileName = FFmpeg,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            if (_hasNvidia) GpuHelper.InjectNvCudaPath(psi);
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            ProcessGuard.Watch(process);

            var timeRegex = new Regex(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                
                var match = timeRegex.Match(e.Data);
                if (match.Success && durationSeconds > 0)
                {
                    if (int.TryParse(match.Groups[1].Value, out int h) &&
                        int.TryParse(match.Groups[2].Value, out int m) &&
                        double.TryParse(match.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double sec))
                    {
                        var ts = new TimeSpan(0, h, m, (int)sec);
                        double progress = (ts.TotalSeconds / durationSeconds) * 50.0;
                        if (progress > 50) progress = 50;
                        
                        double totalProgress = (pass == 1 ? progress : 50 + progress);
                        Dispatcher.Invoke(() => {
                            ProgressBar.Value = totalProgress;
                            ProgressPercentText.Text = $"{(int)totalProgress}%";
                        });
                    }
                }
            };

            process.Exited += (s, e) =>
            {
                tcs.TrySetResult(process.ExitCode == 0);
            };

            try
            {
                process.Start();
                process.BeginErrorReadLine();

                await using (token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    tcs.TrySetResult(false);
                }))
                {
                    return await tcs.Task;
                }
            }
            catch { return false; }
            finally
            {
                ProcessGuard.Unwatch(process);
            }
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

