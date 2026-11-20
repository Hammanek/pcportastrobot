using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AstroHybrid.App.Models;

namespace AstroHybrid.App.Services
{
    public class LauncherService
    {
        private readonly string _rootDirectory;
        private readonly SettingsManager _settingsManager;

        public LauncherService(string rootDirectory)
        {
            _rootDirectory = rootDirectory;
            _settingsManager = new SettingsManager(rootDirectory);
        }

        private void AppendDebugLog(string message)
        {
            try
            {
                var logPath = Path.Combine(_rootDirectory, "Logs", "astrohybrid-debug.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
            }
            catch { }
        }

        public async Task<int> LaunchGameAsync(AppSettings settings, HardwareInfo? hardware = null)
        {
            var batPath = Path.Combine(_rootDirectory, "Run-AstroHybrid.bat");
            if (!File.Exists(batPath))
            {
                throw new FileNotFoundException("Run-AstroHybrid.bat not found", batPath);
            }

            var runHybridPath = Path.Combine(_rootDirectory, "Build", "RunHybrid");
            if (!File.Exists(Path.Combine(runHybridPath, "AstroBot.exe")))
            {
                throw new FileNotFoundException("AstroBot.exe not found in RunHybrid", runHybridPath);
            }

            // Pre-launch validation: check for common issues that cause immediate crashes
            var validationResult = await ValidateLaunchEnvironmentAsync(runHybridPath, settings, hardware);
            if (!validationResult.IsValid)
            {
                throw new InvalidOperationException(validationResult.ErrorMessage);
            }

            // Save settings before launch
            settings.LastLaunched = DateTime.Now;
            _settingsManager.Save(settings);

            // Clear shader cache on startup if enabled (#161, #157)
            if (settings.ClearShaderCacheOnStartup)
            {
                ClearShaderCache(runHybridPath);
            }

            // Build environment variables from settings
            var envVars = BuildEnvironmentVariables(settings, hardware);

            // Add crash diagnostics environment variables
            envVars["ASTROHYBRID_CRASH_DIAGNOSTICS"] = "1";
            envVars["ASTROHYBRID_LAUNCH_TIME"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batPath}\"",
                WorkingDirectory = _rootDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var kvp in envVars)
            {
                psi.EnvironmentVariables[kvp.Key] = kvp.Value;
            }

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var tcs = new TaskCompletionSource<int>();

            process.Exited += (sender, e) =>
            {
                tcs.SetResult(process.ExitCode);
            };

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    System.Diagnostics.Debug.WriteLine($"[OUT] {e.Data}");
                    AppendDebugLog($"[GAME OUT] {e.Data}");
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    System.Diagnostics.Debug.WriteLine($"[ERR] {e.Data}");
                    AppendDebugLog($"[GAME ERR] {e.Data}");
                }
            };

            try
            {
                process.Start();
                AppendDebugLog($"Game process started (PID: {process.Id})");
            }
            catch (Exception ex)
            {
                AppendDebugLog($"Failed to start game process: {ex.Message}");
                throw new InvalidOperationException($"Failed to start game: {ex.Message}", ex);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Add timeout for process startup (detect immediate crashes)
            var startupTimeout = Task.Delay(TimeSpan.FromSeconds(30));
            var completedTask = await Task.WhenAny(tcs.Task, startupTimeout);
            
            if (completedTask == startupTimeout)
            {
                // Process hasn't exited but startup is taking too long - this is normal for first launch
                AppendDebugLog("Game startup taking longer than 30s, continuing to wait...");
            }

            return await tcs.Task;
        }

