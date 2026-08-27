using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Overseer.Services;
using Overseer.ViewModels;
using Overseer.Views;
using Forms = System.Windows.Forms;

namespace Overseer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private Forms.NotifyIcon? _trayIcon;
        private System.Drawing.Icon? _trayIconGraphic;
        private Forms.ToolStripMenuItem? _trayOpenMenuItem;
        private Forms.ToolStripMenuItem? _trayExitMenuItem;
        private readonly SidebarSettingsService _sidebarSettingsService = SidebarSettingsService.Instance;
        private SidebarWindow? _sidebarWindow;
        private bool _exitRequested;

        public MainWindow()
        {
            InitializeComponent();

            // Instantiate the ViewModel and bind it to the Window's DataContext
            _viewModel = new MainViewModel();
            DataContext = _viewModel;
            SidebarClickThroughMenuItem.IsChecked = _sidebarSettingsService.Settings.IsClickThrough;
            InitializeTrayIcon();
            LocalizationService.Instance.PropertyChanged += LocalizationChanged;
            UpdateLocalizedChrome();
            Loaded += MainWindow_Loaded;
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);

            if (WindowState == WindowState.Minimized && MinimizeToTrayMenuItem.IsChecked)
            {
                HideToTray();
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_exitRequested && MinimizeToTrayMenuItem.IsChecked)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            Loaded -= MainWindow_Loaded;
            LocalizationService.Instance.PropertyChanged -= LocalizationChanged;
            if (_sidebarWindow is not null)
            {
                _sidebarWindow.Closed -= SidebarWindow_Closed;
                _sidebarWindow.ClickThroughChanged -= SidebarWindow_ClickThroughChanged;
                _sidebarWindow.Close();
                _sidebarWindow = null;
            }
            _trayIcon?.Dispose();
            _trayIconGraphic?.Dispose();
            _viewModel.Dispose();
            base.OnClosed(e);
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_sidebarSettingsService.Settings.IsOpen)
            {
                OpenSidebarMode();
            }
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ExitApplication();
        }

        private void InitializeTrayIcon()
        {
            Forms.ContextMenuStrip menu = new();
            _trayOpenMenuItem = new Forms.ToolStripMenuItem();
            _trayOpenMenuItem.Click += (_, _) => RestoreFromTray();
            _trayExitMenuItem = new Forms.ToolStripMenuItem();
            _trayExitMenuItem.Click += (_, _) => ExitApplication();
            menu.Items.Add(_trayOpenMenuItem);
            menu.Items.Add(_trayExitMenuItem);

            _trayIcon = new Forms.NotifyIcon
            {
                Text = L("AppTitle"),
                Icon = LoadTrayIcon() ?? System.Drawing.SystemIcons.Application,
                ContextMenuStrip = menu,
                Visible = false
            };
            _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        }

        private System.Drawing.Icon? LoadTrayIcon()
        {
            try
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "Themes", "favicon.ico");
                if (File.Exists(iconPath))
                {
                    _trayIconGraphic = new System.Drawing.Icon(iconPath);
                    return _trayIconGraphic;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("Unable to load Overseer favicon for the tray icon.", ex);
            }

            return null;
        }

        private void MinimizeToTrayMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (MinimizeToTrayMenuItem.IsChecked && WindowState == WindowState.Minimized)
            {
                HideToTray();
            }
            else if (!MinimizeToTrayMenuItem.IsChecked && _trayIcon is not null)
            {
                _trayIcon.Visible = false;
            }
        }

        private void SidebarModeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            SetSidebarModeOpen(SidebarModeMenuItem.IsChecked);
        }

        private void SidebarModeHeaderButton_Click(object sender, RoutedEventArgs e)
        {
            SetSidebarModeOpen(SidebarModeHeaderButton.IsChecked == true);
        }

        private void SidebarClickThroughMenuItem_Click(object sender, RoutedEventArgs e)
        {
            bool isClickThrough = SidebarClickThroughMenuItem.IsChecked;
            if (_sidebarWindow is not null)
            {
                _sidebarWindow.IsClickThrough = isClickThrough;
            }
            else
            {
                _sidebarSettingsService.Settings.IsClickThrough = isClickThrough;
                _sidebarSettingsService.Save();
            }
        }

        private void SetSidebarModeOpen(bool isOpen)
        {
            if (isOpen)
            {
                OpenSidebarMode();
            }
            else
            {
                _sidebarWindow?.Close();
            }
        }

        private void OpenSidebarMode()
        {
            if (_sidebarWindow is null)
            {
                SidebarViewModel sidebarViewModel = new(_viewModel, _sidebarSettingsService);
                _sidebarWindow = new SidebarWindow(sidebarViewModel);
                _sidebarWindow.Closed += SidebarWindow_Closed;
                _sidebarWindow.ClickThroughChanged += SidebarWindow_ClickThroughChanged;
                _sidebarWindow.Show();
            }
            else
            {
                _sidebarWindow.Show();
                if (!_sidebarWindow.IsClickThrough)
                {
                    _sidebarWindow.Activate();
                }
            }

            _sidebarSettingsService.Settings.IsOpen = true;
            _sidebarSettingsService.Save();
            UpdateSidebarModeState(true);
            SidebarClickThroughMenuItem.IsChecked = _sidebarWindow.IsClickThrough;
            AppLog.Write("Sidebar Mode opened.");
        }

        private void SidebarWindow_Closed(object? sender, EventArgs e)
        {
            if (_sidebarWindow is not null)
            {
                _sidebarWindow.Closed -= SidebarWindow_Closed;
                _sidebarWindow.ClickThroughChanged -= SidebarWindow_ClickThroughChanged;
                _sidebarWindow = null;
            }

            _sidebarSettingsService.Settings.IsOpen = false;
            _sidebarSettingsService.Save();
            UpdateSidebarModeState(false);
            AppLog.Write("Sidebar Mode closed.");
        }

        private void SidebarWindow_ClickThroughChanged(object? sender, EventArgs e)
        {
            if (_sidebarWindow is not null)
            {
                SidebarClickThroughMenuItem.IsChecked = _sidebarWindow.IsClickThrough;
            }
        }

        private void UpdateSidebarModeState(bool isOpen)
        {
            SidebarModeMenuItem.IsChecked = isOpen;
            SidebarModeHeaderButton.IsChecked = isOpen;
        }

        private void HideToTray()
        {
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = true;
            }

            Hide();
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();

            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
            }
        }

        private void ExitApplication()
        {
            _exitRequested = true;
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
            }

            Close();
        }

        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                L("AboutMessage"),
                L("AboutTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }


        private void ExportTxtMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ExportData("txt");
        }

        private void ExportCsvMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ExportData("csv");
        }

        private void CopyCurrentTabMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(BuildCurrentTabText());
        }

        private void CopyAllTabsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.RefreshData();
            Clipboard.SetText(BuildTextExport());
        }
        private void ExportScreenshotMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ExportScreenshot();
        }

        private void ExportScreenshot()
        {
            if (ActualWidth <= 0 || ActualHeight <= 0)
            {
                return;
            }

            SaveFileDialog dialog = new()
            {
                FileName = $"overseer-screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png",
                Filter = "PNG image (*.png)|*.png",
                DefaultExt = "png"
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            RenderTargetBitmap bitmap = new(
                (int)Math.Ceiling(ActualWidth),
                (int)Math.Ceiling(ActualHeight),
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(this);

            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using FileStream stream = File.Create(dialog.FileName);
            encoder.Save(stream);
        }
        private void ExportData(string format)
        {
            _viewModel.RefreshData();

            SaveFileDialog dialog = new()
            {
                FileName = $"overseer-export-{DateTime.Now:yyyyMMdd-HHmmss}.{format}",
                Filter = format == "csv" ? "CSV files (*.csv)|*.csv" : "Text files (*.txt)|*.txt",
                DefaultExt = format
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            string content = format == "csv" ? BuildCsvExport() : BuildTextExport();
            File.WriteAllText(dialog.FileName, content, Encoding.UTF8);
        }

        private string BuildTextExport()
        {
            StringBuilder builder = new();
            AppendExportHeader(builder);
            AppendTempsSection(builder);
            builder.AppendLine();
            AppendDiskHealthSection(builder);
            builder.AppendLine();
            AppendSystemInfoSection(builder);
            return builder.ToString();
        }

        private void AppendDriveTemperatures(StringBuilder builder)
        {
            if (_viewModel.DriveTemperatures.Count == 0)
            {
                builder.AppendLine("Drives: N/A");
                return;
            }

            builder.AppendLine("Drives:");
            foreach (var drive in _viewModel.DriveTemperatures)
            {
                builder.AppendLine($"  {drive.Name}: {drive.Temperature} (Min {drive.MinTemperature}, Max {drive.MaxTemperature})");
            }
        }
        private string BuildCurrentTabText()
        {
            _viewModel.RefreshData();

            string selectedTab = (MainTabControl.SelectedItem as TabItem)?.Tag?.ToString() ?? string.Empty;
            return selectedTab switch
            {
                "Temps" => BuildTempsText(),
                "DiskHealth" => BuildDiskHealthText(),
                "SystemInfo" => BuildSystemInfoText(),
                _ => BuildTextExport()
            };
        }

        private void EnglishMenuItem_Click(object sender, RoutedEventArgs e)
        {
            LocalizationService.Instance.SetCulture("en");
        }

        private void SpanishMenuItem_Click(object sender, RoutedEventArgs e)
        {
            LocalizationService.Instance.SetCulture("es");
        }

        private void LocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Item[]" || e.PropertyName == nameof(LocalizationService.Culture))
            {
                Dispatcher.Invoke(UpdateLocalizedChrome);
            }
        }

        private void AppendExportHeader(StringBuilder builder)
        {
            builder.AppendLine("Overseer Hardware Monitor Export");
            builder.AppendLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine();
        }

        private void AppendTempsSection(StringBuilder builder)
        {
            builder.AppendLine("[Temperatures]");
            builder.AppendLine($"CPU: {_viewModel.CpuModel}");
            builder.AppendLine($"CPU Temperature: {_viewModel.CpuTemperature}");
            builder.AppendLine($"CPU Min Temperature: {_viewModel.CpuMinTemperature}");
            builder.AppendLine($"CPU Max Temperature: {_viewModel.CpuMaxTemperature}");
            builder.AppendLine($"CPU Usage: {_viewModel.CpuUsage}");
            builder.AppendLine($"CPU Power: {_viewModel.CpuPower}");
            builder.AppendLine($"CPU Min Power: {_viewModel.CpuMinPower}");
            builder.AppendLine($"CPU Max Power: {_viewModel.CpuMaxPower}");
            builder.AppendLine();
            builder.AppendLine($"GPU: {_viewModel.GpuModel}");
            builder.AppendLine($"GPU Temperature: {_viewModel.GpuTemperature}");
            builder.AppendLine($"GPU Min Temperature: {_viewModel.GpuMinTemperature}");
            builder.AppendLine($"GPU Max Temperature: {_viewModel.GpuMaxTemperature}");
            builder.AppendLine($"GPU Usage: {_viewModel.GpuUsage}");
            builder.AppendLine($"GPU Power: {_viewModel.GpuPower}");
            builder.AppendLine($"GPU Min Power: {_viewModel.GpuMinPower}");
            builder.AppendLine($"GPU Max Power: {_viewModel.GpuMaxPower}");
            builder.AppendLine();
            builder.AppendLine($"RAM Total: {_viewModel.RamTotal}");
            builder.AppendLine($"RAM Used: {_viewModel.RamUsed}");
            builder.AppendLine($"RAM Available: {_viewModel.RamAvailable}");
            builder.AppendLine($"RAM Utilization: {_viewModel.RamUsage}");
            builder.AppendLine($"RAM Temperature: {_viewModel.RamTemperature}");
            AppendDriveTemperatures(builder);
        }

        private void AppendDiskHealthSection(StringBuilder builder)
        {
            builder.AppendLine("[Disk Health]");
            foreach (MainViewModel.StorageDriveViewModel drive in _viewModel.StorageDrives)
            {
                builder.AppendLine($"Drive: {drive.Name}");
                builder.AppendLine($"  Health: {drive.HealthStatus}");
                builder.AppendLine($"  Temperature: {drive.Temperature}");
                builder.AppendLine($"  Life Remaining: {drive.LifeRemaining}");
                builder.AppendLine($"  Interface: {drive.InterfaceType}");
                builder.AppendLine($"  Error Flag: {drive.ErrorFlag}");
                builder.AppendLine($"  Total Host Reads: {drive.TotalReads}");
                builder.AppendLine($"  Total Host Writes: {drive.TotalWrites}");
                builder.AppendLine($"  Power On Count: {drive.PowerOnCount}");
                builder.AppendLine($"  Power On Hours: {drive.PowerOnHours}");
                AppendSmartctlDetails(builder, drive);
                builder.AppendLine();
            }
        }

        private static void AppendSmartctlDetails(StringBuilder builder, MainViewModel.StorageDriveViewModel drive)
        {
            SmartctlDriveReport? report = drive.SmartctlData;
            if (report == null)
            {
                builder.AppendLine($"  SMARTCTL: {drive.SmartctlStatus}");
                return;
            }

            builder.AppendLine($"  SMARTCTL: {report.StatusMessage ?? (report.IsAvailable ? "Available" : "Unavailable")}");
            builder.AppendLine($"  Firmware: {report.FirmwareVersion ?? "N/A"}");
            builder.AppendLine($"  Serial: {report.SerialNumber ?? "N/A"}");
            builder.AppendLine($"  Capacity: {report.Capacity ?? "N/A"}");
            builder.AppendLine($"  Protocol: {report.Protocol ?? "N/A"}");
            builder.AppendLine($"  SMART Passed: {report.SmartPassed?.ToString() ?? "N/A"}");

            if (report.NvmeHealth is not null)
            {
                builder.AppendLine("  [NVMe SMART / Health Attributes]");
                foreach (SmartctlNvmeAttribute attribute in report.NvmeHealth.Attributes)
                {
                    builder.AppendLine($"    {attribute.Id} | {attribute.Name} | Current: {attribute.Current} | Threshold: {attribute.Threshold} | Raw: {attribute.RawValue}");
                }
            }
            else if (report.AtaAttributes.Count > 0)
            {
                builder.AppendLine("  [SATA SMART Attributes]");
                foreach (SmartctlAtaAttribute attribute in report.AtaAttributes)
                {
                    builder.AppendLine($"    {attribute.Id} | {attribute.Name} | Value: {attribute.Value} | Worst: {attribute.Worst} | Threshold: {attribute.Threshold} | Raw: {attribute.RawValue}");
                }
            }
        }

        private void AppendSystemInfoSection(StringBuilder builder)
        {
            builder.AppendLine("[System Info]");
            builder.AppendLine($"CPU: {_viewModel.CpuModel}");
            builder.AppendLine($"CPU Clock: {_viewModel.CpuClock}");
            builder.AppendLine($"CPU Cores/Threads: {_viewModel.CpuCoresThreads}");
            builder.AppendLine($"CPU Caches: {_viewModel.CpuCaches}");
            builder.AppendLine($"CPU TDP: {_viewModel.CpuTdp}");
            builder.AppendLine();
            builder.AppendLine($"RAM: {_viewModel.RamInfo}");
            builder.AppendLine($"RAM Type: {_viewModel.RamType}");
            builder.AppendLine($"RAM Clock: {_viewModel.RamClock}");
            foreach (string module in _viewModel.RamModulesList)
            {
                builder.AppendLine($"RAM Module: {module}");
            }
            builder.AppendLine();
            builder.AppendLine($"GPU: {_viewModel.GpuModel}");
            builder.AppendLine($"GPU Clock: {_viewModel.GpuClock}");
            builder.AppendLine($"GPU RAM: {_viewModel.GpuRam}");
            builder.AppendLine($"GPU Memory Total: {_viewModel.GpuMemoryTotal}");
            builder.AppendLine($"GPU Memory Used: {_viewModel.GpuMemoryUsed}");
            builder.AppendLine($"GPU Memory Free: {_viewModel.GpuMemoryFree}");
            builder.AppendLine($"GPU Bus: {_viewModel.GpuBus}");
            builder.AppendLine();
            builder.AppendLine($"Motherboard: {_viewModel.Motherboard}");
            foreach (string hardware in _viewModel.MotherboardSubHardware)
            {
                builder.AppendLine($"Motherboard Component: {hardware}");
            }
            builder.AppendLine($"BIOS: {_viewModel.Bios}");
            builder.AppendLine($"Operating System: {_viewModel.OsVersion}");
            builder.AppendLine($"Battery: {_viewModel.BatteryInfo}");
            foreach (string graphics in _viewModel.GraphicsDevices)
            {
                builder.AppendLine($"Graphics: {graphics}");
            }
            foreach (string audio in _viewModel.AudioDevices)
            {
                builder.AppendLine($"Audio: {audio}");
            }
            foreach (NetworkAdapterInfo adapter in _viewModel.NetworkAdapters)
            {
                builder.AppendLine($"Network Adapter: {adapter.Name}");
                builder.AppendLine($"  Description: {adapter.Description}");
                builder.AppendLine($"  Type: {adapter.InterfaceType}");
                builder.AppendLine($"  Status: Connected");
                builder.AppendLine($"  Link Speed: {adapter.LinkSpeed}");
                builder.AppendLine($"  IPv4: {adapter.Ipv4Address}");
                builder.AppendLine($"  IPv6: {adapter.Ipv6Address}");
                builder.AppendLine($"  MAC: {adapter.MacAddress}");
            }
        }

        private void UpdateLocalizedChrome()
        {
            EnglishMenuItem.IsChecked = LocalizationService.Instance.IsEnglish;
            SpanishMenuItem.IsChecked = LocalizationService.Instance.IsSpanish;

            if (_trayIcon is not null)
            {
                _trayIcon.Text = L("AppTitle");
            }

            if (_trayOpenMenuItem is not null)
            {
                _trayOpenMenuItem.Text = L("TrayOpen");
            }

            if (_trayExitMenuItem is not null)
            {
                _trayExitMenuItem.Text = L("CommandExit");
            }
        }

        private static string L(string key) => LocalizationService.Instance[key];

        private string BuildTempsText()
        {
            StringBuilder builder = new();
            AppendExportHeader(builder);
            AppendTempsSection(builder);
            return builder.ToString();
        }

        private string BuildDiskHealthText()
        {
            StringBuilder builder = new();
            AppendExportHeader(builder);
            AppendDiskHealthSection(builder);
            return builder.ToString();
        }

        private string BuildSystemInfoText()
        {
            StringBuilder builder = new();
            AppendExportHeader(builder);
            AppendSystemInfoSection(builder);
            return builder.ToString();
        }
        private string BuildCsvExport()
        {
            StringBuilder builder = new();
            builder.AppendLine("Section,Name,Value");
            AddCsvRow(builder, "Temps", "CPU", _viewModel.CpuModel);
            AddCsvRow(builder, "Temps", "CPU Temperature", _viewModel.CpuTemperature);
            AddCsvRow(builder, "Temps", "CPU Min Temperature", _viewModel.CpuMinTemperature);
            AddCsvRow(builder, "Temps", "CPU Max Temperature", _viewModel.CpuMaxTemperature);
            AddCsvRow(builder, "Temps", "CPU Usage", _viewModel.CpuUsage);
            AddCsvRow(builder, "Temps", "CPU Power", _viewModel.CpuPower);
            AddCsvRow(builder, "Temps", "CPU Min Power", _viewModel.CpuMinPower);
            AddCsvRow(builder, "Temps", "CPU Max Power", _viewModel.CpuMaxPower);
            AddCsvRow(builder, "Temps", "GPU", _viewModel.GpuModel);
            AddCsvRow(builder, "Temps", "GPU Temperature", _viewModel.GpuTemperature);
            AddCsvRow(builder, "Temps", "GPU Min Temperature", _viewModel.GpuMinTemperature);
            AddCsvRow(builder, "Temps", "GPU Max Temperature", _viewModel.GpuMaxTemperature);
            AddCsvRow(builder, "Temps", "GPU Usage", _viewModel.GpuUsage);
            AddCsvRow(builder, "Temps", "GPU Power", _viewModel.GpuPower);
            AddCsvRow(builder, "Temps", "GPU Min Power", _viewModel.GpuMinPower);
            AddCsvRow(builder, "Temps", "GPU Max Power", _viewModel.GpuMaxPower);
            AddCsvRow(builder, "Temps", "RAM Total", _viewModel.RamTotal);
            AddCsvRow(builder, "Temps", "RAM Used", _viewModel.RamUsed);
            AddCsvRow(builder, "Temps", "RAM Available", _viewModel.RamAvailable);
            AddCsvRow(builder, "Temps", "RAM Utilization", _viewModel.RamUsage);
            AddCsvRow(builder, "Temps", "RAM Temperature", _viewModel.RamTemperature);
            foreach (var drive in _viewModel.DriveTemperatures)
            {
                string section = $"Temps - {drive.Name}";
                AddCsvRow(builder, section, "Temperature", drive.Temperature);
                AddCsvRow(builder, section, "Min Temperature", drive.MinTemperature);
                AddCsvRow(builder, section, "Max Temperature", drive.MaxTemperature);
            }

            foreach (var drive in _viewModel.StorageDrives)
            {
                string section = $"Disk Health - {drive.Name}";
                AddCsvRow(builder, section, "Health", drive.HealthStatus);
                AddCsvRow(builder, section, "Temperature", drive.Temperature);
                AddCsvRow(builder, section, "Life Remaining", drive.LifeRemaining);
                AddCsvRow(builder, section, "Interface", drive.InterfaceType);
                AddCsvRow(builder, section, "Error Flag", drive.ErrorFlag);
                AddCsvRow(builder, section, "Reads", drive.TotalReads);
                AddCsvRow(builder, section, "Writes", drive.TotalWrites);
                AddCsvRow(builder, section, "Power On Count", drive.PowerOnCount);
                AddCsvRow(builder, section, "Power On Hours", drive.PowerOnHours);
                AddSmartctlCsvRows(builder, section, drive);
            }

            AddCsvRow(builder, "System Info", "CPU", _viewModel.CpuModel);
            AddCsvRow(builder, "System Info", "CPU Clock", _viewModel.CpuClock);
            AddCsvRow(builder, "System Info", "CPU Cores/Threads", _viewModel.CpuCoresThreads);
            AddCsvRow(builder, "System Info", "CPU Caches", _viewModel.CpuCaches);
            AddCsvRow(builder, "System Info", "CPU TDP", _viewModel.CpuTdp);
            AddCsvRow(builder, "System Info", "RAM", _viewModel.RamInfo);
            AddCsvRow(builder, "System Info", "RAM Type", _viewModel.RamType);
            AddCsvRow(builder, "System Info", "RAM Clock", _viewModel.RamClock);
            foreach (string module in _viewModel.RamModulesList)
            {
                AddCsvRow(builder, "System Info", "RAM Module", module);
            }
            AddCsvRow(builder, "System Info", "GPU", _viewModel.GpuModel);
            AddCsvRow(builder, "System Info", "GPU Clock", _viewModel.GpuClock);
            AddCsvRow(builder, "System Info", "GPU RAM", _viewModel.GpuRam);
            AddCsvRow(builder, "System Info", "GPU Memory Total", _viewModel.GpuMemoryTotal);
            AddCsvRow(builder, "System Info", "GPU Memory Used", _viewModel.GpuMemoryUsed);
            AddCsvRow(builder, "System Info", "GPU Memory Free", _viewModel.GpuMemoryFree);
            AddCsvRow(builder, "System Info", "GPU Bus", _viewModel.GpuBus);
            AddCsvRow(builder, "System Info", "Motherboard", _viewModel.Motherboard);
            foreach (string hardware in _viewModel.MotherboardSubHardware)
            {
                AddCsvRow(builder, "System Info", "Motherboard Component", hardware);
            }
            AddCsvRow(builder, "System Info", "BIOS", _viewModel.Bios);
            AddCsvRow(builder, "System Info", "OS Version", _viewModel.OsVersion);
            AddCsvRow(builder, "System Info", "Battery", _viewModel.BatteryInfo);
            foreach (string graphics in _viewModel.GraphicsDevices)
            {
                AddCsvRow(builder, "System Info", "Graphics", graphics);
            }
            foreach (string audio in _viewModel.AudioDevices)
            {
                AddCsvRow(builder, "System Info", "Audio", audio);
            }
            foreach (NetworkAdapterInfo adapter in _viewModel.NetworkAdapters)
            {
                string section = $"System Info - Network - {adapter.Name}";
                AddCsvRow(builder, section, "Description", adapter.Description);
                AddCsvRow(builder, section, "Type", adapter.InterfaceType);
                AddCsvRow(builder, section, "Status", "Connected");
                AddCsvRow(builder, section, "Link Speed", adapter.LinkSpeed);
                AddCsvRow(builder, section, "IPv4", adapter.Ipv4Address);
                AddCsvRow(builder, section, "IPv6", adapter.Ipv6Address);
                AddCsvRow(builder, section, "MAC", adapter.MacAddress);
            }
            return builder.ToString();
        }

        private static void AddSmartctlCsvRows(StringBuilder builder, string section, MainViewModel.StorageDriveViewModel drive)
        {
            SmartctlDriveReport? report = drive.SmartctlData;
            if (report == null)
            {
                AddCsvRow(builder, section, "SMARTCTL Status", drive.SmartctlStatus);
                return;
            }

            AddCsvRow(builder, section, "SMARTCTL Status", report.StatusMessage ?? (report.IsAvailable ? "Available" : "Unavailable"));
            AddCsvRow(builder, section, "SMARTCTL Firmware", report.FirmwareVersion ?? "N/A");
            AddCsvRow(builder, section, "SMARTCTL Serial", report.SerialNumber ?? "N/A");
            AddCsvRow(builder, section, "SMARTCTL Capacity", report.Capacity ?? "N/A");
            AddCsvRow(builder, section, "SMARTCTL Protocol", report.Protocol ?? "N/A");
            AddCsvRow(builder, section, "SMARTCTL Passed", report.SmartPassed?.ToString() ?? "N/A");

            if (report.NvmeHealth is not null)
            {
                foreach (SmartctlNvmeAttribute attribute in report.NvmeHealth.Attributes)
                {
                    AddCsvRow(builder, section, $"NVMe {attribute.Id} {attribute.Name}", $"Current={attribute.Current}; Threshold={attribute.Threshold}; Raw={attribute.RawValue}");
                }
            }
            else
            {
                foreach (SmartctlAtaAttribute attribute in report.AtaAttributes)
                {
                    AddCsvRow(builder, section, $"SATA {attribute.Id} {attribute.Name}", $"Value={attribute.Value}; Worst={attribute.Worst}; Threshold={attribute.Threshold}; Raw={attribute.RawValue}");
                }
            }
        }

        private static void AddCsvRow(StringBuilder builder, string section, string name, string value)
        {
            builder.AppendLine($"{EscapeCsv(section)},{EscapeCsv(name)},{EscapeCsv(value)}");
        }

        private static string EscapeCsv(string value)
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        private void ResetStatisticsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ResetStatistics();
        }

        private void RefreshSystemInformationMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.RefreshSystemInformation();
        }

        private void OpenLogMenuItem_Click(object sender, RoutedEventArgs e)
        {
            AppLog.Open();
        }

        private void AlertSoundMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                _viewModel.AudioAlertsEnabled = menuItem.IsChecked;
            }
        }
        private void AlwaysOnTopMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                Topmost = menuItem.IsChecked;
            }
        }


        private void CelsiusMenuItem_Click(object sender, RoutedEventArgs e)
        {
            CelsiusMenuItem.IsChecked = true;
            FahrenheitMenuItem.IsChecked = false;
            _viewModel.SetTemperatureUnit(false);
        }

        private void FahrenheitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            CelsiusMenuItem.IsChecked = false;
            FahrenheitMenuItem.IsChecked = true;
            _viewModel.SetTemperatureUnit(true);
        }
        protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            if (HandleKeyboardShortcut(e))
            {
                return;
            }

            base.OnPreviewKeyDown(e);
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            HandleKeyboardShortcut(e);
        }

        private bool HandleKeyboardShortcut(System.Windows.Input.KeyEventArgs e)
        {
            if (e.Handled)
            {
                return true;
            }

            bool controlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            bool altPressed = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            key = key == Key.ImeProcessed ? e.ImeProcessedKey : key;

            if (controlPressed && shiftPressed && key == Key.C)
            {
                Clipboard.SetText(BuildCurrentTabText());
                e.Handled = true;
                return true;
            }

            if (controlPressed && altPressed && key == Key.C)
            {
                _viewModel.RefreshData();
                Clipboard.SetText(BuildTextExport());
                e.Handled = true;
                return true;
            }

            if (controlPressed && key == Key.S)
            {
                ExportData("txt");
                e.Handled = true;
                return true;
            }

            if (key == Key.F5)
            {
                _viewModel.ResetStatistics();
                e.Handled = true;
                return true;
            }

            return false;
        }
        private void DocumentationMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://github.com/TechPvnk/OverseerHardwareMonitor#readme");
        }

        private void TechPvnkMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://beacons.ai/techpvnk");
        }

        private void ReadingsHelpMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://overseer.techpvnk.com");
        }

        private void CheckForUpdatesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://github.com/TechPvnk/OverseerHardwareMonitor/releases/latest");
        }

        private static void OpenUrl(string url)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
    }
}
