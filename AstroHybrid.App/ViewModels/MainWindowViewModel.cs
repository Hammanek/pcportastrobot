using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Diagnostics;
using AstroHybrid.App.Models;
using AstroHybrid.App.Services;

namespace AstroHybrid.App.ViewModels
{
    public enum LauncherPage
    {
        Home,
        GraphicsSettings,
        ControllerSettings,
        AdvancedSettings,
        Installation
    }

    public class MainWindowViewModel : ViewModelBase
    {
        private readonly SettingsManager _settingsManager;
        private readonly InstallationManager _installationManager;
        private readonly HardwareDetector _hardwareDetector;
        private readonly ControllerDetector _controllerDetector;
        private readonly LauncherService _launcherService;
        private readonly string _rootDirectory;

        public SettingsManager SettingsManager => _settingsManager;

        private LauncherPage _currentPage = LauncherPage.Home;
        private AppSettings _settings = new();
        private InstallationState? _installationState;
        private HardwareInfo? _hardwareInfo;
        private ObservableCollection<ControllerInfo> _controllers = new();
        private ControllerInfo? _selectedController;
        private bool _isLaunching;
        private bool _isLoading;
        private string _statusMessage = "";

        // Update service
        private UpdateService? _updateService;
        private UpdateState _updateState = new();
        private bool _showUpdateNotification = false;

        public static MainWindowViewModel? Current { get; private set; }

        public MainWindowViewModel(string rootDirectory)
        {
            Current = this;
            _rootDirectory = rootDirectory;
            _settingsManager = new SettingsManager(rootDirectory);
            _installationManager = new InstallationManager(rootDirectory);
            _hardwareDetector = new HardwareDetector();
            _controllerDetector = new ControllerDetector();
            _launcherService = new LauncherService(rootDirectory);
            _updateService = new UpdateService(rootDirectory);

            _installationManager.StatusChanged += msg => StatusMessage = msg;
            _installationManager.ProgressChanged += pct => { /* could show progress */ };

            _updateService.StateChanged += OnUpdateStateChanged;
            _updateService.DownloadProgressChanged += (progress, status) =>
            {
                UpdateState.DownloadProgress = progress;
                UpdateState.DownloadStatus = status;
                OnPropertyChanged(nameof(UpdateState));
            };
            _updateService.StatusChanged += msg => StatusMessage = msg;

            NavigateCommand = new RelayCommand<LauncherPage>(page => CurrentPage = page);
            LaunchGameCommand = new RelayCommand(async () => await LaunchGameAsync(), () => !IsLaunching);
            ApplyBestSettingsCommand = new RelayCommand(ApplyBestSettings);
            RefreshControllersCommand = new RelayCommand(RefreshControllers);
            RepairInstallationCommand = new RelayCommand(async () => await RepairInstallationAsync());
            RebuildInstallationCommand = new RelayCommand(async () => await RebuildInstallationAsync());
            OpenInstallFolderCommand = new RelayCommand(() => _installationManager.OpenInstallFolder());
            OpenLogFolderCommand = new RelayCommand(() => _installationManager.OpenLogFolder());
            ExportSupportLogsCommand = new RelayCommand(ExportSupportLogs);
            ChangeInstallLocationCommand = new RelayCommand(async () => await ChangeInstallLocationAsync());
            UninstallCommand = new RelayCommand(async () => await UninstallAsync());

            // Update commands
            CheckForUpdatesCommand = new RelayCommand(async () => await CheckForUpdatesAsync(true));
             DownloadUpdateCommand = new RelayCommand(async () => await DownloadAndInstallUpdateAsync(), () => HasUpdateAction && !UpdateState.IsDownloading && !UpdateState.IsInstalling);
            IgnoreUpdateCommand = new RelayCommand(IgnoreUpdate);
            DismissUpdateNotificationCommand = new RelayCommand(() => ShowUpdateNotification = false);

            _ = InitializeAsync();
        }