        private async Task<LaunchValidationResult> ValidateLaunchEnvironmentAsync(string runHybridPath, AppSettings settings, HardwareInfo? hardware)
        {
            var errors = new List<string>();

            // Check for required DLLs
            var requiredDlls = new[] { "kyty_gpu.dll", "astrohybrid_host.dll" };
            foreach (var dll in requiredDlls)
            {
                if (!File.Exists(Path.Combine(runHybridPath, dll)))
                {
                    errors.Add($"Required DLL not found: {dll}");
                }
            }

            // Check Vulkan support
            if (!IsVulkanSupported())
            {
                errors.Add("Vulkan is not supported on this system. Please update GPU drivers.");
            }

            // Check for mesh shader support (required for Astro Bot)
            if (!HasMeshShaderSupport())
            {
                errors.Add("GPU does not support VK_EXT_mesh_shader (required by Astro Bot). Minimum: RTX 20 series, RX 6000 series, or Intel Arc.");
            }

            // Validate GPU selection if explicit
            if (hardware != null && hardware.SelectedGpuIndex >= 0)
            {
                var gpu = hardware.SelectedGpu;
                if (gpu != null && !IsGpuAvailable(gpu.Name))
                {
                    errors.Add($"Selected GPU '{gpu.Name}' is not available. Please select 'Automatic' or a different GPU.");
                }
            }

            // Check for NVIDIA DLSS/Frame Gen requirements
            if (settings.Upscale != 0 || settings.FrameGenMode != 0)
            {
                if (!File.Exists(Path.Combine(runHybridPath, "nvngx_dlss.dll")))
                {
                    errors.Add("DLSS runtime not found (nvngx_dlss.dll). Please ensure NVIDIA drivers are installed and DLSS runtime is in the game folder.");
                }
                if (settings.FrameGenMode != 0 && !File.Exists(Path.Combine(runHybridPath, "nvngx_dlssg.dll")))
                {
                    errors.Add("DLSS Frame Generation runtime not found (nvngx_dlssg.dll). Frame Generation requires RTX 40/50 series and latest NVIDIA drivers.");
                }
            }

            // Check disk space
            var driveInfo = new DriveInfo(Path.GetPathRoot(runHybridPath)!);
            if (driveInfo.AvailableFreeSpace < 1024 * 1024 * 1024) // 1 GB
            {
                errors.Add("Insufficient disk space (need at least 1 GB free).");
            }

            // Check Windows version (Windows 10 1809+ required for Vulkan mesh shaders)
            if (!IsWindowsVersionSupported())
            {
                errors.Add("Windows 10 version 1809 (build 17763) or later is required for Vulkan mesh shader support.");
            }

            // Check for administrator privileges (warn but don't block)
            if (!IsRunningAsAdmin())
            {
                // Just log a warning, don't add to errors
                AppendDebugLog("Warning: Not running as administrator. Some features may not work correctly.");
            }

            // Check for known antivirus interference
            if (IsAntivirusInterfering())
            {
                errors.Add("Antivirus software may be blocking game files. Please add the AstroHybrid folder to your antivirus exclusions.");
            }

            // Check GPU memory
            if (hardware != null && hardware.SelectedGpu != null)
            {
                if (!HasSufficientGpuMemory(hardware.SelectedGpu))
                {
                    errors.Add($"GPU '{hardware.SelectedGpu.Name}' may have insufficient VRAM. Minimum 6 GB recommended, 8 GB+ for ultrawide/DLSS.");
                }
            }

            // Check audio drivers
            if (!HasWorkingAudioDevice())
            {
                errors.Add("No working audio output device detected. Please ensure audio drivers are installed and a playback device is enabled.");
            }

            // Check Vulkan version and mesh shader support
            if (!ValidateVulkanSupport())
            {
                errors.Add("Vulkan 1.2+ with mesh shader support (VK_EXT_mesh_shader) is required. Please update GPU drivers.");
            }

            // Check for known problematic driver versions
            if (HasProblematicDriver(hardware?.SelectedGpu))
            {
                errors.Add("Known problematic GPU driver detected. Please update to the latest driver version.");
            }

            // Check DirectX support (for fallback)
            if (!HasDirectXSupport())
            {
                // Warning only, not blocking
                AppendDebugLog("Warning: DirectX 12 not fully supported, may affect compatibility layer");
            }

            // Collect system info for crash diagnostics
            CollectSystemInfoForDiagnostics();

            if (errors.Count > 0)
            {
                return new LaunchValidationResult { IsValid = false, ErrorMessage = "Launch validation failed:\n" + string.Join("\n", errors) };
            }

            return new LaunchValidationResult { IsValid = true };
        }

        private bool ValidateVulkanSupport()
        {
            try
            {
                // Check for vulkan-1.dll and try to query version
                var vulkanPath = Path.Combine(Environment.SystemDirectory, "vulkan-1.dll");
                if (!File.Exists(vulkanPath)) return false;

                // Additional check: try to load and query instance version
                // This would require P/Invoke to vkEnumerateInstanceVersion
                // For now, just check file exists
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool HasProblematicDriver(HardwareInfo.GpuInfo? gpu)
        {
            if (gpu == null) return false;
            
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT DriverVersion, DriverDate FROM Win32_VideoController WHERE Name LIKE '%" + gpu.Name.Replace("'", "''") + "%'");
                foreach (var obj in searcher.Get())
                {
                    var driverVersion = obj["DriverVersion"]?.ToString() ?? "";
                    var driverDate = obj["DriverDate"]?.ToString() ?? "";
                    
                    // Log driver info for debugging
                    AppendDebugLog($"GPU Driver: {driverVersion} (Date: {driverDate})");
                    
                    // Check for known problematic versions
                    // This is a placeholder - actual versions would need to be researched
                    if (driverVersion.Contains("30.0.14") || driverVersion.Contains("30.0.15"))
                    {
                        // Some older NVIDIA drivers had issues with Vulkan mesh shaders
                        AppendDebugLog($"Potentially problematic driver version detected: {driverVersion}");
                    }
                }
            }
            catch { }
            return false;
        }

        private bool HasDirectXSupport()
        {
            try
            {
                // Check for DirectX 12 support
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, DriverVersion FROM Win32_VideoController");
                foreach (var obj in searcher.Get())
                {
                    var name = obj["Name"]?.ToString() ?? "";
                    if (name.Contains("NVIDIA") || name.Contains("AMD") || name.Contains("Intel"))
                    {
                        return true; // Assume modern GPUs support DX12
                    }
                }
            }
            catch { }
            return false;
        }

        private void CollectSystemInfoForDiagnostics()
        {
            try
            {
                var infoPath = Path.Combine(_rootDirectory, "Logs", "system-info.json");
                Directory.CreateDirectory(Path.GetDirectoryName(infoPath)!);
                
                var systemInfo = new SystemInfo
                {
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    OSVersion = Environment.OSVersion.Version.ToString(),
                    OSBuild = Environment.OSVersion.Version.Build,
                    Is64BitOS = Environment.Is64BitOperatingSystem,
                    Is64BitProcess = Environment.Is64BitProcess,
                    ProcessorCount = Environment.ProcessorCount,
                    TotalMemoryMB = GetTotalMemoryMB(),
                    AvailableMemoryMB = GetAvailableMemoryMB(),
                    GPUDevices = GetGpuDevices(),
                    DriverVersions = GetDriverVersions(),
                    VulkanInfo = GetVulkanInfo(),
                    DirectXInfo = GetDirectXInfo(),
                    InstalledAntivirus = GetInstalledAntivirus(),
                    PowerPlan = GetActivePowerPlan()
                };
                
                var json = System.Text.Json.JsonSerializer.Serialize(systemInfo, new System.Text.Json.JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
                File.WriteAllText(infoPath, json);
                AppendDebugLog($"System info collected for diagnostics: {infoPath}");
            }
            catch (Exception ex)
            {
                AppendDebugLog($"Failed to collect system info: {ex.Message}");
            }
        }

        private long GetTotalMemoryMB()
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (var obj in searcher.Get())
                {
                    if (obj["TotalPhysicalMemory"] != null)
                    {
                        return Convert.ToInt64(obj["TotalPhysicalMemory"]) / 1024 / 1024;
                    }
                }
            }
            catch { }
            return 0;
        }

