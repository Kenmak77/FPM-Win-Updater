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
        private const int MAX_TEMP_CLEANUP_RETRIES = 5;
        private const int TEMP_CLEANUP_DELAY_MS = 1000;
        private static readonly string[] PRESERVE_FILES = {
            "dolphin.log", "dolphin.ini", "gfx.ini", "vcruntime140_1.dll",
            "gckeynew.ini", "gcpadnew.ini", "hotkeys.ini", "logger.ini",
            "debugger.ini", "wiimotenew.ini"
        };

        private static string installPath = string.Empty;
        private static string tempPath = string.Empty;
        private static string? dolphinPath;
        private static string zipPath = string.Empty;
        private static readonly HttpClient httpClient = new HttpClient();

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        static async Task Main(string[] args)
        {
            if (args.Any(a => a.Equals("--security-verify", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("Security verification passed");
                return;
            }

            try
            {
                Console.WriteLine("\n======================= P+FR Updater =======================");
                Console.WriteLine("\nIt may take a little time, pls wait until this window close ");
                Console.WriteLine("\n");

                if (!ValidateArguments(args))
                    return;

                installPath = args[1];
                tempPath = Path.Combine(Path.GetTempPath(), $"updater-temp-{Guid.NewGuid()}");
                zipPath = Path.Combine(tempPath, "update.zip");

                CleanupTempDirectory(); // Ensure temp dir is clean before download

                await PromptScoopAndTools();
                await RunUpdateProcess(args[0]);

                Log("All Good. Dolphin should be launched, HF.");
                Thread.Sleep(1000); // Donne le temps à Dolphin de démarrer

                // Laisser le temps à l'utilisateur de voir un message final ?
                // Console.WriteLine("Press any key to close...");
                // Console.ReadKey(true); // Ou directement :
                Environment.Exit(2000);
            }
            catch (Exception ex)
            {
                LogError($"Critical failure: {ex}");
                Thread.Sleep(2000); // Laisse l'utilisateur voir l'erreur
                Environment.Exit(1);
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


        private static async Task PromptScoopAndTools()
        {
            bool hasScoop = IsToolAvailable("scoop");

            if (!hasScoop)
            {
                Log("Scoop is not installed. Install it now? (install Aria2 and Rcloud to faster download) (y/n)");
                if (Console.ReadKey(true).Key == ConsoleKey.Y)
                {
                    await RunPowerShell(@"Set-ExecutionPolicy RemoteSigned -Scope CurrentUser -Force; iwr get.scoop.sh -UseBasicParsing | iex");
                    hasScoop = true;
                }
                else
                {
                    Log("Scoop not installed. Will continue with fallback methods if available.");
                }
            }

            if (hasScoop)
            {
                if (!IsToolAvailable("aria2c"))
                {
                    Log("aria2 is not installed. Installing via Scoop...");
                    await RunPowerShell("scoop install aria2");
                }
                else
                {
                    Log("aria2 is installed. Updating...");
                    await RunPowerShell("scoop update aria2", 6000);
                }

                if (!IsToolAvailable("rclone"))
                {
                    Log("rclone is not installed. Installing via Scoop...");
                    await RunPowerShell("scoop install rclone");
                }
                else
                {
                    Log("rclone is installed. Updating...");
                    await RunPowerShell("scoop update rclone", 6000);
                }
            }
        }




        private static async Task RunPowerShell(string command, int timeoutMs = 10000)
        {
            try
            {
                var ps = new ProcessStartInfo("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(ps);
                if (proc == null)
                {
                    Log("Failed to start PowerShell process.");
                    return;
                }

                // Attente avec timeout
                var exited = await Task.Run(() => proc.WaitForExit(timeoutMs));
                if (exited)
                {
                    string output = await proc.StandardOutput.ReadToEndAsync();
                    string error = await proc.StandardError.ReadToEndAsync();

                    if (!string.IsNullOrWhiteSpace(output))
                        Log(output.Trim());

                    if (!string.IsNullOrWhiteSpace(error))
                        Log("PowerShell Error: " + error.Trim());
                }
                else
                {
                    try { proc.Kill(); } catch { }
                    Log($"[Timeout] PowerShell command took too long and was terminated: {command}");
                }
            }
            catch (Exception ex)
            {
                Log($"Exception while running PowerShell: {ex.Message}");
            }
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
                await Task.Delay(1000);
               

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
            {
                Log("Deleting existing update.zip to avoid conflict...");
                File.Delete(zipPath);
            }

            // aria2c first
            if (await TryDownloadWithTool("aria2c", $"-x 16 -s 16 -o \"{Path.GetFileName(zipPath)}\" \"{downloadUrl}\"", tempPath))
                return;

            // rclone fallback
            if (await TryDownloadWithTool("rclone", $"copyurl \"{downloadUrl}\" \"{Path.GetFileName(zipPath)}\" --multi-thread-streams=8 -P", tempPath))
                return;

            // http client
            Log("Falling back to HTTP...");
            await DownloadWithHttpClient(downloadUrl, zipPath);
        }


        private static async Task<bool> TryDownloadWithTool(string toolName, string arguments, string workingDir)
        {
            if (!IsToolAvailable(toolName)) return false;

            Log($"Attempting download with {toolName}...");

            try
            {
                // Ajoute automatiquement -d pour aria2c
                if (toolName == "aria2c" && !arguments.Contains("-d"))
                {
                    arguments = $"-d \"{workingDir}\" {arguments}";
                }

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

                process.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log($"[{toolName}] {e.Data}"); };
                process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) LogError($"[{toolName} ERROR] {e.Data}"); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();

                if (File.Exists(zipPath) && new FileInfo(zipPath).Length > 0)
                {
                    Log($"Download succeeded using {toolName}");
                    return true;
                }
                else
                {
                    LogError($"{toolName} ran but file was not created correctly.");
                }
            }
            catch (Exception ex)
            {
                LogError($"Download with {toolName} failed: {ex.Message}");
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
                    using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    var total = response.Content.Headers.ContentLength ?? -1L;
                    var canReportProgress = total != -1;

                    await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await using var httpStream = await response.Content.ReadAsStreamAsync();

                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int read;
                    while ((read = await httpStream.ReadAsync(buffer)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read));
                        totalRead += read;
                        if (canReportProgress)
                        {
                            Console.Write($"\rDownloading... {totalRead / 1024 / 1024}MB / {total / 1024 / 1024}MB ({(int)((double)totalRead / total * 100)}%)");
                        }
                        else
                        {
                            Console.Write($"\rDownloading... {totalRead / 1024 / 1024}MB");
                        }
                    }
                    Console.WriteLine();
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
                    FileName = "cmd.exe",
                    Arguments = "/C start \"\" \"./Dolphin.exe\"",
                    WorkingDirectory = installPath, // ou installPath
                    CreateNoWindow = true,
                    UseShellExecute = false
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
            GC.Collect();
            GC.WaitForPendingFinalizers();

            for (int i = 0; i < MAX_TEMP_CLEANUP_RETRIES; i++)
            {
                try
                {
                    if (Directory.Exists(tempPath))
                    {
                        Directory.Delete(tempPath, true);
                    }
                    return;
                }
                catch
                {
                    Thread.Sleep(TEMP_CLEANUP_DELAY_MS);
                }
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

                return !string.IsNullOrWhiteSpace(output);
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

       
       

    }
}
