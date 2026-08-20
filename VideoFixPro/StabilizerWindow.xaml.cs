using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace VideoFixPro
{
    public partial class StabilizerWindow : Window
    {
        private readonly bool _hasNvidia;
        private readonly bool _hasAmd;
        private readonly bool _hasIntel;

        private string? _videoPath;
        private string? _outputPath;
        private double _durationSeconds;
        private CancellationTokenSource? _cts;
        private bool _isProcessing;

        private static string AppDir => AppDomain.CurrentDomain.BaseDirectory;
        private static string FFmpeg => GetBinPath("ffmpeg.exe");
        private static string FFprobe => GetBinPath("ffprobe.exe");

        
        private static string? _nvCudaDir;
        private static bool _nvCudaDirSearched;
        private static string? FindNvCudaDir()
        {
            if (_nvCudaDirSearched) return _nvCudaDir;
            _nvCudaDirSearched = true;
            try
            {
                var sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvcuda.dll");
                if (File.Exists(sys32)) { _nvCudaDir = Path.GetDirectoryName(sys32); return _nvCudaDir; }
                
                var driverStore = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                            "System32", "DriverStore", "FileRepository");
                if (Directory.Exists(driverStore))
                {
                    foreach (var pattern in new[] { "nv_disp*", "nvdsp*", "nvlt*", "nvmi*" })
                        foreach (var dir in Directory.GetDirectories(driverStore, pattern, SearchOption.TopDirectoryOnly))
                            foreach (var name in new[] { "nvcuda64.dll", "nvcuda.dll" })
                                if (File.Exists(Path.Combine(dir, name))) { _nvCudaDir = dir; return _nvCudaDir; }
                }
            }
            catch { }
            return null;
        }

        private static void InjectNvCudaPath(ProcessStartInfo psi)
        {
            var nvDir = FindNvCudaDir();
            if (nvDir != null)
            {
                var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                psi.Environment["PATH"] = nvDir + ";" + currentPath;
            }
        }

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

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (_isProcessing) return;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length > 0)
                {
                    LoadFile(files[0]);
                }
            }
        }

        private void LoadFile(string path)
        {
            _videoPath = path;
            _outputPath = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + "_stabilized.mp4");
            
            DropZone.Visibility = Visibility.Collapsed;
            PlayerBorder.Visibility = Visibility.Visible;
            Player.Source = new Uri(path);
            Player.Play();
            Player.Pause();

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
            Log("Loaded: " + Path.GetFileName(path));
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
            
            try
            {
                Log("[PASS 1] Analyzing shaky camera movement...");
                ProgressBar.Value = 0;
                string escapedTrf1 = trfFile.Replace("\\", "/").Replace(":", "\\:");
                string pass1Args = $"-y -i \"{_videoPath}\" -vf vidstabdetect=shakiness={shakiness}:result='{escapedTrf1}' -f null -";
                
                bool p1 = await RunFFmpegAsync(pass1Args, _durationSeconds, _cts.Token, 1);
                
                if (p1 && !_cts.Token.IsCancellationRequested)
                {
                    Log("[PASS 2] Applying stabilization and encoding...");
                    ProgressBar.Value = 50;

                    string escapedTrf = trfFile.Replace("\\", "/").Replace(":", "\\:");
                    
                    bool isAv1 = Av1Check.IsChecked == true;
                    string vCodecArgs;
                    if (isAv1)
                    {
                        vCodecArgs = "-c:v libsvtav1 -preset 8 -crf 22";
                        if (GpuCheck.IsChecked == true)
                        {
                            if (_hasNvidia) vCodecArgs = "-c:v av1_nvenc -preset p5 -cq 22";
                            else if (_hasAmd) vCodecArgs = "-c:v av1_amf -qp_i 22 -qp_p 22 -qp_b 22";
                            else if (_hasIntel) vCodecArgs = "-c:v av1_qsv -global_quality 22";
                        }
                    }
                    else
                    {
                        vCodecArgs = "-c:v libx264 -preset fast -crf 20";
                        if (GpuCheck.IsChecked == true)
                        {
                            if (_hasNvidia) vCodecArgs = "-c:v h264_nvenc -preset p4 -cq 22";
                            else if (_hasAmd) vCodecArgs = "-c:v h264_amf -qp_i 22 -qp_p 22 -qp_b 22";
                            else if (_hasIntel) vCodecArgs = "-c:v h264_qsv -global_quality 22";
                        }
                    }

                    string pass2Args = $"-y -i \"{_videoPath}\" -vf vidstabtransform=input='{escapedTrf}':smoothing={smoothing}:zoom={zoomMode} {vCodecArgs} -c:a copy \"{_outputPath}\"";
                    
                    bool p2 = await RunFFmpegAsync(pass2Args, _durationSeconds, _cts.Token, 2);
                    if (p2 && !_cts.Token.IsCancellationRequested)
                    {
                        Log("Stabilization completed successfully!");
                        ProgressBar.Value = 100;
                        MessageBox.Show(this, "Stabilization complete!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Error: " + ex.Message);
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
                if (_cts.Token.IsCancellationRequested) Log("Operation cancelled.");
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

            if (_hasNvidia) InjectNvCudaPath(psi);
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            ProcessGuard.Watch(process);

            var timeRegex = new Regex(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                
                var match = timeRegex.Match(e.Data);
                if (match.Success && durationSeconds > 0)
                {
                    var ts = new TimeSpan(0, int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), (int)double.Parse(match.Groups[3].Value));
                    double progress = (ts.TotalSeconds / durationSeconds) * 50.0;
                    if (progress > 50) progress = 50;
                    
                    double totalProgress = (pass == 1 ? progress : 50 + progress);
                    Dispatcher.Invoke(() => ProgressBar.Value = totalProgress);
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

        private void Log(string msg)
        {
            Dispatcher.Invoke(() => {
                LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
                LogBox.ScrollToEnd();
            });
        }
    }
}
