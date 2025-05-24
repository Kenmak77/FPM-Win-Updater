using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;

namespace DolphinUpdater
{
    class Program
    {
        // Configuration
        private const int MAX_DOWNLOAD_RETRIES = 3;
        private const int RETRY_DELAY_MS = 2000;
        private static readonly string[] PRESERVE_FILES = {
            "dolphin.log", "dolphin.ini", "gfx.ini", "vcruntime140_1.dll",
            "gckeynew.ini", "gcpadnew.ini", "hotkeys.ini", "logger.ini",
            "debugger.ini", "wiimotenew.ini"
        };

        // State
        private static string installPath = string.Empty;
        private static string tempPath = string.Empty;
        private static string? dolphinPath;
        private static string zipPath = string.Empty;
        private static readonly HttpClient httpClient = new HttpClient();

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        static async Task Main(string[] args)
        {
            try
            {
                AttachConsole(-1); // Attach to parent console
                Console.WriteLine("\n=== Dolphin Updater ===");

                if (!ValidateArguments(args)) return;

                installPath = args[1];
                tempPath = Path.Combine(installPath, "temp");
                zipPath = Path.Combine(tempPath, "update.zip");

                await RunUpdateProcess(args[0]);
            }
            catch (Exception ex)
            {
                LogError($"Critical failure: {ex}");
                ExitWithDelay(5);
            }
        }

        private static bool ValidateArguments(string[] args)
        {
            if (args.Length < 2)
            {
                LogError("Usage: Updater.exe <downloadUrl> <installPath>");
                return false;
            }

            if (!Uri.TryCreate(args[0], UriKind.Absolute, out _))
            {
                LogError("Invalid download URL format");
                return false;
            }

            if (!Directory.Exists(args[1]))
            {
                LogError("Install directory does not exist");
                return false;
            }

            return true;
        }

        private static async Task RunUpdateProcess(string downloadUrl)
        {
            try
            {
                Log("Starting update process...");

                // 1. Close running Dolphin instances
                CloseDolphin();

                // 2. Prepare temp directory
                PrepareTempDirectory();

                // 3. Download update package
                await DownloadUpdatePackage(downloadUrl);

                // 4. Extract update
                ExtractUpdatePackage();

                // 5. Apply update (preserving config files)
                ApplyUpdate();

                // 6. Launch Dolphin
                LaunchDolphin();

                Log("Update completed successfully!");
            }
            finally
            {
                // 7. Cleanup
                CleanupTempDirectory();
            }
        }

        private static void PrepareTempDirectory()
        {
            try
            {
                if (Directory.Exists(tempPath))
                    Directory.Delete(tempPath, true);

                Directory.CreateDirectory(tempPath);
                Log($"Created temp directory: {tempPath}");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to prepare temp directory: {ex.Message}");
            }
        }

        private static async Task DownloadUpdatePackage(string downloadUrl)
        {
            Log($"Downloading update from: {downloadUrl}");

            if (File.Exists(zipPath))
                File.Delete(zipPath);

            // Try aria2c first, then rclone, then fallback to HttpClient
            if (await TryDownloadWithTool("aria2c", $"-x 16 -s 16 -o \"{Path.GetFileName(zipPath)}\" \"{downloadUrl}\"", tempPath))
                return;

            if (await TryDownloadWithTool("rclone", $"copyurl \"{downloadUrl}\" \"{Path.GetFileName(zipPath)}\" --multi-thread-streams=8 -P", tempPath))
                return;

            await DownloadWithHttpClient(downloadUrl, zipPath);
        }

