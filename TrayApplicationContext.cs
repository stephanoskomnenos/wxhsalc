using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using ClashXW.Models;
using ClashXW.Native;
using ClashXW.Services;

namespace ClashXW
{
    internal class TrayApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly MessageWindow _messageWindow;

        private ClashApiService? _apiService;
        private ClashProcessService? _clashProcessService;
        private Win32MenuBuilder? _menuBuilder;
        private readonly string? _executablePath;
        private string _currentConfigPath;

        // Cache for menu state
        private ClashConfig? _cachedConfigs;
        private ProxiesResponse? _cachedProxies;
        private DashboardForm? _dashboardForm;
        private bool _isSystemProxyEnabled;
        private bool _isTunEnabled;
        private bool _isPowerResumeRecoveryInProgress;
        private bool _isPowerSuspended;
        private int _powerTransitionGeneration;
        private bool _isExiting;

        public TrayApplicationContext()
        {
            ConfigManager.EnsureDefaultConfigExists();
            Logger.Info($"ClashXW starting. LogFile={Logger.CurrentLogFilePath}");

            _executablePath = Path.Combine(AppContext.BaseDirectory, "ClashAssets", "clash.exe");
            _currentConfigPath = ConfigManager.GetCurrentConfigPath();

            // Initialize services
            _clashProcessService = new ClashProcessService(_executablePath);
            _menuBuilder = new Win32MenuBuilder();

            // Create a message window for menu handling
            _messageWindow = new MessageWindow();
            _messageWindow.ThemeChanged += UpdateTrayIcon;
            _messageWindow.PowerSuspending += OnPowerSuspending;
            _messageWindow.PowerResumed += OnPowerResumed;

            // Create notify icon
            _notifyIcon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "ClashXW",
                Visible = true
            };
            _notifyIcon.MouseUp += OnNotifyIconMouseUp;