        public override void OnPropertyChanged(string? propertyName = null)
        {
            base.OnPropertyChanged(propertyName);
            if (propertyName == nameof(Settings) && !IsLoading && _settingsManager != null)
            {
                try { _settingsManager.Save(Settings); } catch { }
            }
        }

        public LauncherPage CurrentPage
        {
            get => _currentPage;
            set => SetProperty(ref _currentPage, value);
        }

        public AppSettings Settings
        {
            get => _settings;
            set => SetProperty(ref _settings, value);
        }

        public InstallationState? InstallationState
        {
            get => _installationState;
            set => SetProperty(ref _installationState, value);
        }

        public HardwareInfo? HardwareInfo
        {
            get => _hardwareInfo;
            set => SetProperty(ref _hardwareInfo, value);
        }

        public ObservableCollection<ControllerInfo> Controllers
        {
            get => _controllers;
            set => SetProperty(ref _controllers, value);
        }

        public ControllerInfo? SelectedController
        {
            get => _selectedController;
            set => SetProperty(ref _selectedController, value);
        }

        public UpdateState UpdateState
        {
            get => _updateState;
            set => SetProperty(ref _updateState, value);
        }

        public bool HasUpdateAction => UpdateState.UpdateAvailable || UpdateState.IgnoredUpdate != null;

        public bool HasIgnoredUpdate => !UpdateState.UpdateAvailable && UpdateState.IgnoredUpdate != null;

        public bool ShowUpdateNotification
        {
            get => _showUpdateNotification;
            set => SetProperty(ref _showUpdateNotification, value);
        }

