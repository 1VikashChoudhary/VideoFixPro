using System;
using System.Diagnostics;
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
    public partial class StabilizerWindow : Window
    {
        private readonly bool _hasNvidia;
        private readonly bool _hasAmd;
        private readonly bool _hasIntel;

        private string? _videoPath;
        private string? _outputPath;
        private string? _customOutputDir;
        private double _durationSeconds;
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
            if (_isProcessing) return;
            var ofd = new OpenFileDialog
            {
                Title = "Select Video to Stabilize",
                Filter = "Video Files|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.flv;*.webm|All Files|*.*"
            };
            if (ofd.ShowDialog() == true) LoadFile(ofd.FileName);
        }

        private void LoadFile(string path)
        {
            _videoPath = path;
            UpdateOutputPath();
            
            TitleFileName.Text = Path.GetFileName(path);
            DropZone.Visibility = Visibility.Collapsed;
            PlayerBorder.Visibility = Visibility.Visible;
            try
            {
                Player.Source = new Uri(path);
                Player.Play();
                Player.Pause();
            }
            catch { }

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
                        string outStr = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit();
                        if (double.TryParse(outStr.Trim(), out double d)) _durationSeconds = d;
                    }
                } catch { }
            });

            StartBtn.IsEnabled = true;
            SetStatus($"Loaded: {Path.GetFileName(path)}", "#3FB950");
            Log("Loaded: " + Path.GetFileName(path));
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

        private void Player_MediaEnded(object sender, RoutedEventArgs e)
        {
            Player.Position = TimeSpan.Zero;
            Player.Play();
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
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

                    string pass2Args = $"-y -i \"{_videoPath}\" -vf vidstabtransform=input='{escapedTrf}':smoothing={smoothing}:zoom={zoomMode} {vCodecArgs} -c:a copy \"{_outputPath}\"";
                    
                    bool p2 = await RunFFmpegAsync(pass2Args, _durationSeconds, _cts.Token, 2);
                    if (p2 && !_cts.Token.IsCancellationRequested)
                    {
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
                if (_cts?.Token.IsCancellationRequested == true)
                {
                    Log("Operation cancelled.");
                    SetStatus("Cancelled", "#D29922");
                }
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
                        double.TryParse(match.Groups[3].Value, out double sec))
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

            process.Start();
            process.BeginErrorReadLine();

            await using (token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
                tcs.TrySetResult(false);
            }))
            {
                bool success = await tcs.Task;
                ProcessGuard.Unwatch(process);
                return success;
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