        private long GetAvailableMemoryMB()
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT FreePhysicalMemory FROM Win32_OperatingSystem");
                foreach (var obj in searcher.Get())
                {
                    if (obj["FreePhysicalMemory"] != null)
                    {
                        return Convert.ToInt64(obj["FreePhysicalMemory"]) / 1024;
                    }
                }
            }
            catch { }
            return 0;
        }

        private List<GpuDeviceInfo> GetGpuDevices()
        {
            var devices = new List<GpuDeviceInfo>();
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, AdapterRAM, DriverVersion, DriverDate, VideoProcessor FROM Win32_VideoController");
                foreach (var obj in searcher.Get())
                {
                    devices.Add(new GpuDeviceInfo
                    {
                        Name = obj["Name"]?.ToString() ?? "Unknown",
                        AdapterRAM_MB = obj["AdapterRAM"] != null ? Convert.ToInt64(obj["AdapterRAM"]) / 1024 / 1024 : 0,
                        DriverVersion = obj["DriverVersion"]?.ToString() ?? "",
                        DriverDate = obj["DriverDate"]?.ToString() ?? "",
                        VideoProcessor = obj["VideoProcessor"]?.ToString() ?? ""
                    });
                }
            }
            catch { }
            return devices;
        }

        private List<string> GetDriverVersions()
        {
            var versions = new List<string>();
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, DriverVersion FROM Win32_VideoController");
                foreach (var obj in searcher.Get())
                {
                    var name = obj["Name"]?.ToString() ?? "Unknown";
                    var version = obj["DriverVersion"]?.ToString() ?? "Unknown";
                    versions.Add($"{name}: {version}");
                }
            }
            catch { }
            return versions;
        }

        private string GetVulkanInfo()
        {
            try
            {
                var vulkanPath = Path.Combine(Environment.SystemDirectory, "vulkan-1.dll");
                if (File.Exists(vulkanPath))
                {
                    var versionInfo = FileVersionInfo.GetVersionInfo(vulkanPath);
                    return $"vulkan-1.dll: {versionInfo.FileVersion}";
                }
            }
            catch { }
            return "Not found";
        }

        private string GetDirectXInfo()
        {
            // Would need DxDiag or similar to get detailed DX info
            return "DirectX 12 (assumed on Windows 10+)";
        }

        private List<string> GetInstalledAntivirus()
        {
            var avList = new List<string>();
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT displayName FROM AntivirusProduct");
                foreach (var obj in searcher.Get())
                {
                    var name = obj["displayName"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(name)) avList.Add(name);
                }
            }
            catch { }
            return avList;
        }

        private string GetActivePowerPlan()
        {
            try
            {
                // Query active power plan
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "powercfg",
                    Arguments = "/getactivescheme",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                process?.WaitForExit(2000);
                if (process?.ExitCode == 0)
                {
                    return process.StandardOutput.ReadToEnd().Trim();
                }
            }
            catch { }
            return "Unknown";
        }

        // System info classes for JSON serialization
        private class SystemInfo
        {
            public string Timestamp { get; set; } = "";
            public string OSVersion { get; set; } = "";
            public int OSBuild { get; set; }
            public bool Is64BitOS { get; set; }
            public bool Is64BitProcess { get; set; }
            public int ProcessorCount { get; set; }
            public long TotalMemoryMB { get; set; }
            public long AvailableMemoryMB { get; set; }
            public List<GpuDeviceInfo> GPUDevices { get; set; } = new();
            public List<string> DriverVersions { get; set; } = new();
            public string VulkanInfo { get; set; } = "";
            public string DirectXInfo { get; set; } = "";
            public List<string> InstalledAntivirus { get; set; } = new();
            public string PowerPlan { get; set; } = "";
        }

        private class GpuDeviceInfo
        {
            public string Name { get; set; } = "";
            public long AdapterRAM_MB { get; set; }
            public string DriverVersion { get; set; } = "";
            public string DriverDate { get; set; } = "";
            public string VideoProcessor { get; set; } = "";
        }

        private bool IsWindowsVersionSupported()
        {
            try
            {
                var version = Environment.OSVersion.Version;
                // Windows 10 build 17763 (1809) or later
                return version.Major > 10 || (version.Major == 10 && version.Build >= 17763);
            }
            catch
            {
                return false;
            }
        }

        private bool IsRunningAsAdmin()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private bool IsAntivirusInterfering()
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT displayName FROM AntivirusProduct");
                foreach (var obj in searcher.Get())
                {
                    var name = obj["displayName"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(name))
                    {
                        // Log detected antivirus for debugging
                        AppendDebugLog($"Detected antivirus: {name}");
                        // We don't block on this, just warn
                    }
                }
            }
            catch { }
            return false; // Don't block, just log
        }

        private bool HasSufficientGpuMemory(HardwareInfo.GpuInfo gpu)
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT AdapterRAM FROM Win32_VideoController WHERE Name LIKE '%" + gpu.Name.Replace("'", "''") + "%'");
                foreach (var obj in searcher.Get())
                {
                    if (obj["AdapterRAM"] != null)
                    {
                        var vramBytes = Convert.ToInt64(obj["AdapterRAM"]);
                        var vramGB = vramBytes / 1024 / 1024 / 1024;
                        return vramGB >= 6; // Minimum 6 GB
                    }
                }
            }
            catch { }
            return true; // Assume sufficient if we can't detect
        }

        private bool HasWorkingAudioDevice()
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name, Status FROM Win32_SoundDevice WHERE Status = 'OK'");
                foreach (var obj in searcher.Get())
                {
                    var status = obj["Status"]?.ToString() ?? "";
                    if (status == "OK")
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false; // No working audio device found
        }

        private void ClearShaderCache(string runHybridPath)
        {
            try
            {
                var cacheFiles = new[]
                {
                    Path.Combine(runHybridPath, "shader_cache.bin"),
                    Path.Combine(runHybridPath, "pipeline_cache.bin"),
                    Path.Combine(runHybridPath, "cache")
                };

                foreach (var cachePath in cacheFiles)
                {
                    if (File.Exists(cachePath))
                    {
                        File.Delete(cachePath);
                        AppendDebugLog($"Cleared shader cache file: {cachePath}");
                    }
                    else if (Directory.Exists(cachePath))
                    {
                        Directory.Delete(cachePath, true);
                        AppendDebugLog($"Cleared shader cache directory: {cachePath}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendDebugLog($"Failed to clear shader cache: {ex.Message}");
            }
        }

        private bool IsVulkanSupported()
        {
            try
            {
                // Try to load vulkan-1.dll
                var vulkanPath = Path.Combine(Environment.SystemDirectory, "vulkan-1.dll");
                return File.Exists(vulkanPath);
            }
            catch
            {
                return false;
            }
        }

        private bool HasMeshShaderSupport()
        {
            // This is a heuristic - actual mesh shader support requires Vulkan query
            // For now, check if we have a reasonably modern GPU
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
                foreach (var obj in searcher.Get())
                {
                    var name = obj["Name"]?.ToString() ?? "";
                    if (name.Contains("RTX") || name.Contains("RX 6") || name.Contains("RX 7") || 
                        name.Contains("Arc") || name.Contains("RDNA 2") || name.Contains("RDNA 3"))
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false; // Conservative: assume no support if we can't detect
        }

        private bool IsGpuAvailable(string gpuName)
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
                foreach (var obj in searcher.Get())
                {
                    var name = obj["Name"]?.ToString() ?? "";
                    if (name.Contains(gpuName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private class LaunchValidationResult
        {
            public bool IsValid { get; set; }
            public string ErrorMessage { get; set; } = "";
        }

        /// <summary>
        /// Generates a comprehensive crash report with system information, logs, and environment details.
        /// </summary>
        public async Task<string> GenerateCrashReportAsync(int exitCode, string? additionalInfo = null)
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("=== AstroHybrid Crash Report ===");
            report.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"Exit Code: {exitCode} ({GetExitCodeDescription(exitCode)})");
            report.AppendLine();

            // System Information
            report.AppendLine("--- System Information ---");
            try
            {
                report.AppendLine($"OS: {Environment.OSVersion}");
                report.AppendLine($".NET Framework: {Environment.Version}");
                report.AppendLine($"Process Architecture: {(Environment.Is64BitProcess ? "x64" : "x86")}");
                report.AppendLine($"OS Architecture: {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}");
                report.AppendLine($"Processor Count: {Environment.ProcessorCount}");
                report.AppendLine($"Working Set: {Environment.WorkingSet / 1024 / 1024} MB");
            }
            catch (Exception ex)
            {
                report.AppendLine($"Failed to collect system info: {ex.Message}");
            }
            report.AppendLine();

            // GPU Information
            report.AppendLine("--- GPU Information ---");
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController");
                foreach (var obj in searcher.Get())
                {
                    var name = obj["Name"]?.ToString() ?? "Unknown";
                    var driver = obj["DriverVersion"]?.ToString() ?? "Unknown";
                    var vram = obj["AdapterRAM"] != null ? $"{Convert.ToInt64(obj["AdapterRAM"]) / 1024 / 1024 / 1024} GB" : "Unknown";
                    report.AppendLine($"GPU: {name} | Driver: {driver} | VRAM: {vram}");
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"Failed to collect GPU info: {ex.Message}");
            }
            report.AppendLine();

            // Environment Variables
            report.AppendLine("--- Relevant Environment Variables ---");
            var relevantVars = new[] { "ASTROHYBRID_TDR_WORKAROUND", "ASTROHYBRID_PHYSICS_SUBSTEP", "ASTROHYBRID_FAST_LOAD", 
                "ASTROHYBRID_SHADER_PRECOMPILE", "ASTROHYBRID_PIPELINE_CACHE_WARMUP", "ASTROHYBRID_VRAM_LIMIT_MB",
                "ASTROHYBRID_DLSS", "ASTROHYBRID_DLSSG", "ASTROHYBRID_CUDA_HOLE", "ANYPS5_GPU", "APS5_GPU_ENGINE",
                "KYTY_HANG_WATCHDOG", "ASTROHYBRID_HANG_WATCHDOG" };
            foreach (var var in relevantVars)
            {
                var value = Environment.GetEnvironmentVariable(var);
                if (!string.IsNullOrEmpty(value))
                {
                    report.AppendLine($"{var}={value}");
                }
            }
            report.AppendLine();

            // Log Files
            report.AppendLine("--- Log Files (last 100 lines each) ---");
            var logFiles = new[] { "astrohybrid-debug.log", "hybrid-run.out.log", "hybrid-run.err.log" };
            foreach (var logFile in logFiles)
            {
                var logPath = Path.Combine(_rootDirectory, "Logs", logFile);
                if (File.Exists(logPath))
                {
                    report.AppendLine($"--- {logFile} ---");
                    try
                    {
                        var lines = await File.ReadAllLinesAsync(logPath);
                        var lastLines = lines.Skip(Math.Max(0, lines.Length - 100));
                        foreach (var line in lastLines)
                        {
                            report.AppendLine(line);
                        }
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine($"Failed to read log: {ex.Message}");
                    }
                    report.AppendLine();
                }
                else
                {
                    report.AppendLine($"{logFile}: NOT FOUND");
                    report.AppendLine();
                }
            }

            // Settings
            report.AppendLine("--- Current Settings ---");
            try
            {
                var settings = _settingsManager.Load();
                report.AppendLine($"Resolution: {settings.Size}");
                report.AppendLine($"Fullscreen: {settings.Fullscreen}");
                report.AppendLine($"VSync: {settings.VSync}");
                report.AppendLine($"Upscale: {settings.Upscale}");
                report.AppendLine($"FrameGenMode: {settings.FrameGenMode}");
                report.AppendLine($"Lighting: {settings.Lighting}");
                report.AppendLine($"Ultrawide: {settings.Ultrawide}");
                report.AppendLine($"GameSpeed: {settings.GameSpeed}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"Failed to load settings: {ex.Message}");
            }
            report.AppendLine();

            if (!string.IsNullOrEmpty(additionalInfo))
            {
                report.AppendLine("--- Additional Information ---");
                report.AppendLine(additionalInfo);
                report.AppendLine();
            }

            report.AppendLine("=== End of Crash Report ===");
            report.AppendLine();
            report.AppendLine("Please include this report when filing an issue at:");
            report.AppendLine("https://github.com/imnotsharingmyname/pcportastrobot/issues/new");

            return report.ToString();
        }

        private string GetExitCodeDescription(int exitCode)
        {
            return exitCode switch
            {
                0 => "Success",
                1 => "General error",
                321 => "Vulkan Device Lost (TDR) - GPU timeout",
                322 => "Vulkan Device Lost (driver crash)",
                -1073740791 => "STATUS_ACCESS_VIOLATION (0xC0000005) - Null pointer dereference",
                -1073741819 => "STATUS_STACK_BUFFER_OVERRUN (0xC00000F7) - Stack corruption",
                259 => "Still active (process hasn't exited)",
                _ => $"Unknown exit code (0x{exitCode:X})"
            };
        }

        public async Task<int> LaunchGameWithDlssAsync(AppSettings settings, HardwareInfo? hardware = null)
        {
            var batPath = Path.Combine(_rootDirectory, "Run-AstroHybrid-DLSS.bat");
            if (!File.Exists(batPath))
            {
                throw new FileNotFoundException("Run-AstroHybrid-DLSS.bat not found", batPath);
            }

            var runHybridPath = Path.Combine(_rootDirectory, "Build", "RunHybrid");
            if (!File.Exists(Path.Combine(runHybridPath, "AstroBot.exe")))
            {
                throw new FileNotFoundException("AstroBot.exe not found in RunHybrid", runHybridPath);
            }

            // Pre-launch validation
            var validationResult = await ValidateLaunchEnvironmentAsync(runHybridPath, settings, hardware);
            if (!validationResult.IsValid)
            {
                throw new InvalidOperationException(validationResult.ErrorMessage);
            }

            settings.LastLaunched = DateTime.Now;
            _settingsManager.Save(settings);

            var envVars = BuildEnvironmentVariables(settings, hardware);
            // DLSS-specific overrides
            envVars["ASTROHYBRID_DLSS"] = "1";
            envVars["ASTROHYBRID_CUDA_HOLE"] = "1";
            envVars["ASTROHYBRID_CRASH_DIAGNOSTICS"] = "1";
            envVars["ASTROHYBRID_LAUNCH_TIME"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batPath}\"",
                WorkingDirectory = _rootDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var kvp in envVars)
            {
                psi.EnvironmentVariables[kvp.Key] = kvp.Value;
            }

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var tcs = new TaskCompletionSource<int>();

            process.Exited += (sender, e) =>
            {
                tcs.SetResult(process.ExitCode);
            };

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    AppendDebugLog($"[GAME OUT] {e.Data}");
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    AppendDebugLog($"[GAME ERR] {e.Data}");
                }
            };

            try
            {
                process.Start();
                AppendDebugLog($"Game process started (PID: {process.Id})");
            }
            catch (Exception ex)
            {
                AppendDebugLog($"Failed to start game process: {ex.Message}");
                throw new InvalidOperationException($"Failed to start game: {ex.Message}", ex);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            return await tcs.Task;
        }

        public async Task<int> LaunchGameWithFrameGenAsync(AppSettings settings, HardwareInfo? hardware = null)
        {
            var batPath = Path.Combine(_rootDirectory, "Run-AstroHybrid-FG.bat");
            if (!File.Exists(batPath))
            {
                throw new FileNotFoundException("Run-AstroHybrid-FG.bat not found", batPath);
            }

            var runHybridPath = Path.Combine(_rootDirectory, "Build", "RunHybrid");
            if (!File.Exists(Path.Combine(runHybridPath, "AstroBot.exe")))
            {
                throw new FileNotFoundException("AstroBot.exe not found in RunHybrid", runHybridPath);
            }

            // Pre-launch validation
            var validationResult = await ValidateLaunchEnvironmentAsync(runHybridPath, settings, hardware);
            if (!validationResult.IsValid)
            {
                throw new InvalidOperationException(validationResult.ErrorMessage);
            }

            settings.LastLaunched = DateTime.Now;
            _settingsManager.Save(settings);

            var envVars = BuildEnvironmentVariables(settings, hardware);
            // Frame Gen specific overrides
            envVars["ASTROHYBRID_DLSSG"] = "1";
            envVars["ASTROHYBRID_CUDA_HOLE"] = "1";
            envVars["ASTROHYBRID_DLSS"] = "1";
            envVars["ASTROHYBRID_CRASH_DIAGNOSTICS"] = "1";
            envVars["ASTROHYBRID_LAUNCH_TIME"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batPath}\"",
                WorkingDirectory = _rootDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var kvp in envVars)
            {
                psi.EnvironmentVariables[kvp.Key] = kvp.Value;
            }

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var tcs = new TaskCompletionSource<int>();

            process.Exited += (sender, e) =>
            {
                tcs.SetResult(process.ExitCode);
            };

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    AppendDebugLog($"[GAME OUT] {e.Data}");
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    AppendDebugLog($"[GAME ERR] {e.Data}");
                }
            };

            try
            {
                process.Start();
                AppendDebugLog($"Game process started (PID: {process.Id})");
            }
            catch (Exception ex)
            {
                AppendDebugLog($"Failed to start game process: {ex.Message}");
                throw new InvalidOperationException($"Failed to start game: {ex.Message}", ex);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            return await tcs.Task;
        }

        private Dictionary<string, string> BuildEnvironmentVariables(AppSettings settings, HardwareInfo? hardware)
        {
            var env = new Dictionary<string, string>();

            // Graphics card (issue #24): an explicit choice names the card, for kyty_gpu.dll (Kyty patch
            // 0047) and the AnyPS5 renderer. A name, not an index: this list comes from Windows, whose
            // order need not be Vulkan's. An automatic choice passes nothing, and kyty_gpu.dll keeps its
            // own preference (a discrete card over integrated graphics).
            var gpu = hardware?.SelectedGpu;
            if (hardware != null && hardware.SelectedGpuIndex >= 0 && gpu != null)
            {
                env["ANYPS5_GPU"] = GpuMatchToken(gpu.Name);
            }

            // Lighting/renderer selection
            // What this game build supports (GameSetup); no file = the original 01.019.000 install.
            var runHybrid = Path.Combine(_rootDirectory, "Build", "RunHybrid");
            var features = GameFeatures.Load(runHybrid);
            // Prefer the soft-assert variant so a command-buffer overflow follows the
            // engine's failure path instead of crashing in command_buffer_writer (issue #25).
            bool useUltrawide = settings.Ultrawide || settings.IsUltrawideSize;
            bool useSuperUltrawide = settings.IsSuperUltrawideSize;
            var candidates = useUltrawide && useSuperUltrawide
                ? (settings.Lighting == 0
                    ? new[] { "AstroBot-NonRT-SuperUltrawide-SoftAssert.exe", "AstroBot-NonRT-SuperUltrawide.exe", "AstroBot-SuperUltrawide.exe", "AstroBot-NonRT-SoftAssert.exe", "AstroBot-NonRT.exe", "AstroBot.exe" }
                    : new[] { "AstroBot-SuperUltrawide.exe", "AstroBot.exe" })
                : useUltrawide
                ? (settings.Lighting == 0
                    ? new[] { "AstroBot-NonRT-Ultrawide-SoftAssert.exe", "AstroBot-NonRT-Ultrawide.exe", "AstroBot-Ultrawide.exe", "AstroBot-NonRT-SoftAssert.exe", "AstroBot-NonRT.exe", "AstroBot.exe" }
                    : new[] { "AstroBot-Ultrawide.exe", "AstroBot.exe" })
                : (settings.Lighting == 0
                    ? new[] { "AstroBot-NonRT-SoftAssert.exe", "AstroBot-NonRT.exe", "AstroBot.exe" }
                    : new[] { "AstroBot.exe" });

            if (!File.Exists(Path.Combine(runHybrid, candidates[0])))
                new GameSetup(_rootDirectory).EnsureVariantExecutables();

            // Bigger worker command buffers (#159): an install set up by 1.1.0 - 1.2.5 has 8 MiB, which
            // ran out in booster_construction. Raised to 16 MiB once per executable; a failure here only
            // keeps the old size, it never stops the game from starting.
            try
            {
                var setup = new GameSetup(_rootDirectory);
                foreach (var candidate in candidates.Concat(new[] { "AstroBot.exe" }).Distinct())
                    setup.UpgradeCommandBufferTable(candidate);
            }
            catch (Exception ex)
            {
                AppendDebugLog($"command buffer upgrade skipped: {ex.Message}");
            }

            if (settings.Lighting >= 0 && settings.Lighting <= 1)
            {
                string chosen = "AstroBot.exe";
                foreach (var candidate in candidates)
                {
                    if (File.Exists(Path.Combine(runHybrid, candidate)))
                    {
                        chosen = candidate;
                        break;
                    }
                }
                env["ASTROHYBRID_EXE"] = chosen;
            }

            // FPS limit
            var fpsValues = new[] { "30", "35", "45", "50", "55", "60", "unlimited" };
            if (settings.Fps >= 0 && settings.Fps < fpsValues.Length)
            {
                env["ASTROHYBRID_FPS_LIMIT"] = fpsValues[settings.Fps];
            }

            // Window size
            // Indices 0..3,9 = 16:9 (9 = 4K), indices 4,5,8 = 21:9, indices 6,7 = 32:9 (#58, #98)
            if (settings.Size >= 0 && settings.Size < AppSettings.ResolutionPresets.Length)
            {
                var preset = AppSettings.ResolutionPresets[settings.Size];
                int width = preset.Width;
                int height = preset.Height;
                if (settings.Ultrawide && !settings.IsUltrawideSize)
                {
                    width = settings.Size == 9 ? 5120 : settings.Size == 3 ? 3440 : 2560;
                    height = settings.Size == 9 ? 2160 : settings.Size == 3 ? 1440 : 1080;
                }
                env["KYTY_GPU_WIDTH"] = width.ToString();
                env["KYTY_GPU_HEIGHT"] = height.ToString();
            }

            // Fullscreen
            env["KYTY_GPU_FULLSCREEN"] = settings.Fullscreen ? "1" : "0";

            // Game speed
            // Always real time, where the game version is known: it writes one global at a fixed address.
            env["ASTROHYBRID_GAME_SPEED"] = features == null || features.RealTimeSpeed ? "1" : "0";
            if (features != null && features.GameStepAddress != 0)
                env["ASTROHYBRID_GAME_STEP_ADDR"] = "0x" + features.GameStepAddress.ToString("x");
            var speedValues = new[] { "100", "95", "90", "85", "80", "75" };
            if (settings.Speed >= 0 && settings.Speed < speedValues.Length)
            {
                env["ASTROHYBRID_GAME_SPEED_PERCENT"] = speedValues[settings.Speed];
            }

            // Performance overlay
            var overlayValues = new[] { "", "fps", "compact", "full" };
            if (settings.Overlay >= 0 && settings.Overlay < overlayValues.Length)
            {
                env["ASTROHYBRID_OVERLAY"] = overlayValues[settings.Overlay];
            }

            // NVIDIA options, as AstroBotLauncher.cs: DLSS and frame generation need the CUDA address
            // window and the NVIDIA runtime files next to the game.
            if (settings.Upscale != 0 || settings.FrameGenMode != 0)
            {
                env["ASTROHYBRID_CUDA_HOLE"] = "1";
                env["ASTROHYBRID_DLSS_PATH"] = Path.Combine(_rootDirectory, "Build", "RunHybrid");
            }
            if (settings.Upscale != 0)
            {
                env["ASTROHYBRID_DLSS"] = "1";
            }

            // Frame generation plan (AstroBotLauncher.FrameGenPlan): the game is never capped below 30.
            // Auto with a target starts at 2x with the game capped at half the target and lets the game
            // raise the multiplier; fixed modes cap the game at target / multiplier; no lock = uncapped.
            if (settings.FrameGenMode != 0)
            {
                var targetValues = new[] { 0, 50, 60, 90, 120, 144, 165 };
                int goal = settings.FrameGenTarget >= 0 && settings.FrameGenTarget < targetValues.Length
                    ? targetValues[settings.FrameGenTarget] : 60;
                int mult = settings.FrameGenMode == 1 ? 2 : Math.Min(Math.Max(settings.FrameGenMode, 2), 4);
                int limit = goal == 0 ? 0 : Math.Max(30, goal / mult);
                env["ASTROHYBRID_DLSSG"] = "1";
                env["ASTROHYBRID_DLSSG_FRAMES"] = (mult - 1).ToString();
                if (settings.FrameGenMode == 1 && goal != 0)
                {
                    env["ASTROHYBRID_DLSSG_TARGET"] = goal.ToString();
                    env["ASTROHYBRID_DLSSG_MAX_FRAMES"] = "3";
                }
                env["ASTROHYBRID_FPS_LIMIT"] = limit == 0 ? "unlimited" : limit.ToString();
            }

            // NVIDIA Reflex
            if (settings.LowLatency)
            {
                env["ASTROHYBRID_LOW_LATENCY"] = "1";
            }

            // Controller settings
            var padValues = new[] { "auto", "ps", "xbox", "switch", "generic" };
            if (settings.Controller >= 0 && settings.Controller < padValues.Length)
            {
                env["ASTROHYBRID_PAD_PREFER"] = padValues[settings.Controller];
            }

            // Vibration strength in percent (0-200); the Switch boost follows it instead of being a flat
            // 96 of 255, which made low strengths louder than the setting itself (#36).
            int rumblePercent = settings.EffectiveRumblePercent;
            env["ASTROHYBRID_RUMBLE_SCALE"] = rumblePercent.ToString();
            env["ASTROHYBRID_RUMBLE_SWITCH_FLOOR"] = RumbleSetting.SwitchFloor(rumblePercent, settings.SwitchBoost).ToString();

            // Window title
            env["ASTROHYBRID_WINDOW_TITLE"] = "Astro Bot";

            // Game language (AnyPS5 console language number); Auto maps the Windows display language.
            int language = settings.Language >= 0 && settings.Language <= 30 ? settings.Language : WindowsLanguage();
            env["ANYPS5_LANGUAGE"] = language.ToString();

            // VSync: Vulkan FIFO presentation (the default is immediate presentation). It also applies with DLSS
            // Frame Generation: the generated frames go through the same swapchain, and forcing VSync in the
            // NVIDIA driver (which only changes the present mode) removes the tearing there too (#15).
            if (settings.VSync) env["KYTY_GPU_PRESENT_MODE"] = "Fifo";

            // Advanced page: the variables Run-AstroHybrid.bat reads (it keeps its defaults when unset).
            if (settings.HangWatchdog) env["ASTROHYBRID_HANG_WATCHDOG"] = "1";
            if (!settings.ShaderCache) env["ASTROHYBRID_SHADER_CACHE"] = "0";
            if (!settings.DrawPrepTuning) env["ASTROHYBRID_DRAW_PREP_TUNING"] = "0";
            if (settings.Priority == "normal" || settings.Priority == "abovenormal" || settings.Priority == "high")
                env["ASTROHYBRID_PRIORITY"] = settings.Priority;
            if (settings.CpPriority == 1 || settings.CpPriority == 2) env["KYTY_CP_PRIORITY"] = settings.CpPriority.ToString();
            env["ASTROHYBRID_PAD_MOTION"] = settings.MotionControls ? "1" : "0";
            // Right-stick tilt on Xbox/generic pads: toggle instead of hold, and its strength (#146).
            if (settings.TiltToggle) env["ASTROHYBRID_TILT_TOGGLE"] = "1";
            env["ASTROHYBRID_TILT_STRENGTH"] = Math.Max(25, Math.Min(200, settings.TiltStrength)).ToString();
            // DualSense adaptive triggers: resistance and vibration in percent, 0 = off (#160).
            env["ASTROHYBRID_TRIGGER_STRENGTH"] = Math.Max(0, Math.Min(100, settings.TriggerStrength)).ToString();
            // DualSense speaker off: the controller speaker's sounds are dropped, wired or Bluetooth (#143).
            if (!settings.DualSenseSpeaker)
            {
                env["ASTROHYBRID_DS_SPEAKER"] = "0";
                env["ASTROHYBRID_DS_WIRELESS_SPEAKER"] = "0";
            }
            if (!settings.TriggerState) env["APS5_PAD_TRIGGER_STATE"] = "0";
            if (settings.SkipIntro) env["ASTROHYBRID_SKIP_INTRO"] = "1";

            // Cloth/blanket/bubble stability (#30, #35): the commit epoch fast path only for read-only
            // bindings (writable ones stay on the full check), plus the buffer range fixes.
            env["ASTROHYBRID_COMMIT_EPOCH_FASTPATH"] = "read";
            env["KYTY_BUFFER_RANGE_FIXES"] = "1";
            env["APS5_SAVEDATA_TRANSFER_FALLBACK"] = "1";

            // New stability and debugging options (from settings)
            env["ASTROHYBRID_HANG_WATCHDOG_INTERVAL"] = settings.HangWatchdogInterval.ToString();
            env["ASTROHYBRID_TEXTURE_STREAMING_QUALITY"] = Math.Clamp(settings.TextureStreamingQuality, 0, 3).ToString();
            env["ASTROHYBRID_CLEAR_SHADER_CACHE_ON_STARTUP"] = settings.ClearShaderCacheOnStartup ? "1" : "0";
            env["ASTROHYBRID_MEMORY_PRESSURE_HANDLING"] = settings.MemoryPressureHandling ? "1" : "0";
            env["ASTROHYBRID_SAFE_MODE"] = settings.SafeMode ? "1" : "0";
            env["ASTROHYBRID_DEBUG_LAYERS"] = settings.DebugLayers ? "1" : "0";
            env["ASTROHYBRID_SINGLE_THREADED_SUBMIT"] = settings.SingleThreadedSubmit ? "1" : "0";

            // Safe mode: disable experimental features
            if (settings.SafeMode)
            {
                env["ASTROHYBRID_TDR_WORKAROUND"] = "0";
                env["ASTROHYBRID_FAST_LOAD"] = "0";
                env["ASTROHYBRID_SHADER_PRECOMPILE"] = "0";
                env["ASTROHYBRID_PIPELINE_CACHE_WARMUP"] = "0";
                env["ASTROHYBRID_PHYSICS_SUBSTEP"] = "0";
                env["ASTROHYBRID_OPT_1"] = "0";
                env["ASTROHYBRID_OPT_2"] = "0";
                env["KYTY_HANG_WATCHDOG"] = "0";
            }

            return env;
        }

        // The renderer selects a card by name (ANYPS5_GPU, a case-insensitive substring of the Vulkan
        // device name). The model token is used instead of the full Windows name so it still matches
        // the shorter name Vulkan reports ("NVIDIA GeForce RTX 4050 Laptop GPU" -> "RTX 4050").
        public static string GpuMatchToken(string name)
        {
            var match = Regex.Match(name, @"(?:RTX|GTX)\s*\d{3,4}(?:\s*(?:Ti|SUPER))?", RegexOptions.IgnoreCase);
            if (match.Success) return match.Value;
            match = Regex.Match(name, @"Arc\s*[AB]?\d{3}", RegexOptions.IgnoreCase);
            if (match.Success) return match.Value;
            match = Regex.Match(name, @"(?:UHD|Iris(?:\s*Xe)?|HD)\s*Graphics(?:\s*\d{3})?", RegexOptions.IgnoreCase);
            if (match.Success) return match.Value;
            match = Regex.Match(name, @"RX\s*\d{4}(?:\s*XT)?", RegexOptions.IgnoreCase);
            if (match.Success) return match.Value;
            return name;
        }

        // The console language number for the Windows display language; English (United States) when unknown.
        public static int WindowsLanguage()
        {
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            string name = culture.Name, two = culture.TwoLetterISOLanguageName;
            switch (name)
            {
                case "pt-BR": return 17;
                case "en-GB": return 18;
                case "fr-CA": return 22;
                case "zh-TW": case "zh-HK": case "zh-MO": return 10;
            }
            if (two == "es") return name == "es-ES" ? 3 : 20;
            if (two == "zh") return 11;
            switch (two)
            {
                case "ja": return 0;
                case "en": return 1;
                case "fr": return 2;
                case "de": return 4;
                case "it": return 5;
                case "nl": return 6;
                case "pt": return 7;
                case "ru": return 8;
                case "ko": return 9;
                case "fi": return 12;
                case "sv": return 13;
                case "da": return 14;
                case "nb": case "nn": case "no": return 15;
                case "pl": return 16;
                case "tr": return 19;
                case "ar": return 21;
                case "cs": return 23;
                case "hu": return 24;
                case "el": return 25;
                case "ro": return 26;
                case "th": return 27;
                case "vi": return 28;
                case "id": return 29;
                case "uk": return 30;
                default: return 1;
            }
        }

        public bool IsGameRunning()
        {
            var processes = Process.GetProcessesByName("AstroBot");
            return processes.Length > 0;
        }

        public void KillGameProcesses()
        {
            foreach (var proc in Process.GetProcessesByName("AstroBot"))
            {
                try { proc.Kill(); } catch { }
            }
        }
    }
}
