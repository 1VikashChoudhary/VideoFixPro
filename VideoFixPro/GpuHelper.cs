using System;
using System.Diagnostics;
using System.IO;

namespace VideoFixPro
{
    public static class GpuHelper
    {
        private static string? _nvCudaDir;
        private static bool _nvCudaDirSearched;

        public static string? FindNvCudaDir()
        {
            if (_nvCudaDirSearched) return _nvCudaDir;
            _nvCudaDirSearched = true;
            try
            {
                var sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvcuda.dll");
                if (File.Exists(sys32)) { _nvCudaDir = Path.GetDirectoryName(sys32); return _nvCudaDir; }

                var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
                if (!string.IsNullOrEmpty(cudaPath))
                {
                    var cudaBin = Path.Combine(cudaPath, "bin", "nvcuda.dll");
                    if (File.Exists(cudaBin)) { _nvCudaDir = Path.GetDirectoryName(cudaBin); return _nvCudaDir; }
                }

                var driverStore = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                            "System32", "DriverStore", "FileRepository");
                if (Directory.Exists(driverStore))
                {
                    foreach (var pattern in new[] { "nv_disp*", "nvdsp*", "nvlt*", "nvmi*" })
                        foreach (var dir in Directory.GetDirectories(driverStore, pattern, SearchOption.TopDirectoryOnly))
                            foreach (var name in new[] { "nvcuda64.dll", "nvcuda.dll" })
                                if (File.Exists(Path.Combine(dir, name))) { _nvCudaDir = dir; return _nvCudaDir; }
                }

                foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                {
                    var nvDir = Path.Combine(pf, "NVIDIA Corporation");
                    if (Directory.Exists(nvDir))
                        try { foreach (var f in Directory.GetFiles(nvDir, "nvcuda*.dll", SearchOption.AllDirectories))
                            { _nvCudaDir = Path.GetDirectoryName(f); return _nvCudaDir; } } catch { }
                }
            }
            catch { }
            return _nvCudaDir;
        }

        public static void InjectNvCudaPath(ProcessStartInfo psi)
        {
            var nvDir = FindNvCudaDir();
            if (nvDir != null)
            {
                var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                psi.Environment["PATH"] = nvDir + ";" + currentPath;
            }
        }
    }
}
