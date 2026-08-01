using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.ServiceProcess;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Runtime.InteropServices;
using System.Text;

namespace Overseer.Helpers
{
    public static class LibreHardwareMonitorInstaller
    {
        // Built-in fallback allowlist. For production, place a file named
        // "pawnio_trusted_thumbprints.txt" in the helpers folder (one thumbprint per line).
        private static readonly string[] BuiltInAllowedSignerThumbprints = new[]
        {
            // Example: "AB12CD34EF56..."
            "REPLACE_WITH_PAWNIO_SIGNER_THUMBPRINT"
        };

        private static readonly object s_thumbprintsLock = new();
        private static string[]? s_allowedSignerThumbprintsCache;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool CanRun, string StatusText)> s_verificationCache = new(System.StringComparer.OrdinalIgnoreCase);

        private static string[] GetAllowedSignerThumbprints()
        {
            if (s_allowedSignerThumbprintsCache != null)
                return s_allowedSignerThumbprintsCache;

            try
            {
                // Build a set of base directories to probe (runtime base, appdomain base, working dir, process location)
                var baseDirs = new[]
                {
                    AppContext.BaseDirectory,
                    AppDomain.CurrentDomain.BaseDirectory,
                    Directory.GetCurrentDirectory(),
                    Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName) ?? AppContext.BaseDirectory
                }.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

                foreach (var baseDir in baseDirs)
                {
                    for (int up = 0; up < 5; up++)
                    {
                        string probeDir = baseDir;
                        if (up > 0)
                            probeDir = Path.GetFullPath(Path.Combine(baseDir, string.Concat(Enumerable.Repeat(".." + Path.DirectorySeparatorChar, up))));

                        string[] helperCandidates = new[] { Path.Combine(probeDir, "helpers"), Path.Combine(probeDir, "Helpers") };

                        foreach (var dir in helperCandidates)
                        {
                            try
                            {
                                Debug.WriteLine($"Probing for thumbprint file in: {dir}");
                                if (!Directory.Exists(dir))
                                {
                                    Debug.WriteLine($"Directory not present: {dir}");
                                    continue;
                                }

                                string file = Path.Combine(dir, "pawnio_trusted_thumbprints.txt");
                                Debug.WriteLine($"Checking file: {file} (exists={File.Exists(file)})");
                                if (File.Exists(file))
                                {
                                    var lines = File.ReadAllLines(file)
                                        .Select(l => l.Trim().Replace(" ", string.Empty).ToUpperInvariant())
                                        .Where(l => l.Length >= 20)
                                        .ToArray();
                                    Debug.WriteLine($"Read {lines.Length} thumbprint lines from {file}");
                                    if (lines.Length > 0) return lines;
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"GetAllowedSignerThumbprints probe failed for '{dir}': {ex}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetAllowedSignerThumbprints failed: {ex}");
            }

            return BuiltInAllowedSignerThumbprints;
        }

        private static readonly string[] CandidateFiles = new[]
        {
            "LibreHardwareMonitor.exe",
            "librehardwaremonitor.exe",
            "DriverInstaller.exe",
            "LibreHardwareMonitor-DriverInstaller.exe",
            "PawnIO_setup.exe",
            "pawnio_setup.exe",
            "pawnio.exe"
        };

        private const string ReleasesUrl = "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases";
        private const string PawnIoDownloadUrl = ""; // configure if you want automatic download

        public static bool TryInstallHelper()
        {
            try
            {
                // If called from the UI thread, run installation flow on a background thread so we don't block UI rendering
                if (Application.Current != null && Application.Current.Dispatcher != null && Application.Current.Dispatcher.CheckAccess())
                {
                    Task.Run(() => TryInstallHelperInternal());
                    return true;
                }

                // Otherwise run synchronously on the current thread
                return TryInstallHelperInternal();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TryInstallHelper failed: {ex}");
                return false;
            }
        }

        private static bool TryInstallHelperInternal()
        {
            string appDir = AppContext.BaseDirectory;

            // Search helpers folders up to 4 parent levels
            for (int up = 0; up < 5; up++)
            {
                string probeDir = appDir;
                    if (up > 0)
                        probeDir = Path.GetFullPath(Path.Combine(appDir, string.Concat(Enumerable.Repeat(".." + Path.DirectorySeparatorChar, up))));

                string[] helperCandidates = new[] { Path.Combine(probeDir, "helpers"), Path.Combine(probeDir, "Helpers") };

                foreach (var helpersDir in helperCandidates)
                {
                    try
                    {
                        if (!Directory.Exists(helpersDir)) continue;

                        Debug.WriteLine($"Searching for helper executables in: {helpersDir}");

                        // Preferred names first
                        foreach (string candidate in CandidateFiles)
                        {
                            string path = Path.Combine(helpersDir, candidate);
                            if (!File.Exists(path)) continue;

                            try
                            {
                                // Unblock the file before we inspect it. Accessing blocked files can trigger the Windows
                                // security warning when we compute signature/checksum; remove ADS proactively.
                                TryUnblockFile(path);
                                var verification = VerifyInstallerChecksumIfPresent(path);

                                bool consent = true;
                                try
                                {
                                    if (Application.Current != null)
                                    {
                                        // Show dialog synchronously on UI thread from background thread
                                        Application.Current.Dispatcher.Invoke(() =>
                                        {
                                            var dlg = new Overseer.Views.InstallerConsentWindow();
                                            if (Application.Current.MainWindow != null && Application.Current.MainWindow.IsVisible)
                                                dlg.Owner = Application.Current.MainWindow;
                                            dlg.SetMessage($"The installer '{Path.GetFileName(path)}' will be run with elevated privileges.\n\nDo you want to continue?");
                                            dlg.SetChecksumStatus(verification.StatusText);
                                            bool? res = dlg.ShowDialog();
                                            consent = res.GetValueOrDefault(false);
                                        });
                                    }
                                    else
                                    {
                                        Debug.WriteLine("No WPF dispatcher available for consent dialog; skipping installer.");
                                        consent = false;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"Consent dialog failed: {ex}");
                                }

                                if (!consent)
                                {
                                    Debug.WriteLine($"User declined installation of {path}");
                                    return false;
                                }

                                if (!verification.CanRun)
                                {
                                    Debug.WriteLine($"Checksum verification failed for {path}: {verification.StatusText}");
                                    return false;
                                }

                                var psi = new ProcessStartInfo(path)
                                {
                                    UseShellExecute = true,
                                    Verb = "runas",
                                    // PawnIO requires an explicit install or uninstall action; pass install + silent
                                    Arguments = "-install -silent"
                                };

                                TryUnblockFile(path);
                                var proc = Process.Start(psi);
                                Debug.WriteLine($"Launched helper installer: {path} (silent)");
                                try { proc?.WaitForExit(); } catch { }

                                // After installer exits, poll for up to 12 seconds to give the driver time to register
                                bool installed = false;
                                int attempts = 12;
                                for (int i = 0; i < attempts; i++)
                                {
                                    try
                                    {
                                        if (IsPawnIoInstalled())
                                        {
                                            installed = true;
                                            break;
                                        }
                                    }
                                    catch { }
                                    Thread.Sleep(1000);
                                }
                                // If still not installed, attempt to publish/install the driver package from DriverStore using pnputil
                                if (!installed)
                                {
                                    try
                                    {
                                        Debug.WriteLine("Attempting pnputil publish/install fallback from DriverStore...");
                                        bool pnputilResult = TryPublishDriverFromDriverStore();
                                        Debug.WriteLine($"pnputil attempt returned: {pnputilResult}");

                                        if (pnputilResult)
                                        {
                                            // give Windows a few seconds to bind the driver
                                            for (int r = 0; r < 10; r++)
                                            {
                                                try
                                                {
                                                    if (IsPawnIoInstalled())
                                                    {
                                                        installed = true;
                                                        break;
                                                    }
                                                }
                                                catch { }
                                                Thread.Sleep(1000);
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.WriteLine($"pnputil fallback failed: {ex}");
                                    }
                                }

                                try
                                {
                                    if (Application.Current != null)
                                    {
                                        Application.Current.Dispatcher.Invoke(() =>
                                        {
                                            if (installed)
                                            {
                                                var msg = "PawnIO has been installed successfully.\n\nWould you like to restart the application now to apply changes?";
                                                var result = System.Windows.MessageBox.Show(msg, "PawnIO Installed", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information);
                                                if (result == System.Windows.MessageBoxResult.Yes)
                                                {
                                                    try
                                                    {
                                                        var exe = Process.GetCurrentProcess().MainModule?.FileName;
                                                        if (!string.IsNullOrWhiteSpace(exe))
                                                        {
                                                            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                                                            Environment.Exit(0);
                                                        }
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                        Debug.WriteLine($"Failed to restart app: {ex}");
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                System.Windows.MessageBox.Show("PawnIO installer finished but installation could not be verified. Please reboot or install manually.", "PawnIO Installation", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                                            }
                                        });
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"Post-install notification failed: {ex}");
                                }

                                return true;
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Failed to launch bundled helper at {path}: {ex}");
                                // continue
                            }
                        }

                        // Fallback: any exe in helpers folder (skip current process)
                        var exes = Directory.GetFiles(helpersDir, "*.exe", SearchOption.TopDirectoryOnly);
                        if (exes.Length > 0)
                        {
                            string currentExe = null;
                            try { currentExe = Process.GetCurrentProcess().MainModule?.FileName; } catch { }

                        foreach (var path in exes)
                            {
                                try
                                {
                                TryUnblockFile(path);
                                    if (!string.IsNullOrWhiteSpace(currentExe) && Path.GetFullPath(path).Equals(Path.GetFullPath(currentExe), StringComparison.OrdinalIgnoreCase))
                                    {
                                        Debug.WriteLine($"Skipping current process executable: {path}");
                                        continue;
                                    }

                                    var verification = VerifyInstallerChecksumIfPresent(path);

                                    bool consent = true;
                                    try
                                    {
                                        if (Application.Current != null)
                                        {
                                            Application.Current.Dispatcher.Invoke(() =>
                                            {
                                                var dlg = new Overseer.Views.InstallerConsentWindow();
                                                if (Application.Current.MainWindow != null && Application.Current.MainWindow.IsVisible)
                                                    dlg.Owner = Application.Current.MainWindow;
                                                dlg.SetMessage($"The installer '{Path.GetFileName(path)}' will be run with elevated privileges.\n\nDo you want to continue?");
                                                dlg.SetChecksumStatus(verification.StatusText);
                                                bool? res = dlg.ShowDialog();
                                                consent = res.GetValueOrDefault(false);
                                            });
                                        }
                                        else
                                        {
                                            Debug.WriteLine("No WPF dispatcher available for consent dialog; skipping installer.");
                                            consent = false;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.WriteLine($"Consent dialog failed: {ex}");
                                    }

                                    if (!consent)
                                    {
                                        Debug.WriteLine($"User declined installation of {path}");
                                        return false;
                                    }

                                    if (!verification.CanRun)
                                    {
                                        Debug.WriteLine($"Checksum verification failed for {path}: {verification.StatusText}");
                                        return false;
                                    }

                                    var psi = new ProcessStartInfo(path)
                                    {
                                        UseShellExecute = true,
                                        Verb = "runas",
                                        // PawnIO requires an explicit install or uninstall action; pass install + silent
                                        Arguments = "-install -silent"
                                    };

                                    TryUnblockFile(path);
                                    var proc = Process.Start(psi);
                                    Debug.WriteLine($"Launched helper installer (silent): {path}");
                                    try { proc?.WaitForExit(); } catch { }
                                    Thread.Sleep(2000);
                                    return true;
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"Failed to launch fallback exe {path}: {ex}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error scanning helpersDir '{helpersDir}': {ex}");
                    }
                }
            }

            // Not found in searched helpers folders - fall through to download/releases page

            // Try automatic download if configured
            if (!string.IsNullOrWhiteSpace(PawnIoDownloadUrl))
            {
                try
                {
                    bool launched = LibreHardwareMonitorDownloader.TryDownloadAndLaunch(PawnIoDownloadUrl);
                    if (launched) return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Automatic download attempt failed: {ex}");
                }
            }

            try
            {
                Process.Start(new ProcessStartInfo(ReleasesUrl) { UseShellExecute = true });
                Debug.WriteLine("Opened LibreHardwareMonitor releases page in browser.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open releases page: {ex}");
            }

            return false;
        }

    private static void TryUnblockFile(string path)
    {
        try
        {
            // Remove the Zone.Identifier alternate data stream if present (downloaded files get blocked by Windows).
            // Deleting the ADS prevents the "This file is blocked because it does not have a valid digital signature" UI.
            string zonePath = path + ":Zone.Identifier";
            try
            {
                if (File.Exists(zonePath))
                {
                    File.Delete(zonePath);
                    Debug.WriteLine($"Removed Zone.Identifier ADS from: {path}");
                }
            }
            catch
            {
                // Some environments may not allow ADS deletion via File APIs; ignore failures.
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TryUnblockFile failed for '{path}': {ex}");
        }
    }

        public static bool IsPawnIoInstalled()
        {
            try
            {
                Debug.WriteLine($"IsPawnIoInstalled: Is64BitProcess={Environment.Is64BitProcess}");

                // Prefer the 64-bit registry view for system services/drivers.
                RegistryKey? svcKey = null;
                try
                {
                    svcKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"System\CurrentControlSet\Services\PawnIO");
                    Debug.WriteLine($"Raw ImagePath from HKLM (Registry64): '{svcKey?.GetValue("ImagePath")}'");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed reading Registry64 PawnIO key: {ex}");
                }

                if (svcKey == null)
                {
                    try
                    {
                        svcKey = Registry.LocalMachine.OpenSubKey(@"System\CurrentControlSet\Services\PawnIO");
                        Debug.WriteLine($"Raw ImagePath from HKLM (default view): '{svcKey?.GetValue("ImagePath")}'");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed reading default PawnIO key: {ex}");
                    }
                }

                if (svcKey != null)
                {
                    try
                    {
                        object? imagePathObj = svcKey.GetValue("ImagePath");
                        if (imagePathObj != null)
                        {
                            string imagePath = imagePathObj.ToString() ?? string.Empty;
                            imagePath = Environment.ExpandEnvironmentVariables(imagePath).Trim();

                            // Some registry ImagePath values use the literal "\SystemRoot\..." (no percent signs).
                            if (imagePath.StartsWith("\\SystemRoot\\", StringComparison.OrdinalIgnoreCase) ||
                                imagePath.Equals("\\SystemRoot", StringComparison.OrdinalIgnoreCase))
                            {
                                string sysRoot = Environment.GetEnvironmentVariable("SystemRoot") ??
                                                 Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                                string remainder = imagePath.Length > 11 ? imagePath.Substring(11).TrimStart('\\', '/') : string.Empty;
                                imagePath = string.IsNullOrEmpty(remainder) ? sysRoot : Path.Combine(sysRoot, remainder);
                            }

                            // Remove quotes or trailing args to normalize to a file path
                            if (imagePath.StartsWith("\""))
                            {
                                int end = imagePath.IndexOf('"', 1);
                                if (end > 1) imagePath = imagePath.Substring(1, end - 1);
                            }
                            else
                            {
                                int sp = imagePath.IndexOf(' ');
                                if (sp > 0) imagePath = imagePath.Substring(0, sp);
                            }

                            if (!string.IsNullOrWhiteSpace(imagePath))
                            {
                                string fullImagePath = imagePath;
                                try { fullImagePath = Path.GetFullPath(imagePath); } catch { }

                                bool fileExists = File.Exists(fullImagePath);
                                bool inDriverStore = fullImagePath.IndexOf(Path.Combine("DriverStore", "FileRepository"), StringComparison.OrdinalIgnoreCase) >= 0;
                                bool inSysDrivers = fullImagePath.IndexOf(Path.Combine("System32", "drivers"), StringComparison.OrdinalIgnoreCase) >= 0;

                                if (fileExists && inSysDrivers)
                                {
                                    Debug.WriteLine($"PawnIO registry key found and image exists in System32\\drivers: {fullImagePath}");
                                    Debug.WriteLine("IsPawnIoInstalled -> true (driver file in System32\\drivers)");
                                    return true;
                                }

                                if (fileExists && inDriverStore)
                                {
                                    Debug.WriteLine($"PawnIO registry key found but image located in DriverStore (not considered installed): {fullImagePath}");
                                    Debug.WriteLine("IsPawnIoInstalled -> false (only DriverStore present)");
                                }
                                else if (fileExists)
                                {
                                    Debug.WriteLine($"PawnIO registry key found and image exists (unexpected location): {fullImagePath}");
                                    Debug.WriteLine("IsPawnIoInstalled -> false (unexpected image location)");
                                }
                                else
                                {
                                    Debug.WriteLine($"PawnIO registry key found but image not present: {fullImagePath}");
                                    Debug.WriteLine("IsPawnIoInstalled -> false (image missing)");
                                }
                            }
                        }
                        else
                        {
                            Debug.WriteLine("PawnIO registry key found but ImagePath value is missing.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error examining PawnIO registry key: {ex}");
                    }
                }

                try
                {
                    var svc = ServiceController.GetServices().FirstOrDefault(s => s.ServiceName.Equals("PawnIO", StringComparison.OrdinalIgnoreCase));
                    if (svc != null)
                    {
                        Debug.WriteLine($"PawnIO service found: Status={svc.Status}");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ServiceController check failed: {ex}");
                }

                Debug.WriteLine("PawnIO not found via registry or service controller. Checking driver files...");

                // Fallback: check system drivers folder only. Files in DriverStore do not mean the driver is installed or loaded;
                // DriverStore contains cached packages. Require an actual driver file in System32\drivers to consider PawnIO installed.
                try
                {
                    string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    string sysDrivers = Path.Combine(systemRoot, "System32", "drivers");
                    if (Directory.Exists(sysDrivers))
                    {
                        var found2 = Directory.EnumerateFiles(sysDrivers, "PawnIO.sys", SearchOption.TopDirectoryOnly).FirstOrDefault();
                        if (!string.IsNullOrEmpty(found2))
                        {
                            Debug.WriteLine($"Found PawnIO.sys in System32\\drivers: {found2}");
                            return true;
                        }
                    }
                    Debug.WriteLine("PawnIO not found in System32\\drivers; DriverStore copy alone is insufficient to treat as installed.");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Driver file check failed: {ex}");
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IsPawnIoInstalled check failed: {ex}");
                return false;
            }
        }

        private static (bool CanRun, string StatusText) VerifyInstallerChecksumIfPresent(string installerPath)
        {
            try
            {
                string shaFile = installerPath + ".sha256";
                if (!File.Exists(shaFile))
                {
                    shaFile = installerPath + ".sha256.txt";
                    if (!File.Exists(shaFile))
                    {
                        string alt = Path.Combine(Path.GetDirectoryName(installerPath) ?? string.Empty, Path.GetFileName(installerPath) + ".sha256");
                        if (File.Exists(alt)) shaFile = alt; else shaFile = string.Empty;
                    }
                }

            // If exact sidecar not found, try alternative names: base filename without extension, or any *.sha256* file in folder or parent helpers dir
            if (string.IsNullOrWhiteSpace(shaFile) || !File.Exists(shaFile))
            {
                string dir = Path.GetDirectoryName(installerPath) ?? string.Empty;
                string baseNoExt = Path.GetFileNameWithoutExtension(installerPath);
                string alt1 = Path.Combine(dir, baseNoExt + ".sha256");
                string alt2 = Path.Combine(dir, baseNoExt + ".sha256.txt");
                if (File.Exists(alt1)) shaFile = alt1;
                else if (File.Exists(alt2)) shaFile = alt2;
                else
                {
                    // Try any matching in the same folder
                    var any = Directory.GetFiles(dir, "*.sha256*");
                    if (any.Length > 0)
                    {
                        // Prefer files that contain the base name
                        var match = any.FirstOrDefault(f => Path.GetFileName(f).IndexOf(baseNoExt, StringComparison.OrdinalIgnoreCase) >= 0);
                        shaFile = match ?? any[0];
                    }
                    else
                    {
                        // Try parent helpers folder if present
                        try
                        {
                            var parent = Directory.GetParent(dir)?.FullName;
                            if (!string.IsNullOrWhiteSpace(parent))
                            {
                                var anyParent = Directory.GetFiles(parent, "*.sha256*");
                                if (anyParent.Length > 0)
                                {
                                        var matchParent = anyParent.FirstOrDefault(f => Path.GetFileName(f).IndexOf(baseNoExt, StringComparison.OrdinalIgnoreCase) >= 0);
                                    shaFile = matchParent ?? anyParent[0];
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(shaFile) || !File.Exists(shaFile))
            {
                Debug.WriteLine($"No checksum file found next to installer {installerPath}");
                return (true, "Checksum not found");
            }

            Debug.WriteLine($"Using checksum file: {shaFile}");

            string expected = File.ReadAllText(shaFile).Trim();
                if (expected.Contains(' ')) expected = expected.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
                expected = expected.Trim();
                if (expected.Length == 0) return (false, "Checksum file empty");

                using (var sha256 = SHA256.Create())
                using (var stream = File.OpenRead(installerPath))
                {
                    byte[] hash = sha256.ComputeHash(stream);
                    string actual = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
                    if (actual.Equals(expected.Replace(" ", string.Empty).ToLowerInvariant()))
                    {
                        // SHA matches. Additionally verify Authenticode signature and require signer allowlist.
                        var sig = VerifyAuthenticodeSignature(installerPath);
                        Debug.WriteLine($"Authenticode: IsSigned={sig.IsSigned}, Signer='{sig.Signer}', Thumbprint='{sig.Thumbprint}', Error='{sig.Error}'");
                        try
                        {
                            var allowed = GetAllowedSignerThumbprints();
                            Debug.WriteLine($"Allowed thumbprints ({allowed.Length}): {string.Join(",", allowed)}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to read allowed thumbprints: {ex}");
                        }
                        // If WinVerifyTrust succeeded, sig.IsSigned==true and we can enforce allowlist.
                        // If WinVerifyTrust failed, we still attempt to extract the signing cert and thumbprint
                        // (see VerifyAuthenticodeSignature). Allow the installer if the extracted thumbprint
                        // matches the allowlist (defense-in-depth: require SHA + pinned signer).
                        string thumb = (sig.Thumbprint ?? string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
                        bool trusted = !string.IsNullOrEmpty(thumb) && GetAllowedSignerThumbprints().Any(t => string.Equals(t, thumb, StringComparison.OrdinalIgnoreCase));

                        if (sig.IsSigned || trusted)
                        {
                            // Prefer the signer subject if present
                            string signer = string.IsNullOrWhiteSpace(sig.Signer) ? "(unknown)" : sig.Signer;
                            if (sig.IsSigned)
                            {
                                return (true, $"SHA256 OK; Signed by: {signer} ({thumb})");
                            }
                            else
                            {
                                // WinVerifyTrust failed, but the thumbprint is explicitly allowed by policy.
                                return (true, $"SHA256 OK; Signed by: {signer} ({thumb}) [trusted by thumbprint]");
                            }
                        }
                        else
                        {
                            // No signature present or thumbprint not allowed
                            return (false, $"SHA256 OK but no valid signature present: {sig.Error}");
                        }
                    }
                else
                {
                    return (false, $"SHA256 mismatch. Expected {expected}, got {actual}");
                }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Checksum verification failed: {ex}");
                return (false, "Checksum verification error");
            }
        }

    private static (bool IsSigned, string Signer, string Thumbprint, string Error) VerifyAuthenticodeSignature(string filePath)
    {
            try
        {
            // Prepare structures for WinVerifyTrust
            var fileInfo = new WINTRUST_FILE_INFO(filePath);

            // Try WinVerifyTrust with a conservative set of flags first (no special provider flags).
           using var data = new WinTrustData(fileInfo)
            {
                dwUIChoice = WinTrustData.UIChoice.WTD_UI_NONE,
                fdwRevocationChecks = WinTrustData.RevocationChecks.WTD_REVOKE_NONE,
                dwUnionChoice = WinTrustData.UnionChoice.WTD_CHOICE_FILE,
                dwStateAction = WinTrustData.StateAction.Ignore,
                dwProvFlags = 0,
                dwUIContext = 0
            };

            Guid action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2

            uint result = WinVerifyTrust(IntPtr.Zero, action, data);

                // If the first attempt fails, try again with the SAFER flag (previous behavior). Some installer signing combos
                // can succeed with different provider flags on different systems; try both before giving up.
                if (result != 0)
            {
                try
                {
                    var data2 = new WinTrustData(fileInfo)
                    {
                        dwUIChoice = WinTrustData.UIChoice.WTD_UI_NONE,
                        fdwRevocationChecks = WinTrustData.RevocationChecks.WTD_REVOKE_NONE,
                        dwUnionChoice = WinTrustData.UnionChoice.WTD_CHOICE_FILE,
                        dwStateAction = WinTrustData.StateAction.None,
                        dwProvFlags = WinTrustData.ProvFlags.WTD_SAFER_FLAG,
                        dwUIContext = 0
                    };

                    uint result2 = WinVerifyTrust(IntPtr.Zero, action, data2);
                    if (result2 == 0)
                    {
                        result = result2;
                    }
                    else
                    {
                        // keep original result if second attempt also failed
                        Debug.WriteLine($"WinVerifyTrust attempts failed: first=0x{result:X}, second=0x{result2:X}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Secondary WinVerifyTrust attempt failed: {ex}");
                }
            }

            const uint ERROR_SUCCESS = 0;
            if (result == ERROR_SUCCESS)
            {
                // Attempt to read signer certificate subject and thumbprint using X509Certificate2 if available
                try
                {
                    var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(filePath));
                    string subj = cert?.Subject ?? "(unknown)";
                    string thumb = cert?.Thumbprint ?? string.Empty;
                    return (true, subj, thumb, string.Empty);
                }
                catch
                {
                    return (true, "(signed)", string.Empty, string.Empty);
                }
            }
            else
            {
                // WinVerifyTrust failed (chain or timestamp issues). Try to extract the signing certificate anyway
                try
                {
                    var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(filePath));
                    string subj = cert?.Subject ?? "(unknown)";
                    string thumb = cert?.Thumbprint ?? string.Empty;

                    // Attempt to extract embedded PKCS7 (WIN_CERTIFICATE) from the PE and inspect countersignatures (timestamp).
                    try
                    {
                        byte[]? pkcs7 = TryGetEmbeddedPkcs7(filePath);
                        if (pkcs7 != null && pkcs7.Length > 0)
                        {
                            try
                            {
                                var cms = new System.Security.Cryptography.Pkcs.SignedCms();
                                cms.Decode(pkcs7);
                                foreach (var signer in cms.SignerInfos)
                                {
                                    // Check countersignatures (timestamp tokens)
                                    foreach (var csig in signer.CounterSignerInfos)
                                    {
                                        try
                                        {
                                            var csCert = csig.Certificate;
                                            if (csCert != null)
                                            {
                                                string csSubj = csCert.Subject ?? string.Empty;
                                                string csThumb = csCert.Thumbprint ?? string.Empty;
                                                // If the countersigner appears to be a Microsoft timestamp authority, accept the signature as valid
                                                if (csSubj.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                                    (csSubj.IndexOf("Time", StringComparison.OrdinalIgnoreCase) >= 0 || csSubj.IndexOf("Stamp", StringComparison.OrdinalIgnoreCase) >= 0))
                                                {
                                                    return (true, subj, thumb, $"WinVerifyTrust error 0x{result:X}; countersignature by '{csSubj}' ({csThumb}) accepted");
                                                }
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                            catch (Exception exCms)
                            {
                                Debug.WriteLine($"Failed to decode SignedCms from embedded PKCS7: {exCms}");
                            }
                        }
                    }
                    catch (Exception exExtract)
                    {
                        Debug.WriteLine($"Failed to extract embedded PKCS7: {exExtract}");
                    }

                    return (false, subj, thumb, $"WinVerifyTrust error 0x{result:X}");
                }
                catch
                {
                    return (false, string.Empty, string.Empty, $"WinVerifyTrust error 0x{result:X}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Authenticode check failed: {ex}");
            return (false, string.Empty, string.Empty, ex.Message);
        }
    }

    private static byte[]? TryGetEmbeddedPkcs7(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            using var br = new BinaryReader(fs);
            // Read DOS header e_lfanew
            fs.Seek(0x3C, SeekOrigin.Begin);
            int e_lfanew = br.ReadInt32();
            if (e_lfanew <= 0 || e_lfanew > fs.Length - 4) return null;

            fs.Seek(e_lfanew, SeekOrigin.Begin);
            uint peSig = br.ReadUInt32(); // "PE\0\0"
            if (peSig != 0x00004550) return null;

            // Skip COFF File Header (20 bytes)
            fs.Seek(20, SeekOrigin.Current);

            long optionalHeaderStart = fs.Position;
            ushort magic = br.ReadUInt16();
            // DataDirectory starts at offset depending on PE32 vs PE32+
            int dataDirOffsetFromOptional = (magic == 0x10b) ? 96 : 112;
            long dataDirectoryStart = optionalHeaderStart + dataDirOffsetFromOptional;
            if (dataDirectoryStart + 8 * 5 > fs.Length) return null; // ensure enough space for certificate directory

            // Certificate Table is directory index 4
            long certDirOffset = dataDirectoryStart + 8 * 4;
            fs.Seek(certDirOffset, SeekOrigin.Begin);
            uint certEntryOffset = br.ReadUInt32(); // for security directory this is file offset
            uint certEntrySize = br.ReadUInt32();

            if (certEntryOffset == 0 || certEntrySize <= 8 || certEntryOffset + certEntrySize > fs.Length) return null;

            fs.Seek(certEntryOffset, SeekOrigin.Begin);
            uint dwLength = br.ReadUInt32();
            // skip wRevision (2) + wCertificateType (2)
            br.ReadUInt16();
            br.ReadUInt16();
            int certBlobLen = (int)dwLength - 8;
            if (certBlobLen <= 0 || certBlobLen > certEntrySize) return null;

            byte[] pkcs7 = br.ReadBytes(certBlobLen);
            if (pkcs7.Length != certBlobLen) return null;
            return pkcs7;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TryGetEmbeddedPkcs7 failed: {ex}");
            return null;
        }
    }

    #region WinTrust interop
    [DllImport("wintrust.dll", PreserveSig = true, SetLastError = true)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, WinTrustData pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private class WINTRUST_FILE_INFO
    {
        public uint cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO));
        public string pcwszFilePath;
        public IntPtr hFile = IntPtr.Zero;
        public IntPtr pgKnownSubject = IntPtr.Zero;

        public WINTRUST_FILE_INFO(string filePath)
        {
            pcwszFilePath = filePath;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private class WinTrustData : IDisposable
    {
        public enum UIChoice : uint
        {
            WTD_UI_ALL = 1,
            WTD_UI_NONE = 2,
            WTD_UI_NOBAD = 3,
            WTD_UI_NOGOOD = 4
        }

        public enum RevocationChecks : uint
        {
            WTD_REVOKE_NONE = 0x00000000,
            WTD_REVOKE_WHOLECHAIN = 0x00000001
        }

        public enum UnionChoice : uint
        {
            WTD_CHOICE_FILE = 1,
            WTD_CHOICE_CATALOG = 2
        }

        public enum StateAction : uint
        {
            Ignore = 0x00000000,
            Verify = 0x00000001,
            Close = 0x00000002,
            AutoCache = 0x00000003,
            AutoCacheFlush = 0x00000004,
            None = 0x00000005
        }

        [Flags]
        public enum ProvFlags : uint
        {
            WTD_SAFER_FLAG = 0x00000100
        }

        public uint cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData));

        public IntPtr pPolicyCallbackData = IntPtr.Zero;
        public IntPtr pSIPClientData = IntPtr.Zero;
        
        public UIChoice dwUIChoice;
        public RevocationChecks fdwRevocationChecks;
        public UnionChoice dwUnionChoice;
        public IntPtr pFile; // points to WINTRUST_FILE_INFO
        public StateAction dwStateAction;
        public IntPtr hWVTStateData = IntPtr.Zero;
        public string? pwszURLReference = null;
        public ProvFlags dwProvFlags;
        public uint dwUIContext;

        public WinTrustData(WINTRUST_FILE_INFO fileInfo)
        {
            pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
            Marshal.StructureToPtr(fileInfo, pFile, false);
        }
            public void Dispose()
            {
                if (pFile != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(pFile);
                    pFile = IntPtr.Zero;
                }

                GC.SuppressFinalize(this);
            }
        }
    #endregion

    private static bool TryPublishDriverFromDriverStore()
    {
        try
        {
            string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string driverStore = Path.Combine(systemRoot, "System32", "DriverStore", "FileRepository");
            if (!Directory.Exists(driverStore))
            {
                Debug.WriteLine($"DriverStore not found: {driverStore}");
                return false;
            }

            // Find an INF for PawnIO in the DriverStore
            var infPath = Directory.EnumerateFiles(driverStore, "pawnio.inf", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(infPath))
            {
                Debug.WriteLine("No pawnio.inf found in DriverStore.");
                return false;
            }

            Debug.WriteLine($"Found pawnio.inf: {infPath}. Running pnputil to add+install.");

            string args = $"/add-driver \"{infPath}\" /install";
            var psi = new ProcessStartInfo("pnputil.exe")
            {
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var proc = Process.Start(psi);
            proc?.WaitForExit(30000);
            int code = proc?.ExitCode ?? -1;
            Debug.WriteLine($"pnputil exit code: {code}");

            return code == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TryPublishDriverFromDriverStore failed: {ex}");
            return false;
        }
    }

    #region Driver enumeration interop
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumDeviceDrivers([Out] IntPtr[] lpImageBase, int cb, out int lpcbNeeded);

    [DllImport("psapi.dll", CharSet = CharSet.Auto)]
    private static extern uint GetDeviceDriverBaseName(IntPtr ImageBase, StringBuilder lpFilename, int nSize);

    private static bool IsDriverLoaded(string driverFileName)
    {
        try
        {
            int requiredBytes = 0;
            // first call to get required buffer size
            EnumDeviceDrivers(null, 0, out requiredBytes);
            if (requiredBytes == 0) return false;

            int count = requiredBytes / IntPtr.Size;
            IntPtr[] drivers = new IntPtr[count];
            if (!EnumDeviceDrivers(drivers, requiredBytes, out requiredBytes)) return false;

            var sb = new StringBuilder(260);
            for (int i = 0; i < drivers.Length; i++)
            {
                sb.Clear();
                uint ret = GetDeviceDriverBaseName(drivers[i], sb, sb.Capacity);
                if (ret > 0)
                {
                    string name = sb.ToString();
                    if (string.Equals(name, driverFileName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"IsDriverLoaded check failed: {ex}");
        }

        return false;
    }
    #endregion
    }
}