            StartClashCore(exitOnFailure: true);
            InitializeApiService();
        }

        private Icon LoadIcon()
        {
            // Select icon based on TUN state
            var resourceName = _isTunEnabled ? "ClashXW.Resources.icon_tun.ico" : "ClashXW.Resources.icon.ico";
            Logger.Info($"LoadIcon: TUN={_isTunEnabled}, SystemProxy={_isSystemProxyEnabled}, DarkMode={DarkModeHelper.IsDarkModeEnabled}, Resource={resourceName}");

            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(resourceName);
            Icon baseIcon;

            if (stream != null)
            {
                baseIcon = new Icon(stream);
            }
            else
            {
                // Fallback to default icon
                var defaultStream = assembly.GetManifestResourceStream("ClashXW.Resources.icon.ico");
                baseIcon = defaultStream != null ? new Icon(defaultStream) : SystemIcons.Application;
            }

            // Invert icon colors for dark mode (black icon -> white icon)
            if (DarkModeHelper.IsDarkModeEnabled)
            {
                baseIcon = InvertIconColors(baseIcon);
            }

            // Apply lightening filter when system proxy is off
            if (!_isSystemProxyEnabled)
            {
                return ApplyLighteningFilter(baseIcon);
            }

            return baseIcon;
        }

        private Icon ApplyLighteningFilter(Icon icon)
        {
            using var originalBitmap = icon.ToBitmap();
            var lightenedBitmap = new Bitmap(originalBitmap.Width, originalBitmap.Height, PixelFormat.Format32bppArgb);

            // Color matrix to lighten the image (increase brightness and reduce saturation slightly)
            var colorMatrix = new ColorMatrix(new float[][]
            {
                new float[] { 1.0f, 0, 0, 0, 0 },
                new float[] { 0, 1.0f, 0, 0, 0 },
                new float[] { 0, 0, 1.0f, 0, 0 },
                new float[] { 0, 0, 0, 0.5f, 0 },  // Reduce alpha to 50%
                new float[] { 0.3f, 0.3f, 0.3f, 0, 1 }  // Add brightness
            });

            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(colorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);

            using var g = Graphics.FromImage(lightenedBitmap);
            g.DrawImage(originalBitmap,
                new Rectangle(0, 0, lightenedBitmap.Width, lightenedBitmap.Height),
                0, 0, originalBitmap.Width, originalBitmap.Height,
                GraphicsUnit.Pixel, attributes);

            var result = Icon.FromHandle(lightenedBitmap.GetHicon());
            icon.Dispose();
            return result;
        }

        private Icon InvertIconColors(Icon icon)
        {
            using var originalBitmap = icon.ToBitmap();
            var invertedBitmap = new Bitmap(originalBitmap.Width, originalBitmap.Height, PixelFormat.Format32bppArgb);

            // Color matrix to invert RGB while preserving alpha
            var colorMatrix = new ColorMatrix(new float[][]
            {
                new float[] { -1, 0, 0, 0, 0 },
                new float[] { 0, -1, 0, 0, 0 },
                new float[] { 0, 0, -1, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 },   // Keep alpha unchanged
                new float[] { 1, 1, 1, 0, 1 }    // Add 1 to RGB to complete inversion
            });

            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(colorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);

            using var g = Graphics.FromImage(invertedBitmap);
            g.DrawImage(originalBitmap,
                new Rectangle(0, 0, invertedBitmap.Width, invertedBitmap.Height),
                0, 0, originalBitmap.Width, originalBitmap.Height,
                GraphicsUnit.Pixel, attributes);

            var result = Icon.FromHandle(invertedBitmap.GetHicon());
            icon.Dispose();
            return result;
        }

        private void UpdateTrayIcon()
        {
            var oldIcon = _notifyIcon.Icon;
            _notifyIcon.Icon = LoadIcon();

            // Dispose the old icon if it's not a system icon
            if (oldIcon != null && oldIcon != SystemIcons.Application)
            {
                oldIcon.Dispose();
            }
        }

        private static readonly TimeSpan PowerResumeRestartDelay = TimeSpan.FromSeconds(2);

        private bool StartClashCore(bool exitOnFailure = false)
        {
            if (_clashProcessService == null) return false;

            try
            {
                _clashProcessService.Start(_currentConfigPath);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to start Clash core", ex);

                if (exitOnFailure)
                {
                    MessageBox.Show($"Failed to start Clash process:\n{ex.Message}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    ExitThread();
                }
                else
                {
                    ShowBalloonTip("Error", $"Failed to recover Clash core after resume: {ex.Message}", ToolTipIcon.Error);
                }

                return false;
            }
        }

        private void OnPowerSuspending()
        {
            if (_isExiting || _clashProcessService == null) return;

            if (_isPowerSuspended)
            {
                Logger.Info($"Ignoring duplicate power suspend; generation={_powerTransitionGeneration}");
                return;
            }

            _isPowerSuspended = true;
            _powerTransitionGeneration++;
            _isPowerResumeRecoveryInProgress = false;
            DisposeApiService();

            try
            {
                Logger.Info($"Power suspend/hibernate requested; generation={_powerTransitionGeneration}; stopping Clash core before the system sleeps");
                _clashProcessService.Stop();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to stop Clash core before suspend/hibernate: {ex.Message}");
            }
        }

        private async void OnPowerResumed()
        {
            if (_isExiting || _clashProcessService == null || _isPowerResumeRecoveryInProgress) return;

            if (!_isPowerSuspended)
            {
                Logger.Info("Power resume received without a tracked suspend; refreshing Clash core/API state");
            }

            _isPowerResumeRecoveryInProgress = true;
            var recoveryGeneration = _powerTransitionGeneration;
            Logger.Info($"Power resume recovery scheduled; generation={recoveryGeneration}; delay={PowerResumeRestartDelay.TotalMilliseconds:0}ms");

            try
            {
                // Give Windows a short moment to restore network adapters and routing before
                // restarting mihomo/Clash and refreshing its API state.
                await Task.Delay(PowerResumeRestartDelay);

                if (_isExiting || recoveryGeneration != _powerTransitionGeneration)
                {
                    Logger.Info($"Skipping stale power resume recovery; recoveryGeneration={recoveryGeneration}; currentGeneration={_powerTransitionGeneration}; isExiting={_isExiting}");
                    return;
                }

                Logger.Info($"Power resume detected; generation={recoveryGeneration}; restarting Clash core");
                if (!StartClashCore())
                {
                    return;
                }

                InitializeApiService();
                await RefreshCachedDataAsync();
                Logger.Info($"Power resume recovery completed; generation={recoveryGeneration}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to refresh state after power resume: {ex.Message}");
            }
            finally
            {
                if (recoveryGeneration == _powerTransitionGeneration)
                {
                    _isPowerSuspended = false;
                    _isPowerResumeRecoveryInProgress = false;
                }
            }
        }

        private void InitializeApiService()
        {
            DisposeApiService();

            var apiDetails = ConfigManager.ReadApiDetails(_currentConfigPath);
            if (apiDetails != null)
            {
                _apiService = new ClashApiService(apiDetails.BaseUrl, apiDetails.Secret);
            }
            else
            {
                MessageBox.Show($"Failed to read API details from {_currentConfigPath}. API features will be disabled.",
                    "Config Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void DisposeApiService()
        {
            _apiService?.Dispose();
            _apiService = null;
        }

        private async void OnNotifyIconMouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right) return;

            // Fetch fresh data before showing menu
            await RefreshCachedDataAsync();

            ShowContextMenu();
        }

        private async Task RefreshCachedDataAsync()
        {
            var apiService = _apiService;
            if (apiService == null) return;

            try
            {
                var configsTask = apiService.GetConfigsAsync();
                var proxiesTask = apiService.GetProxiesAsync();

                await Task.WhenAll(configsTask, proxiesTask);

                _cachedConfigs = await configsTask;
                _cachedProxies = await proxiesTask;

                // Sync TUN and system proxy states
                var tunChanged = false;
                var proxyChanged = false;

                var newTunState = _cachedConfigs?.Tun?.Enable ?? false;
                if (_isTunEnabled != newTunState)
                {
                    _isTunEnabled = newTunState;
                    tunChanged = true;
                }

                var proxyAddress = _cachedConfigs != null ? GetProxyAddress(_cachedConfigs) : null;
                var newProxyState = proxyAddress != null && SystemProxyManager.IsProxyEnabled(proxyAddress);
                if (_isSystemProxyEnabled != newProxyState)
                {
                    _isSystemProxyEnabled = newProxyState;
                    proxyChanged = true;
                }

                if (tunChanged || proxyChanged)
                {
                    UpdateTrayIcon();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to fetch API data: {ex.Message}");
            }
        }

        private void ShowContextMenu()
        {
            if (_menuBuilder == null) return;

            using var menu = _menuBuilder.BuildMenu(
                _cachedConfigs,
                _cachedProxies,
                _currentConfigPath,
                onModeSelected: OnModeSelected,
                onProxyNodeSelected: OnProxyNodeSelected,
                onTestGroupLatency: OnTestGroupLatency,
                onSystemProxyToggle: OnSystemProxyToggle,
                onTunModeToggle: OnTunModeToggle,
                onOpenDashboard: OnOpenDashboard,
                onTestLatency: OnTestLatency,
                onConfigSelected: OnConfigSelected,
                onReloadConfig: OnReloadConfig,
                onEditConfig: OnEditConfig,
                onOpenConfigFolder: OnOpenConfigFolder,
                onExit: OnExit
            );

            var commandId = menu.Show(_messageWindow.Handle);
            if (commandId.HasValue)
            {
                menu.ExecuteCommand(commandId.Value);
            }
        }

        private async void OnModeSelected(string mode)
        {
            var apiService = _apiService;
            if (apiService == null) return;

            try
            {
                await apiService.UpdateModeAsync(mode);
            }
            catch (Exception ex)
            {
                ShowBalloonTip("Error", $"Failed to set mode: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private async void OnProxyNodeSelected(string groupName, string nodeName)
        {
            var apiService = _apiService;
            if (apiService == null) return;

            try
            {
                await apiService.SelectProxyNodeAsync(groupName, nodeName);
            }
            catch (Exception ex)
            {
                ShowBalloonTip("Error", $"Failed to set proxy node: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private async void OnTestGroupLatency(string groupName)
        {
            var apiService = _apiService;
            if (apiService == null) return;

            try
            {
                var group = _cachedProxies?.Proxies?.GetValueOrDefault(groupName);
                if (group == null) return;

                if (IsAutoGroup(group))
                {
                    await apiService.TestGroupLatencyAsync(groupName);
                }
                else
                {
                    var tasks = new System.Collections.Generic.List<Task>();
                    foreach (var nodeName in group.All ?? (IReadOnlyList<string>)Array.Empty<string>())
                    {
                        tasks.Add(apiService.TestProxyLatencyAsync(nodeName));
                    }
                    await Task.WhenAll(tasks);
                }

                ShowBalloonTip("Success", $"Latency test completed for {groupName}", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                ShowBalloonTip("Error", $"Failed to test latency for {groupName}: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private static bool IsAutoGroup(ProxyNode proxy)
        {
            return proxy.Type.Equals("Fallback", StringComparison.OrdinalIgnoreCase)
                || proxy.Type.Equals("URLTest", StringComparison.OrdinalIgnoreCase);
        }

        private async void OnSystemProxyToggle(bool enable)
        {
            var apiService = _apiService;
            if (apiService == null) return;

            try
            {
                var configs = await apiService.GetConfigsAsync();
                if (configs == null) return;

                var proxyAddress = GetProxyAddress(configs);
                if (proxyAddress == null)
                {
                    ShowBalloonTip("Error", "Proxy port not configured in Clash.", ToolTipIcon.Error);
                    return;
                }

                if (enable)
                {
                    SystemProxyManager.SetProxy(proxyAddress);
                }
                else
                {
                    SystemProxyManager.DisableProxy();
                }

                _isSystemProxyEnabled = enable;
                UpdateTrayIcon();
            }
            catch (Exception ex)
            {
                ShowBalloonTip("Error", $"Failed to toggle system proxy: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private async void OnTunModeToggle(bool enable)
        {
            var apiService = _apiService;
            if (apiService == null) return;

            try
            {
                await apiService.UpdateTunModeAsync(enable);

                // Verify actual state after toggle
                var configs = await apiService.GetConfigsAsync();
                var actualTunState = configs?.Tun?.Enable ?? false;

                if (actualTunState != enable)
                {
                    ShowBalloonTip("Error", "Failed to set TUN mode. Check log.", ToolTipIcon.Error);
                }

                if (_isTunEnabled != actualTunState)
                {
                    _isTunEnabled = actualTunState;
                    UpdateTrayIcon();
                }
            }
            catch (Exception ex)
            {
                ShowBalloonTip("Error", $"Failed to set TUN mode: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private void OnOpenDashboard()
        {
            var apiDetails = ConfigManager.ReadApiDetails(_currentConfigPath);
            if (apiDetails == null || string.IsNullOrEmpty(apiDetails.DashboardUrl)) return;

            // Show existing window or create new
            if (_dashboardForm != null && !_dashboardForm.IsDisposed)
            {
                if (_dashboardForm.WindowState == FormWindowState.Minimized)
                {
                    _dashboardForm.WindowState = FormWindowState.Normal;
                }

                _dashboardForm.Activate();
                return;
            }

            _dashboardForm = new DashboardForm(apiDetails.DashboardUrl);
            DashboardWindowPlacementManager.Restore(_dashboardForm);
            _dashboardForm.Show();

            // OLD IMPLEMENTATION (preserved):
            // try
            // {
            //     Process.Start(new ProcessStartInfo(apiDetails.DashboardUrl) { UseShellExecute = true });
            // }
            // catch (Exception ex)
            // {
            //     ShowBalloonTip("Error", $"Failed to open dashboard: {ex.Message}", ToolTipIcon.Error);
            // }
        }

        private async void OnTestLatency()
        {
            var apiService = _apiService;
            if (apiService == null || _cachedProxies?.Proxies == null) return;

            try
            {
                var latencyTasks = new System.Collections.Generic.List<Task>();

                foreach (var proxy in _cachedProxies.Proxies.Values)
                {
                    if (proxy.All is { Count: > 0 })
                    {
                        if (IsAutoGroup(proxy))
                        {
                            latencyTasks.Add(apiService.TestGroupLatencyAsync(proxy.Name));
                        }
                        else
                        {
                            foreach (var nodeName in proxy.All)
                            {
                                latencyTasks.Add(apiService.TestProxyLatencyAsync(nodeName));
                            }
                        }
                    }
                    else
                    {
                        latencyTasks.Add(apiService.TestProxyLatencyAsync(proxy.Name));
                    }
                }

                await Task.WhenAll(latencyTasks);
                ShowBalloonTip("Success", "Latency tests completed", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                ShowBalloonTip("Error", $"Failed to run latency tests: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private async void OnConfigSelected(string newPath)
        {
            var apiService = _apiService;
            if (apiService == null) return;

            try
            {
                await apiService.ReloadConfigAsync(newPath);
                _currentConfigPath = newPath;
                ConfigManager.SetCurrentConfigPath(newPath);
                InitializeApiService();
            }
            catch (Exception ex)
            {
                ShowBalloonTip("Error", $"Failed to switch configuration: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private async void OnReloadConfig()
        {
            var apiService = _apiService;
            if (apiService == null || string.IsNullOrEmpty(_currentConfigPath)) return;

            try
            {
                await apiService.ReloadConfigAsync(_currentConfigPath);
                ShowBalloonTip("Success", "Configuration reloaded", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                ShowBalloonTip("Error", $"Failed to reload configuration: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private void OnEditConfig()
        {
            if (string.IsNullOrEmpty(_currentConfigPath) || !File.Exists(_currentConfigPath))
            {
                ShowBalloonTip("Error", $"Config file not found at: {_currentConfigPath}", ToolTipIcon.Error);
                return;
            }

            try
            {
                // open the config file with default program that is associated with extension "yml" and "yaml"
                Process.Start(new ProcessStartInfo(_currentConfigPath)
                {
                    UseShellExecute = true,
                    Verb = "open",
                });
            }
            catch (Exception ex)
            {
                ShowBalloonTip("Error", $"Failed to open config file: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private void OnOpenConfigFolder()
        {
            if (string.IsNullOrEmpty(_currentConfigPath)) return;

            var configFolder = Path.GetDirectoryName(_currentConfigPath);
            if (configFolder == null || !Directory.Exists(configFolder)) return;

            try
            {
                Process.Start(new ProcessStartInfo(configFolder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowBalloonTip("Error", $"Failed to open config folder: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private void OnExit()
        {
            _isExiting = true;
            // Check if system proxy was enabled and disable it
            if (_cachedConfigs != null)
            {
                var proxyAddress = GetProxyAddress(_cachedConfigs);
                if (proxyAddress != null && SystemProxyManager.IsProxyEnabled(proxyAddress))
                {
                    SystemProxyManager.DisableProxy();
                }
            }

            DisposeApiService();
            _clashProcessService?.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _messageWindow.Dispose();
            ExitThread();
        }

        private string? GetProxyAddress(ClashConfig configs)
        {
            var mixedPort = configs.MixedPort;
            if (mixedPort > 0) return $"127.0.0.1:{mixedPort}";

            var socksPort = configs.SocksPort;
            if (socksPort > 0) return $"socks=127.0.0.1:{socksPort}";

            return null;
        }

        private void ShowBalloonTip(string title, string text, ToolTipIcon icon)
        {
            _notifyIcon.ShowBalloonTip(3000, title, text, icon);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _notifyIcon.Dispose();
                DisposeApiService();
                _messageWindow.ThemeChanged -= UpdateTrayIcon;
                _messageWindow.PowerSuspending -= OnPowerSuspending;
                _messageWindow.PowerResumed -= OnPowerResumed;
                _messageWindow.Dispose();
                _clashProcessService?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A hidden window used for Win32 menu operations and receiving system broadcast messages.
    /// Note: Message-only windows (HWND_MESSAGE) do NOT receive broadcast messages like
    /// WM_SETTINGCHANGE, so we use a regular hidden window instead.
    /// </summary>
    internal class MessageWindow : NativeWindow, IDisposable
    {
        private const int WM_SETTINGCHANGE = 0x001A;
        private const int WM_POWERBROADCAST = 0x0218;
        private const int PBT_APMSUSPEND = 0x0004;
        private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        private const int PBT_APMRESUMECRITICAL = 0x0006;
        private const int PBT_APMRESUMESUSPEND = 0x0007;

        private IntPtr _suspendResumeNotificationHandle;
        private bool _disposed;

        public event Action? ThemeChanged;
        public event Action? PowerSuspending;
        public event Action? PowerResumed;

        public MessageWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "ClashXW_MessageWindow",
                Style = 0, // Not visible
            });
            Logger.Info($"MessageWindow created: Handle={Handle}");
            RegisterSuspendResumeNotifications();
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            UnregisterSuspendResumeNotifications();
            DestroyHandle();
            GC.SuppressFinalize(this);
        }

        private void RegisterSuspendResumeNotifications()
        {
            _suspendResumeNotificationHandle = NativeMethods.RegisterSuspendResumeNotification(
                Handle,
                NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);

            if (_suspendResumeNotificationHandle == IntPtr.Zero)
            {
                Logger.Warn($"RegisterSuspendResumeNotification failed: {Marshal.GetLastWin32Error()}");
                return;
            }

            Logger.Info($"Registered suspend/resume notifications: Handle={_suspendResumeNotificationHandle}");
        }

        private void UnregisterSuspendResumeNotifications()
        {
            if (_suspendResumeNotificationHandle == IntPtr.Zero) return;

            if (!NativeMethods.UnregisterSuspendResumeNotification(_suspendResumeNotificationHandle))
            {
                Logger.Warn($"UnregisterSuspendResumeNotification failed: {Marshal.GetLastWin32Error()}");
            }
            else
            {
                Logger.Info($"Unregistered suspend/resume notifications: Handle={_suspendResumeNotificationHandle}");
            }

            _suspendResumeNotificationHandle = IntPtr.Zero;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SETTINGCHANGE)
            {
                var section = Marshal.PtrToStringUni(m.LParam);
                Logger.Info($"MessageWindow: WM_SETTINGCHANGE received, section={section ?? "(null)"}");
                if (section == "ImmersiveColorSet")
                {
                    DarkModeHelper.RefreshDarkModeState();
                    ThemeChanged?.Invoke();
                }
            }
            else if (m.Msg == WM_POWERBROADCAST)
            {
                var powerEvent = m.WParam.ToInt32();
                Logger.Info($"MessageWindow: WM_POWERBROADCAST received, event=0x{powerEvent:X} ({GetPowerEventName(powerEvent)})");

                switch (powerEvent)
                {
                    case PBT_APMSUSPEND:
                        PowerSuspending?.Invoke();
                        break;
                    case PBT_APMRESUMECRITICAL:
                    case PBT_APMRESUMEAUTOMATIC:
                    case PBT_APMRESUMESUSPEND:
                        PowerResumed?.Invoke();
                        break;
                }
            }
            base.WndProc(ref m);
        }

        private static string GetPowerEventName(int powerEvent)
        {
            return powerEvent switch
            {
                PBT_APMSUSPEND => nameof(PBT_APMSUSPEND),
                PBT_APMRESUMECRITICAL => nameof(PBT_APMRESUMECRITICAL),
                PBT_APMRESUMEAUTOMATIC => nameof(PBT_APMRESUMEAUTOMATIC),
                PBT_APMRESUMESUSPEND => nameof(PBT_APMRESUMESUSPEND),
                _ => "Unknown"
            };
        }
    }
}
