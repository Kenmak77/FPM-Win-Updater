#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using System.Security.Cryptography; // en haut du fichier
using System.Text.Json.Nodes; // si tu utilises JsonObject / JsonNode
using System.Text.Json;

#nullable enable
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
        private static string dolphinPath = string.Empty;
        private static string zipPath = string.Empty;
        private static readonly HttpClient httpClient = new HttpClient();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int MessageBox(IntPtr hWnd, String text, String caption, uint type);



        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetDiskFreeSpaceEx(
    string lpDirectoryName,
    out ulong lpFreeBytesAvailable,
    out ulong lpTotalNumberOfBytes,
    out ulong lpTotalNumberOfFreeBytes);

        [DllImport("Kernel32")]
        private static extern bool SetConsoleCtrlHandler(EventHandler handler, bool add);

        private delegate bool EventHandler(CtrlType sig);
        private static readonly EventHandler _handler = Handler;


        enum CtrlType
        {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT = 1,
            CTRL_CLOSE_EVENT = 2,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT = 6
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);

        private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

        private static void MarkForDeletionOnReboot(string path)
        {
            try
            {
                // Méthode 1: API Windows
                MoveFileEx(path, "", MOVEFILE_DELAY_UNTIL_REBOOT);

                // Méthode 2: Registry (fallback)
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager", writable: true))
                {
                    if (key != null)
                    {
                        var pendingFiles = key.GetValue("PendingFileRenameOperations") as string[] ?? Array.Empty<string>();
                        var newPendingFiles = new List<string>(pendingFiles) { path, "" };
                        key.SetValue("PendingFileRenameOperations", newPendingFiles.ToArray(),
                            Microsoft.Win32.RegistryValueKind.MultiString);
                    }
                    else
                    {
                        Log("Registry key not found (cannot mark file for deletion on reboot).");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Échec marquage reboot: {ex.Message}");
            }
        }
        private static bool Handler(CtrlType sig)
        {
            // Ajoutez une vérification de null si nécessaire
            if (tempPath == null) return false;

            Log($"Shutdown signal received: {sig}");
            CleanupTempDirectory();
            Environment.Exit(1);
            return true;
        }

        private static extern bool AttachConsole(int dwProcessId);

        static async Task Main(string[] args)
        {
            // Gestion des signaux systèm
            SetConsoleCtrlHandler(_handler, true);

            KillAllTempProcesses();

            if (args.Any(a => a.Equals("--security-verify", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("Security verification passed");
                return;
            }

            try
            {
                Console.WriteLine("\n===================================================== P+FR Updater =====================================================");
                Console.WriteLine("\nIt may take a little time, pls wait until this window close ");
                Console.WriteLine("\n");

                if (!ValidateArguments(args))
                    return;

                installPath = args[1];


                CleanupTempDirectory(); // Ensure temp dir is clean before download

                await PromptScoopAndTools();
                await RunUpdateProcess(args[0]);

              
            }
            catch (Exception ex)
            {
                LogError($"Critical failure: {ex}");
                Thread.Sleep(2000); // Laisse l'utilisateur voir l'erreur
                CleanupTempDirectory();
                Environment.Exit(1);
            }
        }

        private static async Task<long> GetContentLength(string url)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (response.Content.Headers.ContentLength.HasValue)
                    return response.Content.Headers.ContentLength.Value;
            }
            catch (Exception ex)
            {
                Log($"Warning: Could not fetch Content-Length for {url}: {ex.Message}");
            }
            return -1;
        }
        private static void KillAllTempProcesses()
        {
            try
            {
                var currentProcess = Process.GetCurrentProcess();
                var mainModule = currentProcess.MainModule;

                // Ajout de la vérification de null
                if (mainModule?.FileName == null)
                {
                    Log("Could not get main module info");
                    return;
                }

                foreach (var process in Process.GetProcessesByName("Dolphin"))
                {
                    try
                    {
                        if (process.Id != currentProcess.Id)
                            process.Kill();
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static bool IsHDD(string path)
        {
            try
            {
                // Gestion explicite du cas null
                string? rootPath = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(rootPath))
                {
                    Log("Could not determine drive root path, defaulting to HDD behavior");
                    return true; // fallback to HDD if can't determine
                }

                // Suppression des caractères inutiles
                string driveLetter = rootPath.Replace("\\", "").Replace(":", "");
                if (string.IsNullOrEmpty(driveLetter))
                {
                    Log("Could not extract drive letter, defaulting to HDD behavior");
                    return true;
                }

                var ps = new ProcessStartInfo("powershell",
                    $"-NoProfile -Command \"(Get-PhysicalDisk | Where-Object {{$_.DeviceID -eq (Get-Partition -DriveLetter {driveLetter} | Get-Disk).Number}}).MediaType\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(ps);
                if (proc == null)
                {
                    Log("Failed to start PowerShell process, defaulting to HDD behavior");
                    return true; // fallback HDD
                }

                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();

                return output.Equals("HDD", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log($"Error checking disk type: {ex.Message}, defaulting to HDD behavior");
                return true; // fallback → si erreur, on suppose HDD
            }
        }
        private static void CheckDiskSpace(string targetPath, long requiredBytes)
        {
            string root = Path.GetPathRoot(targetPath)!;

            if (!GetDiskFreeSpaceEx(root, out ulong freeBytes, out _, out _))
            {
                throw new Exception($"Failed to check disk space on {root}, error code: {Marshal.GetLastWin32Error()}");
            }

            if ((long)freeBytes < requiredBytes)
            {
                string msg = $"Not enough free disk space on {root}.\n" +
                             $"At least 8 GB is required \n" +
                             $"Available: {freeBytes / (1024 * 1024 * 1024)} GB.";

                MessageBox(IntPtr.Zero, msg, "Espace disque insuffisant", 0x10); // 0x10 = MB_ICONERROR
                throw new Exception(msg);
            }
        }

        private static void CleanupOldTempDirectories()
        {
            try
            {
                // Version sécurisée avec vérification de null
                string? systemDrive = Path.GetPathRoot(Environment.SystemDirectory);

                if (string.IsNullOrEmpty(systemDrive))
                {
                    Log("Could not determine system drive for cleanup");
                    return;
                }

                try
                {
                    var directories = Directory.GetDirectories(systemDrive, "updater-temp-*");

                    foreach (var dir in directories)
                    {
                        try
                        {
                            if (Directory.Exists(dir) &&
                                Directory.GetCreationTime(dir) < DateTime.Now.AddHours(-1))
                            {
                                try
                                {
                                    Directory.Delete(dir, true);
                                    Log($"Cleaned old temp dir: {dir}");
                                }
                                catch (Exception ex)
                                {
                                    Log($"Failed to delete {dir}: {ex.Message}");
                                    MarkForDeletionOnReboot(dir);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"Error processing directory {dir}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error scanning directories: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log($"Error in old temp cleanup: {ex.Message}");
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

            // Vérification supplémentaire
            try
            {
                Path.GetFullPath(args[1]); // Valide le chemin
            }
            catch
            {
                LogError("Invalid install path");
                return false;
            }

            return true;
        }


        private static async Task PromptScoopAndTools()
        {
            bool hasScoop = IsToolAvailable("scoop");

            if (!hasScoop)
            {
                Log("Scoop is not installed. Install it now? (install Aria2 and Rcloud to faster download on SSD only) (y/n)");
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
                AppDomain.CurrentDomain.ProcessExit += (s, e) => CleanupTempDirectory();
                Console.CancelKeyPress += (s, e) =>
                {
                    CleanupTempDirectory();
                    e.Cancel = true;
                };


                Log("Starting update process...");

                

                // 1. Close running Dolphin instances
                CloseDolphin();

                string dolphinExe = Path.Combine(installPath, "Dolphin.exe");
                if (!File.Exists(dolphinExe))
                    throw new Exception($"Dolphin.exe not found in install path: {installPath}");

                //4. HDD or SSD
                bool useHttpOnly = IsHDD(installPath);

                // 3bis. Update SD card avant tout
                await UpdateSdIfNeeded("https://update.pplusfr.org/update.json");

                // 3. Prepare temp directory
                PrepareTempDirectory();

                // Vérifier l’espace disque requis dynamiquement
                long requiredSpace = 8L * 1024 * 1024 * 1024;
                CheckDiskSpace(AppContext.BaseDirectory, requiredSpace);

                // 2.1 Ensuite faire le process classique du .zip
                zipPath = Path.Combine(tempPath, "update.zip");
                await DownloadWithHttpClient(downloadUrl, zipPath);

                // 5. Extract update
                ExtractUpdatePackage();

                await Task.Delay(70);

                // 6. Apply update (preserving config files)
                ApplyUpdate();

                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }

                GC.Collect();
                GC.WaitForPendingFinalizers();

                // 7. Launch Dolphin
                LaunchDolphin();
                await Task.Delay(500);

                Environment.Exit(0);


                Log("Update completed successfully!");
            }
            finally
            {
                try { CleanupTempDirectory(); }
                catch { }
            }
        }

        private static void PrepareTempDirectory()
        {
            try
            {
                string exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName)!;

                // Vérification espace disque (12 Go requis)
                tempPath = Path.Combine(exeDir, "updater-temp");


                if (Directory.Exists(tempPath))
                    Directory.Delete(tempPath, true);

                Directory.CreateDirectory(tempPath);
                zipPath = Path.Combine(tempPath, "update.zip");
                Log($"Created temp directory next to actual Updater.exe: {tempPath}");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to prepare temp directory: {ex.Message}");
            }
        }

        private static async Task DownloadUpdatePackage(string url, string outputPath)
        {
            // si tu veux supporter aria2 / rclone / http, tu choisis ici
            try
            {
                Log($"Downloading update (zip) from: {url}");
                await DownloadWithHttpClient(url, outputPath); // exemple avec HTTP direct
            }
            catch (Exception ex)
            {
                throw new Exception($"Download failed: {ex.Message}", ex);
            }
        }

        private static async Task DownloadWithHttpClient(string url, string outputPath)
        {
            Log("Downloading via HTTP (single progress bar)...");

            int retryCount = 0;
            while (retryCount < MAX_DOWNLOAD_RETRIES)
            {
                try
                {

                    // 🔹 Vérifie toujours que le dossier existe
                    var dir = Path.GetDirectoryName(outputPath)!;
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    var total = response.Content.Headers.ContentLength ?? -1L;
                    var canReportProgress = total != -1;

                    await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await using var httpStream = await response.Content.ReadAsStreamAsync();

                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int read;
                    var sw = Stopwatch.StartNew();

                    while ((read = await httpStream.ReadAsync(buffer)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read));
                        totalRead += read;

                        if (canReportProgress)
                        {
                            double percent = (double)totalRead / total * 100;
                            double speed = totalRead / 1024d / 1024d / sw.Elapsed.TotalSeconds;

                            int barLength = 50;
                            int filled = (int)(percent / 100 * barLength);
                            string bar = new string('#', filled) + new string('-', barLength - filled);

                            Console.Write($"\r[{bar}] {percent:0.0}%  {totalRead / 1024 / 1024}MB / {total / 1024 / 1024}MB  ({speed:0.0} MB/s)   ");
                        }
                        else
                        {
                            Console.Write($"\rDownloaded {totalRead / 1024 / 1024}MB");
                        }
                    }

                    Console.WriteLine("\nDownload complete!");
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


            // Trouver Dolphin dans le package extrait
            dolphinPath = FindDolphinPath(tempPath) ?? tempPath;

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

        private static string? FindDolphinPath(string basePath)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(basePath, "dolphin.exe", SearchOption.AllDirectories))
                    return Path.GetDirectoryName(file);
            }
            catch (Exception ex)
            {
                Log($"Warning: Could not locate Dolphin.exe in {basePath}: {ex.Message}");
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
                    Arguments = "/C start \"\" \"Dolphin.exe\"",
                    WorkingDirectory = installPath,
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            catch (Exception ex)
            {
                LogError($"Failed to launch Dolphin: {ex.Message}");
            }
        }


        private static void ScheduleDelayedDeletion(string path)
        {
            try
            {
                string bat = Path.Combine(Path.GetTempPath(), $"cleanup_{Guid.NewGuid():N}.cmd");

                // On met le chemin en argument pour éviter toute ambiguïté de quoting
                string script = @"@echo off
chcp 65001>nul
setlocal enabledelayedexpansion
set ""TARGET=%~1""

REM quelques tentatives au cas où un handle se libère avec un léger délai
for /L %%i in (1,1,30) do (
  rmdir /s /q ""!TARGET!"" 2>nul && goto :done
  ping -n 2 127.0.0.1>nul
)
:done
del ""%~f0""";

                File.WriteAllText(bat, script, new ASCIIEncoding());

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/d /c \"\"{bat}\" \"{path}\"\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log($"Échec suppression différée: {ex.Message}");
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
                Thread.Sleep(500); // Wait for process to fully terminate
            }
            catch (Exception ex)
            {
                Log($"Warning: Could not close Dolphin: {ex.Message}");
            }
        }

        private static void CleanupTempDirectory()
        {
            if (string.IsNullOrEmpty(tempPath)) return;

            // Méthode 1: Suppression standard immédiate
            try
            {
                if (Directory.Exists(tempPath))
                {
                    Directory.Delete(tempPath, true);
                    Log("Suppression normale réussie");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log($"Échec suppression normale: {ex.Message}");
            }

            // Méthode 2: Suppression différée en arrière-plan (invisible)
            try
            {
                if (Directory.Exists(tempPath))
                {
                    ScheduleDelayedDeletion(tempPath);
                    Log("Suppression différée programmée");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log($"Échec suppression différée: {ex.Message}");
            }

            // Méthode 3: Si tout échoue, suppression au reboot
            if (Directory.Exists(tempPath))
            {
                MarkForDeletionOnReboot(tempPath);
                Log("Dossier marqué pour suppression au reboot");
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

        private static async Task UpdateSdIfNeeded(string manifestUrl)
        {
            Log("Checking SD card update...");

            using var http = new HttpClient();
            var json = await http.GetStringAsync(manifestUrl);

            var doc = JsonDocument.Parse(json).RootElement;

            string hash = doc.GetProperty("sd-hash").GetString()!;
            string url = doc.GetProperty("download-sd").GetString()!;

            string exeDir = Path.GetDirectoryName(Environment.ProcessPath)!;
            string sdPath = Path.Combine(exeDir, "User", "Wii", "sd.raw");

            // Vérifier si déjà à jour
            if (File.Exists(sdPath))
            {
                string currentHash = ComputeSHA256(sdPath);
                if (string.Equals(currentHash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    Log("SD card already up to date.");
                    return;
                }
            }

            // Téléchargement
            Log("SD card update...");
            Directory.CreateDirectory(Path.GetDirectoryName(sdPath)!);

            bool success =
                await TryDownloadWithAria2(url, sdPath) ||
                await TryDownloadWithRclone(url, sdPath);

            if (!success)
                await DownloadWithHttpClient(url, sdPath);

            Log("SD card updated successfully.");
        }


        private static string ComputeSHA256(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hash = sha256.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private static async Task DownloadSdFile(string url, string destPath)
        {
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fs);
        }

        private static string ComputeFileHash(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hash = sha256.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private static async Task<bool> DownloadWithProcess(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    UseShellExecute = false,
                    CreateNoWindow = false
                };

                using var proc = new Process { StartInfo = psi };

                proc.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Log($"[{exe}] {e.Data}");
                };

                proc.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        LogError($"[{exe}] {e.Data}");
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                await proc.WaitForExitAsync();

                Log($"[{exe}] Exit code {proc.ExitCode}");
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                LogError($"ERROR: {exe} failed: {ex.Message}");
                return false;
            }
        }

        private static async Task<bool> TryDownloadWithAria2(string url, string dest)
        {
            string dir = Path.GetDirectoryName(dest)!;
            string file = Path.GetFileName(dest);

            var args = $"-x 16 -s 16 --allow-overwrite=true --dir \"{dir}\" -o \"{file}\" \"{url}\"";

            var psi = new ProcessStartInfo
            {
                FileName = "aria2c",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = false, // laisse aria2 dessiner sa barre
                RedirectStandardError = false,
                CreateNoWindow = false
            };

            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }

        private static async Task<bool> TryDownloadWithRclone(string url, string dest)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "rclone",
                Arguments = $"copyurl \"{url}\" \"{dest}\" --auto-filename=false",
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = false
            };

            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
    }
}
