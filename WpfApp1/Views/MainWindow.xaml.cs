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
using Overseer.ViewModels;
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
        private bool _exitRequested;

        public MainWindow()
        {
            InitializeComponent();

            // Instantiate the ViewModel and bind it to the Window's DataContext
            _viewModel = new MainViewModel();
            DataContext = _viewModel;
            InitializeTrayIcon();
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
            _trayIcon?.Dispose();
            _viewModel.Dispose();
            base.OnClosed(e);
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ExitApplication();
        }

        private void InitializeTrayIcon()
        {
            Forms.ContextMenuStrip menu = new();
            menu.Items.Add("Open Overseer", null, (_, _) => RestoreFromTray());
            menu.Items.Add("Exit", null, (_, _) => ExitApplication());

            _trayIcon = new Forms.NotifyIcon
            {
                Text = "Overseer - Hardware Monitor",
                Icon = System.Drawing.SystemIcons.Application,
                ContextMenuStrip = menu,
                Visible = false
            };
            _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
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
                "Version 1.0.0\n" +
                "Built on\n" +
                "LibreHardwareMonitor\n" +
                "PawnIO\n" +
                ".NET 8\n" +
                "--------------\n" +
                "Created by\n\n" +
                "2026 Alfredo Capella (TechPvnk)\n" +
                "techpvnk@proton.me\n\n" +
                "Panama\n\n" +
                "If you would like to support development:\n" +
                "https://ko-fi.com/techpvnk",
                "About Overseer",
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
            builder.AppendLine("Overseer Hardware Monitor Export");
            builder.AppendLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine();
            builder.AppendLine("[Temperatures]");
            builder.AppendLine($"CPU: {_viewModel.CpuModel}");
            builder.AppendLine($"CPU Temperature: {_viewModel.CpuTemperature}");
            builder.AppendLine($"CPU Min Temperature: {_viewModel.CpuMinTemperature}");
            builder.AppendLine($"CPU Max Temperature: {_viewModel.CpuMaxTemperature}");
            builder.AppendLine($"CPU Usage: {_viewModel.CpuUsage}");
            builder.AppendLine($"CPU Power: {_viewModel.CpuPower}");
            builder.AppendLine($"CPU Min Power: {_viewModel.CpuMinPower}");
            builder.AppendLine($"CPU Max Power: {_viewModel.CpuMaxPower}");
            builder.AppendLine($"GPU: {_viewModel.GpuModel}");
            builder.AppendLine($"GPU Temperature: {_viewModel.GpuTemperature}");
            builder.AppendLine($"GPU Min Temperature: {_viewModel.GpuMinTemperature}");
            builder.AppendLine($"GPU Max Temperature: {_viewModel.GpuMaxTemperature}");
            builder.AppendLine($"GPU Usage: {_viewModel.GpuUsage}");
            builder.AppendLine($"GPU Power: {_viewModel.GpuPower}");
            builder.AppendLine($"GPU Min Power: {_viewModel.GpuMinPower}");
            builder.AppendLine($"GPU Max Power: {_viewModel.GpuMaxPower}");
            AppendDriveTemperatures(builder);
            builder.AppendLine();
            builder.AppendLine("[Disk Health]");

            foreach (var drive in _viewModel.StorageDrives)
            {
                builder.AppendLine($"Drive: {drive.Name}");
                builder.AppendLine($"  Health: {drive.HealthStatus}");
                builder.AppendLine($"  Temperature: {drive.Temperature}");
                builder.AppendLine($"  Life Remaining: {drive.LifeRemaining}");
                builder.AppendLine($"  Interface: {drive.InterfaceType}");
                builder.AppendLine($"  Error Flag: {drive.ErrorFlag}");
                builder.AppendLine($"  Reads: {drive.TotalReads}");
                builder.AppendLine($"  Writes: {drive.TotalWrites}");
                builder.AppendLine($"  Power On Count: {drive.PowerOnCount}");
                builder.AppendLine($"  Power On Hours: {drive.PowerOnHours}");
            }

            builder.AppendLine();
            builder.AppendLine("[System Info]");
            builder.AppendLine($"CPU Clock: {_viewModel.CpuClock}");
            builder.AppendLine($"RAM: {_viewModel.RamInfo}");
            builder.AppendLine($"Motherboard: {_viewModel.Motherboard}");
            builder.AppendLine($"BIOS: {_viewModel.Bios}");
            builder.AppendLine($"OS Version: {_viewModel.OsVersion}");
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

            string selectedHeader = (MainTabControl.SelectedItem as TabItem)?.Header?.ToString() ?? string.Empty;
            return selectedHeader switch
            {
                "Temps" => BuildTempsText(),
                "Disk Health" => BuildDiskHealthText(),
                "System Info" => BuildSystemInfoText(),
                _ => BuildTextExport()
            };
        }

        private string BuildTempsText()
        {
            StringBuilder builder = new();
            builder.AppendLine("Overseer Hardware Monitor Export");
            builder.AppendLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine();
            builder.AppendLine("[Temperatures]");
            builder.AppendLine($"CPU: {_viewModel.CpuModel}");
            builder.AppendLine($"CPU Temperature: {_viewModel.CpuTemperature}");
            builder.AppendLine($"CPU Min Temperature: {_viewModel.CpuMinTemperature}");
            builder.AppendLine($"CPU Max Temperature: {_viewModel.CpuMaxTemperature}");
            builder.AppendLine($"CPU Usage: {_viewModel.CpuUsage}");
            builder.AppendLine($"CPU Power: {_viewModel.CpuPower}");
            builder.AppendLine($"CPU Min Power: {_viewModel.CpuMinPower}");
            builder.AppendLine($"CPU Max Power: {_viewModel.CpuMaxPower}");
            builder.AppendLine($"GPU: {_viewModel.GpuModel}");
            builder.AppendLine($"GPU Temperature: {_viewModel.GpuTemperature}");
            builder.AppendLine($"GPU Min Temperature: {_viewModel.GpuMinTemperature}");
            builder.AppendLine($"GPU Max Temperature: {_viewModel.GpuMaxTemperature}");
            builder.AppendLine($"GPU Usage: {_viewModel.GpuUsage}");
            builder.AppendLine($"GPU Power: {_viewModel.GpuPower}");
            builder.AppendLine($"GPU Min Power: {_viewModel.GpuMinPower}");
            builder.AppendLine($"GPU Max Power: {_viewModel.GpuMaxPower}");
            AppendDriveTemperatures(builder);
            return builder.ToString();
        }

        private string BuildDiskHealthText()
        {
            StringBuilder builder = new();
            builder.AppendLine("Overseer Hardware Monitor Export");
            builder.AppendLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine();
            builder.AppendLine("[Disk Health]");

            foreach (var drive in _viewModel.StorageDrives)
            {
                builder.AppendLine($"Drive: {drive.Name}");
                builder.AppendLine($"  Health: {drive.HealthStatus}");
                builder.AppendLine($"  Temperature: {drive.Temperature}");
                builder.AppendLine($"  Life Remaining: {drive.LifeRemaining}");
                builder.AppendLine($"  Interface: {drive.InterfaceType}");
                builder.AppendLine($"  Error Flag: {drive.ErrorFlag}");
                builder.AppendLine($"  Reads: {drive.TotalReads}");
                builder.AppendLine($"  Writes: {drive.TotalWrites}");
                builder.AppendLine($"  Power On Count: {drive.PowerOnCount}");
                builder.AppendLine($"  Power On Hours: {drive.PowerOnHours}");
            }

            return builder.ToString();
        }

        private string BuildSystemInfoText()
        {
            StringBuilder builder = new();
            builder.AppendLine("Overseer Hardware Monitor Export");
            builder.AppendLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine();
            builder.AppendLine("[System Info]");
            builder.AppendLine($"CPU Clock: {_viewModel.CpuClock}");
            builder.AppendLine($"RAM: {_viewModel.RamInfo}");
            builder.AppendLine($"Motherboard: {_viewModel.Motherboard}");
            builder.AppendLine($"BIOS: {_viewModel.Bios}");
            builder.AppendLine($"OS Version: {_viewModel.OsVersion}");
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
            AddCsvRow(builder, "Temps", "GPU", _viewModel.GpuModel);
            AddCsvRow(builder, "Temps", "GPU Temperature", _viewModel.GpuTemperature);
            AddCsvRow(builder, "Temps", "GPU Min Temperature", _viewModel.GpuMinTemperature);
            AddCsvRow(builder, "Temps", "GPU Max Temperature", _viewModel.GpuMaxTemperature);
            AddCsvRow(builder, "Temps", "GPU Usage", _viewModel.GpuUsage);
            AddCsvRow(builder, "Temps", "GPU Power", _viewModel.GpuPower);
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
            }

            AddCsvRow(builder, "System Info", "CPU Clock", _viewModel.CpuClock);
            AddCsvRow(builder, "System Info", "RAM", _viewModel.RamInfo);
            AddCsvRow(builder, "System Info", "Motherboard", _viewModel.Motherboard);
            AddCsvRow(builder, "System Info", "BIOS", _viewModel.Bios);
            AddCsvRow(builder, "System Info", "OS Version", _viewModel.OsVersion);
            return builder.ToString();
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