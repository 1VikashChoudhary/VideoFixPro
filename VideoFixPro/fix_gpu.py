import os
import re

FILES_TO_FIX = [
    "ColorGradeWindow.xaml.cs",
    "SpeedStudioWindow.xaml.cs",
    "VideoMergerWindow.xaml.cs",
    "WatermarkWindow.xaml.cs",
    "TrimWindow.xaml.cs",
    "AudioMuxerWindow.xaml.cs"
]

HELPER_CODE = '''
    private static string? _nvCudaDir;
    private static bool _nvCudaDirSearched;

    private static string? FindNvCudaDir()
    {
        if (_nvCudaDirSearched) return _nvCudaDir;
        _nvCudaDirSearched = true;
        try
        {
            var sys32 = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvcuda.dll");
            if (System.IO.File.Exists(sys32)) { _nvCudaDir = System.IO.Path.GetDirectoryName(sys32); return _nvCudaDir; }

            var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
            if (!string.IsNullOrEmpty(cudaPath))
            {
                var cudaBin = System.IO.Path.Combine(cudaPath, "bin", "nvcuda.dll");
                if (System.IO.File.Exists(cudaBin)) { _nvCudaDir = System.IO.Path.GetDirectoryName(cudaBin); return _nvCudaDir; }
            }

            var driverStore = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                           "System32", "DriverStore", "FileRepository");
            if (System.IO.Directory.Exists(driverStore))
            {
                foreach (var pattern in new[] { "nv_disp*", "nvdsp*", "nvlt*", "nvmi*" })
                    foreach (var dir in System.IO.Directory.GetDirectories(driverStore, pattern, System.IO.SearchOption.TopDirectoryOnly))
                        foreach (var name in new[] { "nvcuda64.dll", "nvcuda.dll" })
                            if (System.IO.File.Exists(System.IO.Path.Combine(dir, name))) { _nvCudaDir = dir; return _nvCudaDir; }
            }

            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                       Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                var nvDir = System.IO.Path.Combine(pf, "NVIDIA Corporation");
                if (System.IO.Directory.Exists(nvDir))
                    try { foreach (var f in System.IO.Directory.GetFiles(nvDir, "nvcuda*.dll", System.IO.SearchOption.AllDirectories))
                        { _nvCudaDir = System.IO.Path.GetDirectoryName(f); return _nvCudaDir; } } catch { }
            }
        }
        catch { }
        return null;
    }

    private static void InjectNvCudaPath(System.Diagnostics.ProcessStartInfo psi)
    {
        var nvDir = FindNvCudaDir();
        if (nvDir != null)
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.Environment["PATH"] = nvDir + ";" + currentPath;
        }
    }
'''

for file_name in FILES_TO_FIX:
    if not os.path.exists(file_name):
        continue
    
    with open(file_name, "r", encoding="utf-8") as f:
        content = f.read()

    # 1. Skip if already has FindNvCudaDir
    if "FindNvCudaDir()" in content:
        print(f"Skipping {file_name}, already patched.")
        continue
    
    # 2. Fix Priority
    if "useGpu && _hasAmd ?" in content and "useGpu && _hasNvidia ?" in content:
        # Swap lines
        content = re.sub(
            r'(useGpu && _hasAmd \?[^\n]*\n)(\s*useGpu && _hasNvidia \?[^\n]*\n)',
            r'\2\1',
            content
        )
    
    # 3. Add Helper Code before RunFFmpegAsync / RunFfmpegProcess / RunFFmpegRawAsync
    match = re.search(r'(\s+private (?:async )?(?:Task<bool>|\(bool Success, int ExitCode\)) (?:RunFFmpegAsync|RunFfmpegProcess|RunFFmpegRawAsync)\()', content)
    if match:
        insertion_point = match.start(1)
        content = content[:insertion_point] + "\n" + HELPER_CODE + content[insertion_point:]

    # 4. Inject NvCudaPath after ProcessStartInfo
    # We find ProcessStartInfo psi = ... }; and inject after
    psi_pattern = r'(var\s+psi\s*=\s*new\s+ProcessStartInfo\s*\{[^}]+\}\s*;)'
    
    if file_name == "AudioMuxerWindow.xaml.cs":
        inject_code = r'\1\n        InjectNvCudaPath(psi);'
    else:
        inject_code = r'\1\n        if (_hasNvidia) InjectNvCudaPath(psi);'
    
    # We only want to inject inside the Run method we modified. But replacing all is fine for these files.
    content = re.sub(psi_pattern, inject_code, content, count=1)
    
    with open(file_name, "w", encoding="utf-8") as f:
        f.write(content)
    print(f"Patched {file_name}")

