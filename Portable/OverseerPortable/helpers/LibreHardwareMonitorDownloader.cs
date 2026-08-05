using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;

namespace Overseer.Helpers;

public static class LibreHardwareMonitorDownloader
{
    /// <summary>
    /// Attempts to download a helper package from the specified URL into the app's helpers folder,
    /// extract if necessary and launch the first executable found. Returns true if an executable
    /// was launched successfully.
    /// This method is best-effort and will not throw on failure.
    /// </summary>
    public static bool TryDownloadAndLaunch(string url)
    {
        try
        {
            string appDir = AppContext.BaseDirectory;
            string helpersDir = Path.Combine(appDir, "helpers");
            Directory.CreateDirectory(helpersDir);

            string fileName = Path.GetFileName(new Uri(url).LocalPath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "librehardwaremonitor_helper";

            string targetPath = Path.Combine(helpersDir, fileName);

            // Download
            try
            {
                using HttpClient client = new HttpClient();
                using var resp = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                resp.EnsureSuccessStatusCode();
                using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to download helper from {url}: {ex}");
                return false;
            }

            // If it's a zip, extract
            if (targetPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    ZipFile.ExtractToDirectory(targetPath, helpersDir, true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to extract downloaded zip: {ex}");
                    // continue — maybe it's a non-zip package
                }
            }

            // Find candidate executables in helpers folder
            string[] candidates = Directory.GetFiles(helpersDir, "*.exe", SearchOption.TopDirectoryOnly);
            // prefer known names
            string[] preferredNames = new[] { "LibreHardwareMonitor.exe", "librehardwaremonitor.exe", "DriverInstaller.exe", "pawnio.exe" };

            string exeToLaunch = null;
            foreach (var pref in preferredNames)
            {
                string prefPath = Path.Combine(helpersDir, pref);
                if (File.Exists(prefPath))
                {
                    exeToLaunch = prefPath;
                    break;
                }
            }

            if (exeToLaunch == null && candidates.Length > 0)
            {
                exeToLaunch = candidates[0];
            }

            if (exeToLaunch == null)
            {
                Debug.WriteLine("No executable found in helpers folder after download.");
                return false;
            }

            try
            {
                var psi = new ProcessStartInfo(exeToLaunch)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
                Debug.WriteLine($"Launched downloaded helper: {exeToLaunch}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to launch downloaded helper {exeToLaunch}: {ex}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TryDownloadAndLaunch failed: {ex}");
            return false;
        }
    }
}