        public bool IsLaunching
        {
            get => _isLaunching;
            set => SetProperty(ref _isLaunching, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public string AppVersionText
        {
            get
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v != null ? $"AstroHybrid v{v.Major}.{v.Minor}.{v.Build}" : "AstroHybrid";
            }
        }

        public ICommand NavigateCommand { get; }
        public ICommand LaunchGameCommand { get; }
        public ICommand ApplyBestSettingsCommand { get; }
        public ICommand RefreshControllersCommand { get; }
        public ICommand RepairInstallationCommand { get; }
        public ICommand RebuildInstallationCommand { get; }
        public ICommand OpenInstallFolderCommand { get; }
        public ICommand OpenLogFolderCommand { get; }
        public ICommand ExportSupportLogsCommand { get; }
        public ICommand ChangeInstallLocationCommand { get; }
        public ICommand UninstallCommand { get; }

        // Update commands
        public ICommand CheckForUpdatesCommand { get; }
        public ICommand DownloadUpdateCommand { get; }
        public ICommand IgnoreUpdateCommand { get; }
        public ICommand DismissUpdateNotificationCommand { get; }

        private async Task InitializeAsync()
        {
            IsLoading = true;
            StatusMessage = "Loading...";

            try
            {
                // Load settings
                Settings = _settingsManager.Load();

                // Check installation state
                InstallationState = await _installationManager.GetInstallationStateAsync();

                // Detect hardware
                HardwareInfo = await Task.Run(() => _hardwareDetector.DetectHardware());
                // The card chosen on the Graphics page (issue #24), -1 = automatic.
                HardwareInfo.SelectGpu(Settings.Gpu);
                OnPropertyChanged(nameof(HardwareInfo));

                // Detect controllers
                await RefreshControllersAsync();

                // Apply best settings if first run
                if (Settings.ApplyBestOnStart && InstallationState?.IsInstalled == true)
                {
                    Settings.ApplyBestOnStart = false;
                    ApplyBestSettings();
                }

                if (HardwareInfo != null)
                    Settings.Sanitize(HardwareInfo.SupportsDlss, HardwareInfo.SupportsFrameGeneration, HardwareInfo.SupportsReflex);
                else
                    Settings.Sanitize();
                _settingsManager.Save(Settings);

                StatusMessage = InstallationState?.IsInstalled == true ? "Ready" : "Setup required";

                // A game converted by an earlier version on a CPU that is not an AMD one still holds AMD-only
                // instructions and hangs after the title screen: convert it again, once.
                var runHybrid = System.IO.Path.Combine(_rootDirectory, "Build", "RunHybrid");
                var features = GameFeatures.Load(runHybrid);
                var setup = new GameSetup(_rootDirectory);
                if (InstallationState?.IsInstalled == true && setup.IsReleaseLayout && features != null && !features.IntelConversion &&
                    GameSetup.NeedsIntelConversion() && !string.IsNullOrEmpty(Settings.GameDumpPath))
                {
                    var answer = MessageBox.Show(
                        "This PC's processor needs an updated conversion of the game (it fixes the hang after the title screen on Intel processors).\n\nConvert the game again now? It takes about a minute.",
                        "AstroHybrid", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (answer == MessageBoxResult.Yes)
                    {
                        StatusMessage = "Converting the game for this processor...";
                        try
                        {
                            await _installationManager.RepairAsync();
                            InstallationState = await _installationManager.GetInstallationStateAsync();
                            StatusMessage = "Ready";
                        }
                        catch (Exception ex)
                        {
                            StatusMessage = $"Conversion failed: {ex.Message}";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }

            // Auto-check for updates in background (after UI is responsive)
            if (Settings.AutoCheckUpdates)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000); // Small delay to let UI settle
                    await _updateService?.CheckForUpdatesAsync(false);
                });
            }
        }

        private async Task LaunchGameAsync()
        {
            if (InstallationState?.IsInstalled != true)
            {
                MessageBox.Show("Please complete the setup first.", "AstroHybrid", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!await EnsureGameLinkAsync())
                return;

            IsLaunching = true;
            StatusMessage = "Launching Astro Bot...";

            try
            {
                // Determine which launcher to use based on settings
                int exitCode;
                if (Settings.FrameGenMode != 0)
                {
                    exitCode = await _launcherService.LaunchGameAsync(Settings, HardwareInfo);
                }
                else if (Settings.Upscale != 0)
                {
                    exitCode = await _launcherService.LaunchGameAsync(Settings, HardwareInfo);
                }
                else
                {
                    exitCode = await _launcherService.LaunchGameAsync(Settings, HardwareInfo);
                }

                StatusMessage = exitCode == 0 ? "Game exited normally" : $"Game exited with code {exitCode}";
                if (exitCode != 0)
                {
                    var detail = DescribeExitFailure(exitCode);
                    if (!string.IsNullOrEmpty(detail))
                    {
                        MessageBox.Show(detail, "AstroHybrid - Game Exited With Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Launch error: {ex.Message}";
                MessageBox.Show($"Failed to launch game:\n{ex.Message}", "AstroHybrid", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLaunching = false;
            }
        }

        private void ApplyBestSettings()
        {
            if (HardwareInfo == null) return;

            var best = PickBestSettings(HardwareInfo);

            Settings.Lighting = 0; // Faster lighting
            Settings.Fps = 0; // 30 FPS steady
            Settings.Speed = 0; // 100% real-time
            Settings.Size = best.Size;
            Settings.Ultrawide = best.Ultrawide;
            Settings.Fullscreen = true;
            Settings.Upscale = best.Upscale;
            Settings.FrameGenMode = best.FrameGen;
            Settings.FrameGenTarget = best.Target;
            Settings.LowLatency = best.LowLatency;
            Settings.Controller = 0; // Auto
            Settings.RumblePercent = 70; // Medium
            Settings.Rumble = RumbleSetting.ToLegacyIndex(70);

            _settingsManager.Save(Settings);
            StatusMessage = "Applied best settings for this PC";
        }

        private void RefreshControllers()
        {
            _ = RefreshControllersAsync();
        }

        // The controller choice stays the user's (Auto lets the game pick the pad that is pressed).
        private async Task RefreshControllersAsync()
        {
            var detected = await Task.Run(() => _controllerDetector.DetectControllers());
            Controllers.Clear();
            foreach (var c in detected)
                Controllers.Add(c);

        }

        // A moved or disconnected dump leaves Build\RunHybrid\app0 pointing nowhere, and the game then stops at
        // start (#44). Check before launching and offer the repair instead. Returns true when the game may start.
        private async Task<bool> EnsureGameLinkAsync()
        {
            string? problem;
            try
            {
                problem = GameSetup.DescribeBrokenGameLink(System.IO.Path.Combine(_rootDirectory, "Build", "RunHybrid"));
            }
            catch
            {
                return true; // never block a launch because the check itself failed
            }
            if (problem == null)
                return true;

            bool dumpFound = !string.IsNullOrEmpty(Settings.GameDumpPath) &&
                             System.IO.File.Exists(System.IO.Path.Combine(Settings.GameDumpPath, "eboot.bin"));
            var message = $"Astro Bot can't start: {problem}.\n\n" +
                          (dumpFound
                              ? "This usually means the game links are out of date. Repair the installation now?"
                              : $"The game dump{(string.IsNullOrEmpty(Settings.GameDumpPath) ? "" : $" ({Settings.GameDumpPath})")} was moved, renamed or is on a drive that is not connected. " +
                                "Connect the drive and press Play again, or choose the dump's new folder and repair now?");
            if (MessageBox.Show(message, "AstroHybrid", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return false;
            var savedDump = Settings.GameDumpPath;
            if (!dumpFound)
            {
                // RepairInstallationAsync asks for the folder when none is saved.
                Settings.GameDumpPath = "";
            }
            await RepairInstallationAsync();
            if (string.IsNullOrEmpty(Settings.GameDumpPath))
                Settings.GameDumpPath = savedDump; // folder dialog cancelled: keep the old path
            return false;
        }

        private async Task RepairInstallationAsync()
        {
            if (string.IsNullOrEmpty(Settings.GameDumpPath))
            {
                // Installs made by the classic launcher or an older version do not know the dump folder: ask once.
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Select your Astro Bot dump folder (the one with eboot.bin)", ShowNewFolderButton = false })
                {
                    if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                    var info = await new GameValidator().ValidateGameDumpAsync(dialog.SelectedPath);
                    if (!info.IsValid)
                    {
                        MessageBox.Show(info.ValidationError ?? "That folder is not an Astro Bot dump.", "AstroHybrid", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    if (!string.IsNullOrEmpty(info.Warning))
                        MessageBox.Show(info.Warning, "AstroHybrid - Game version", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Settings.GameDumpPath = dialog.SelectedPath;
                    _settingsManager.Save(Settings);
                }
            }

            var result = MessageBox.Show("This will rebuild the installation from your game dump. Continue?",
                "Repair Installation", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            IsLoading = true;
            StatusMessage = "Repairing installation...";

            try
            {
                var success = await _installationManager.RepairAsync();
                if (success)
                {
                    InstallationState = await _installationManager.GetInstallationStateAsync();
                    StatusMessage = "Repair complete";
                }
                else
                {
                    StatusMessage = "Repair failed";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Repair error: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task RebuildInstallationAsync()
        {
            if (string.IsNullOrEmpty(Settings.GameDumpPath))
            {
                // Installs made by the classic launcher or an older version do not know the dump folder: ask once.
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Select your Astro Bot dump folder (the one with eboot.bin)", ShowNewFolderButton = false })
                {
                    if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                    var info = await new GameValidator().ValidateGameDumpAsync(dialog.SelectedPath);
                    if (!info.IsValid)
                    {
                        MessageBox.Show(info.ValidationError ?? "That folder is not an Astro Bot dump.", "AstroHybrid", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    if (!string.IsNullOrEmpty(info.Warning))
                        MessageBox.Show(info.Warning, "AstroHybrid - Game version", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Settings.GameDumpPath = dialog.SelectedPath;
                    _settingsManager.Save(Settings);
                }
            }

            var result = MessageBox.Show("This will completely rebuild the installation. Continue?",
                "Rebuild Installation", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            IsLoading = true;
            StatusMessage = "Rebuilding installation...";

            try
            {
                var success = await _installationManager.RebuildAsync();
                if (success)
                {
                    InstallationState = await _installationManager.GetInstallationStateAsync();
                    StatusMessage = "Rebuild complete";
                }
                else
                {
                    StatusMessage = "Rebuild failed";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Rebuild error: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task ChangeInstallLocationAsync()
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select new installation location",
                ShowNewFolderButton = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                IsLoading = true;
                StatusMessage = "Moving installation...";

                try
                {
                    await _installationManager.ChangeInstallLocationAsync(dialog.SelectedPath);
                    InstallationState = await _installationManager.GetInstallationStateAsync();
                    StatusMessage = "Installation moved successfully";
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Move error: {ex.Message}";
                }
                finally
                {
                    IsLoading = false;
                }
            }
        }

        private async Task UninstallAsync()
        {
            var result = MessageBox.Show("This will remove all AstroHybrid generated files. Your original game dump will NOT be deleted. Continue?",
                "Uninstall AstroHybrid", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            result = MessageBox.Show("Are you sure? This cannot be undone.",
                "Confirm Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            IsLoading = true;
            StatusMessage = "Uninstalling...";

            try
            {
                await _installationManager.UninstallAsync(true); // Keep game dump
                InstallationState = await _installationManager.GetInstallationStateAsync();
                Settings.FirstRunComplete = false;
                _settingsManager.Save(Settings);
                StatusMessage = "Uninstall complete. Run setup to reinstall.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Uninstall error: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        // Best settings logic (ported from AstroBotLauncher.cs)
        private (int Size, bool Ultrawide, int Upscale, int FrameGen, int Target, bool LowLatency) PickBestSettings(HardwareInfo pc)
        {
            var best = (Size: 2, Ultrawide: false, Upscale: 0, FrameGen: 0, Target: 2, LowLatency: false);

            var displayAspect = pc.ScreenHeight > 0 ? (double)pc.ScreenWidth / pc.ScreenHeight : 16.0 / 9.0;
            if (displayAspect >= 3.2)
            {
                best.Ultrawide = true;
                best.Size = pc.ScreenWidth >= 5120 && pc.ScreenHeight >= 1440 ? 6
                          : pc.ScreenWidth >= 3840 && pc.ScreenHeight >= 1080 ? 7 : 4;
            }
            else if (displayAspect >= 2.1)
            {
                best.Ultrawide = true;
                best.Size = pc.ScreenWidth >= 5120 && pc.ScreenHeight >= 2160 ? 8
                          : pc.ScreenWidth >= 3440 && pc.ScreenHeight >= 1440 ? 5 : 4;
            }
            else
            {
                foreach (var i in new[] { 0, 1, 2, 3, 9 })
                {
                    var preset = AppSettings.ResolutionPresets[i];
                    if (preset.Width <= pc.ScreenWidth && preset.Height <= pc.ScreenHeight)
                        best.Size = i;
                }
            }

            if (pc.RtxSeries >= 20)
            {
                best.Upscale = 1;
                best.LowLatency = true;
            }

            if (pc.RtxSeries >= 40)
            {
                best.FrameGen = 1; // Auto
                int goal = pc.RtxSeries >= 50 && pc.RefreshRate >= 120 ? 120 :
                          pc.RtxSeries >= 50 && pc.RefreshRate >= 90 ? 90 : 60;
                var targetValues = new[] { 0, 50, 60, 90, 120, 144, 165 };
                best.Target = Array.IndexOf(targetValues, goal);
                if (best.Target < 0) best.Target = 2;
            }

            return best;
        }

        // Update service event handlers
        private void OnUpdateStateChanged(UpdateState state)
        {
            UpdateState = state;

            // Show notification when update becomes available
            if (state.UpdateAvailable && state.LatestUpdate != null && !state.IsChecking)
            {
                // Don't show if user dismissed or if we're currently downloading/installing
                if (!ShowUpdateNotification && !state.IsDownloading && !state.IsInstalling)
                {
                    ShowUpdateNotification = true;
                }
            }
            else if (!state.UpdateAvailable)
            {
                ShowUpdateNotification = false;
            }

            // Refresh command states
            OnPropertyChanged(nameof(UpdateState));
            OnPropertyChanged(nameof(HasUpdateAction));
            OnPropertyChanged(nameof(HasIgnoredUpdate));
            (DownloadUpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private async Task CheckForUpdatesAsync(bool forceCheck = false)
        {
            if (_updateService == null) return;

            if (!forceCheck && Settings.AutoCheckUpdates == false)
            {
                return;
            }

            await _updateService.CheckForUpdatesAsync(forceCheck);
        }

        private async Task DownloadAndInstallUpdateAsync()
        {
            if (_updateService == null || UpdateState.LatestUpdate == null) return;

            var updateInfo = UpdateState.LatestUpdate ?? UpdateState.IgnoredUpdate;
            if (updateInfo == null) return;

            // Download the update
            var downloadSuccess = await _updateService.DownloadUpdateAsync(updateInfo);
            if (!downloadSuccess)
            {
                return;
            }

            // Install the update
            var installSuccess = await _updateService.InstallUpdateAsync(updateInfo);
            if (installSuccess)
            {
                ShowUpdateNotification = false;
                UpdateState.UpdateAvailable = false;
                UpdateState.LatestUpdate = null;

                // Clear ignored version since we updated
                var settings = _settingsManager.Load();
                settings.IgnoredUpdateVersion = null;
                _settingsManager.Save(settings);

            }
        }

        private void IgnoreUpdate()
        {
            if (UpdateState.LatestUpdate != null)
            {
                _updateService?.IgnoreVersion(UpdateState.LatestUpdate.Version);
                ShowUpdateNotification = false;
                UpdateState.IgnoredVersion = UpdateState.LatestUpdate.Version;
                UpdateState.IgnoredUpdate = UpdateState.LatestUpdate;
                UpdateState.LatestUpdate = null;
                UpdateState.UpdateAvailable = false;
                OnPropertyChanged(nameof(UpdateState));
                OnPropertyChanged(nameof(HasUpdateAction));
                OnPropertyChanged(nameof(HasIgnoredUpdate));
            }
        }

        private void RestartLauncher()
        {
            try
            {
                var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true
                };
                Process.Start(psi);
                System.Windows.Application.Current.Shutdown();
            }
            catch
            {
                // Fallback: just close
                System.Windows.Application.Current.Shutdown();
            }
        }

        private string DescribeExitFailure(int exitCode)
        {
            string logSnippet = "";
            try
            {
                foreach (var logName in new[] { "hybrid-run.err.log", "hybrid-run.out.log" })
                {
                    var path = Path.Combine(_rootDirectory, "Logs", logName);
                    if (!File.Exists(path)) continue;
                    var hint = HintFromLog(File.ReadAllText(path), Settings.GameVersion, out bool wholeMessage);
                    if (hint == null) continue;
                    if (wholeMessage) return hint;
                    logSnippet = hint;
                    break;
                }
            }
            catch { }

            if (exitCode == 321 && string.IsNullOrEmpty(logSnippet))
                logSnippet = "Renderer initialization or runtime failure (code 321). Make sure your GPU supports Vulkan VK_EXT_mesh_shader and drivers are up to date.";
            else if (exitCode == -1073741819 && string.IsNullOrEmpty(logSnippet))
                logSnippet = "Access violation (0xC0000005). Try 'Repair Installation' on the Installation page.";
            else if (exitCode == -1073740791 && string.IsNullOrEmpty(logSnippet))
                logSnippet = "Application abort/VRAM failure (0xC0000409). Try lowering resolution or VRAM load.";

            if (string.IsNullOrEmpty(logSnippet)) return "";
            return $"Astro Bot exited with code {exitCode}.\n\n{logSnippet}\n\nLogs are in Logs\\hybrid-run.out.log and Logs\\hybrid-run.err.log (or use 'Export Support Logs' on the Installation page).";
        }

        // Turns the text of a game log into advice for the player, or null when nothing known is in it.
        // wholeMessage: the hint replaces the whole dialog text (no exit code / log paths around it).
        public static string? HintFromLog(string text, string? gameVersion, out bool wholeMessage)
        {
            wholeMessage = false;
            bool Has(string s) => text.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;
            if (Has("VK_EXT_mesh_shader") || Has("meshShader"))
            {
                wholeMessage = true;
                return "Your GPU or Vulkan driver does not support VK_EXT_mesh_shader (required by Astro Bot's NGG mesh shaders).\n\nIf you have an RTX 20+, RX 6000+, or Intel Arc GPU, update your GPU drivers or select your discrete GPU in Graphics Settings.\n(Older GPUs such as GTX 10/16 or RX 5000/RDNA1 lack hardware mesh shader support.)";
            }
            if (Has("VK_ERROR_DEVICE_LOST"))
                return "Vulkan device lost (VK_ERROR_DEVICE_LOST) detected in logs. Try lowering resolution, turning Frame Generation off, or closing GPU overlays.";
            if (Has("param.json or param.sfo not found"))
                return "The game's files could not be found (param.json). The dump was probably moved, renamed or is on a drive that is not connected. Reconnect it, or use 'Repair Installation' on the Installation page and pick the dump's current folder.";
            // Game asserts in the asset loader (#54, #47, #80, #130): seen only on game version 01.007.000.
            if (Has("ObjectDefinitionContext") || Has("Duplicated definition name") ||
                (Has("Xml.cpp:") && Has("is broken and failed to load")))
            {
                return "The game stopped in its asset loader (\"Duplicated definition name\" / \"Xml file is broken\"). " +
                       ((gameVersion ?? "").StartsWith(GameValidator.KnownBadVersion)
                           ? "Your dump is game version 01.007.000, which is known to crash this way. Please update the dump to 01.019.000 and run 'Repair Installation'."
                           : "This is known from game version 01.007.000; 01.019.000 is recommended. If your dump is already 01.019.000, please report it with the logs.");
            }
            return null;
        }

        private void ExportSupportLogs()
        {
            try
            {
                var logsDir = Path.Combine(_rootDirectory, "Logs");
                Directory.CreateDirectory(logsDir);
                var zipPath = Path.Combine(logsDir, $"astrohybrid-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
                using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    var candidates = new[]
                    {
                        Path.Combine(logsDir, "setup.log"),
                        Path.Combine(logsDir, "setup-relinker.log"),
                        Path.Combine(logsDir, "hybrid-run.out.log"),
                        Path.Combine(logsDir, "hybrid-run.err.log"),
                        Path.Combine(_rootDirectory, "Build", "astrohybrid-settings.json"),
                        Path.Combine(_rootDirectory, "Build", "RunHybrid", "astrohybrid-game.json")
                    };
                    foreach (var file in candidates)
                    {
                        if (File.Exists(file))
                        {
                            archive.CreateEntryFromFile(file, Path.GetFileName(file));
                        }
                    }
                }
                StatusMessage = $"Exported support logs to {Path.GetFileName(zipPath)}";
                MessageBox.Show($"Support logs bundle created (no copyrighted game files included):\n{zipPath}",
                    "Export Support Logs", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export logs: {ex.Message}", "Export Support Logs", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