        private static async Task<bool> TryDownloadWithTool(string toolName, string arguments, string workingDir)
        {
            if (!IsToolAvailable(toolName)) return false;

            Log($"Attempting download with {toolName}...");

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = toolName,
                        Arguments = arguments,
                        WorkingDirectory = workingDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.OutputDataReceived += (s, e) => Log($"[{toolName}] {e.Data}");
                process.ErrorDataReceived += (s, e) => Log($"[{toolName} ERROR] {e.Data}");

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();

                if (File.Exists(zipPath))
                {
                    Log($"Download succeeded using {toolName}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"Download with {toolName} failed: {ex.Message}");
            }

            return false;
        }

        private static async Task DownloadWithHttpClient(string url, string outputPath)
        {
            Log("Falling back to HttpClient...");

            int retryCount = 0;
            while (retryCount < MAX_DOWNLOAD_RETRIES)
            {
                try
                {
                    using var response = await httpClient.GetAsync(url);
                    response.EnsureSuccessStatusCode();

                    await using var stream = await response.Content.ReadAsStreamAsync();
                    await using var fileStream = File.Create(outputPath);
                    await stream.CopyToAsync(fileStream);
                    return;
                }
                catch (Exception ex)
                {
                    retryCount++;
                    if (retryCount == MAX_DOWNLOAD_RETRIES)
                        throw new Exception($"Download failed after {MAX_DOWNLOAD_RETRIES} attempts: {ex.Message}");

                    Log($"Attempt {retryCount} failed, retrying in {RETRY_DELAY_MS / 1000} seconds...");
                    await Task.Delay(RETRY_DELAY_MS);
                }
            }
        }

        private static void ExtractUpdatePackage()
        {
            Log("Extracting update package...");

            try
            {
                using var archive = ZipArchive.Open(zipPath);
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    if (PRESERVE_FILES.Any(f => entry.Key?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        Log($"Skipping preserved file: {entry.Key}");
                        continue;
                    }

                    Log($"Extracting: {entry.Key}");
                    entry.WriteToDirectory(tempPath, new ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to extract update package: {ex.Message}");
            }
        }

        private static void ApplyUpdate()
        {
            Log("Applying update...");

            // Find the actual Dolphin folder in the extracted files
            dolphinPath = FindDolphinPath() ?? tempPath;

            try
            {
                // Copy all files except preserved ones
                foreach (var sourceFile in Directory.GetFiles(dolphinPath))
                {
                    var fileName = Path.GetFileName(sourceFile);
                    if (PRESERVE_FILES.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                        continue;

                    var destFile = Path.Combine(installPath, fileName);

                    if (File.Exists(destFile))
                        File.Delete(destFile);

                    File.Copy(sourceFile, destFile);
                    Log($"Updated: {fileName}");
                }

                // Copy directories recursively
                foreach (var sourceDir in Directory.GetDirectories(dolphinPath))
                {
                    var dirName = Path.GetFileName(sourceDir);
                    var destDir = Path.Combine(installPath, dirName);

                    CopyDirectory(sourceDir, destDir);
                    Log($"Updated directory: {dirName}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to apply update: {ex.Message}");
            }
        }

        private static string? FindDolphinPath()
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(tempPath, "dolphin.exe", SearchOption.AllDirectories))
                    return Path.GetDirectoryName(file);
            }
            catch (Exception ex)
            {
                Log($"Warning: Could not locate Dolphin.exe in package: {ex.Message}");
            }

            return null;
        }

        private static void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true);
            }

            foreach (var directory in Directory.GetDirectories(sourceDir))
            {
                CopyDirectory(directory, Path.Combine(targetDir, Path.GetFileName(directory)));
            }
        }

        private static void LaunchDolphin()
        {
            var dolphinExe = Path.Combine(installPath, "Dolphin.exe");
            if (!File.Exists(dolphinExe))
            {
                LogError("Dolphin.exe not found after update!");
                return;
            }

            try
            {
                Log("Launching Dolphin...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = dolphinExe,
                    WorkingDirectory = installPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LogError($"Failed to launch Dolphin: {ex.Message}");
            }
        }

        private static void CloseDolphin()
        {
            try
            {
                Log("Closing any running Dolphin instances...");
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/f /im Dolphin.exe",
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    }
                };
                process.Start();
                process.WaitForExit();
                Thread.Sleep(1000); // Wait for process to fully terminate
            }
            catch (Exception ex)
            {
                Log($"Warning: Could not close Dolphin: {ex.Message}");
            }
        }

        private static void CleanupTempDirectory()
        {
            try
            {
                if (Directory.Exists(tempPath))
                {
                    Log("Cleaning up temporary files...");
                    Directory.Delete(tempPath, true);
                }
            }
            catch (Exception ex)
            {
                Log($"Warning: Could not clean up temp directory: {ex.Message}");
            }
        }

        private static bool IsToolAvailable(string tool)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = tool,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process == null) return false;

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                return !string.IsNullOrEmpty(output);
            }
            catch
            {
                return false;
            }
        }

        private static void Log(string message)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        private static void LogError(string message)
        {
            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] ERROR: {message}");
        }

        private static void ExitWithDelay(int seconds)
        {
            Log($"Closing in {seconds} seconds...");
            Thread.Sleep(seconds * 1000);
            Environment.Exit(1);
        }
    }
}