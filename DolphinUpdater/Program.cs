// DolphinUpdater with Scoop install integration and SHA1 hash verification
using System;
using System.Diagnostics;
using System.Net;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using ICSharpCode.SharpZipLib.Zip; // Remplace DotNetZip pour éviter les failles de sécurité

namespace DolphinUpdater
{
    class Program
    {
        static string path;
        static string tempPath;
        static string updatedPath;
        static string zipPath;
        private static int counter;
        static bool allowAria2c = false;
        static bool allowRclone = false;

        static async Task Main(string[] args)
        {
            if (args.Length == 0)
                Environment.Exit(0);

            string downloadLink = args[0];
            path = args[1];
            tempPath = path + "/temp/";
            zipPath = tempPath + "temp.zip";

            CloseDolphin();

            allowAria2c = AskToolPermission("aria2");
            allowRclone = AskToolPermission("rclone");

            if (File.Exists(zipPath))
                File.Delete(zipPath);

            await DownloadZip(downloadLink);

            string expectedHash = GetExpectedZipHash();
            string actualHash = GetFileSHA1(zipPath);
            if (!string.IsNullOrEmpty(expectedHash) && actualHash != expectedHash)
            {
                Console.WriteLine("⚠️ SHA1 hash mismatch! The downloaded file may be corrupted or tampered with.");
                Console.WriteLine("Expected: " + expectedHash);
                Console.WriteLine("Actual:   " + actualHash);
                Console.WriteLine("Aborting update process.");
                return;
            }

            ExtractZip(zipPath, tempPath);
            GetDolphinPath();

            if (updatedPath == null)
                updatedPath = tempPath;

            Console.WriteLine("Moving files. Please wait...");
            MoveUpdateFiles(updatedPath, path);

            Directory.Delete(tempPath, true);

            Console.WriteLine("Finished! You can close this window if it's still open!");

            string dolphinPath = path + "/Dolphin.exe";
            if (File.Exists(dolphinPath))
                Process.Start(dolphinPath);
            else
            {
                do
                {
                    Console.WriteLine("Dolphin.exe not found! Press the enter key to close this application.");
                } while (Console.ReadKey(true).Key != ConsoleKey.Enter);
            }
        }

        private static void GetDolphinPath()
        {
            DirectoryInfo diTop = new DirectoryInfo(tempPath);
            foreach (var di in diTop.EnumerateDirectories("*"))
            {
                try
                {
                    foreach (var fi in di.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            if (fi.Name.ToLower() == "dolphin.exe")
                            {
                                updatedPath = fi.FullName.ToLower().Replace("dolphin.exe", "");
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        private static void MoveUpdateFiles(string updateFilesPath, string destinationPath)
        {
            if (!Directory.Exists(destinationPath))
            {
                Directory.CreateDirectory(destinationPath);
            }

            foreach (string folder in Directory.GetDirectories(updateFilesPath))
            {
                string dest = Path.Combine(destinationPath, Path.GetFileName(folder));
                MoveUpdateFiles(folder, dest);
            }

            foreach (string file in Directory.GetFiles(updateFilesPath))
            {
                string dest = Path.Combine(destinationPath, Path.GetFileName(file));

                if (File.Exists(dest))
                    File.Delete(dest);

                if (!file.Contains("temp.zip"))
                    File.Copy(file, dest);
            }
        }

        private static bool AskToolPermission(string toolName)
        {
            Console.WriteLine($"Allow use of {toolName}? (Faster Download) (y/n)");
            return Console.ReadKey(true).Key == ConsoleKey.Y;
        }

        private static string GetExpectedZipHash()
        {
            try
            {
                using (var client = new WebClient())
                {
                    string json = client.DownloadString("https://update.pplusfr.org/update.json");
                    var match = System.Text.RegularExpressions.Regex.Match(json, "\"zip-hash\"\\s*:\\s*\"([a-fA-F0-9]+)\"");
                    if (match.Success)
                        return match.Groups[1].Value.ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to fetch expected hash: " + ex.Message);
            }
            return null;
        }

        private static string GetFileSHA1(string filePath)
        {
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (var sha1 = System.Security.Cryptography.SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(fs);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static async Task DownloadZip(string downloadLink)
        {
            if (!Directory.Exists(tempPath))
                Directory.CreateDirectory(tempPath);

            if (File.Exists(zipPath))
                File.Delete(zipPath);

            string zipFileName = Path.GetFileName(zipPath);
            string zipDir = Path.GetDirectoryName(zipPath);

            if (allowAria2c && IsToolAvailable("aria2c"))
            {
                Console.WriteLine("Downloading with aria2c...");
                string arguments = $"-x 16 -s 16 --summary-interval=1 -o \"{zipFileName}\" \"{downloadLink}\"";
                if (await RunExternalDownloader("aria2c", arguments, zipDir)) return;
                Console.WriteLine("aria2c failed. Trying rclone...");
            }

            if (allowRclone && IsToolAvailable("rclone"))
            {
                Console.WriteLine("Downloading with rclone...");
                string arguments = $"copyurl \"{downloadLink}\" \"{zipFileName}\" --multi-thread-streams=8 -P";
                if (await RunExternalDownloader("rclone", arguments, zipDir)) return;
                Console.WriteLine("rclone failed. Trying WebClient...");
            }

            Console.WriteLine("Downloading with WebClient...");
            using (var client = new WebClient())
            {
                client.DownloadProgressChanged += new DownloadProgressChangedEventHandler(Client_DownloadProgressChanged);
                await client.DownloadFileTaskAsync(new Uri(downloadLink), zipPath);
            }
        }

        private static void Client_DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            counter++;
            if (counter % 5500 == 0)
            {
                Console.Clear();
                Console.WriteLine($"\rDownloaded {(e.BytesReceived / 1024f) / 1024f:0.##}mb of {(e.TotalBytesToReceive / 1024f) / 1024f:0.##}mb ({e.ProgressPercentage}%)");
            }
        }

        private static bool IsToolAvailable(string tool)
        {
            try
            {
                var psi = new ProcessStartInfo("where", tool)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    return !string.IsNullOrWhiteSpace(output);
                }
            }
            catch { return false; }
        }

        private static async Task<bool> RunExternalDownloader(string exePath, string arguments, string workingDir)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = psi })
            {
                process.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await Task.Run(() => process.WaitForExit());

                string outputFile = Path.Combine(workingDir, Path.GetFileName(zipPath));
                return process.ExitCode == 0 && File.Exists(outputFile);
            }
        }

        private static void CloseDolphin()
        {
            var process = new Process();
            var startInfo = new ProcessStartInfo
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                FileName = "cmd.exe",
                Arguments = "/C taskkill /f /im \"Dolphin.exe\""
            };
            process.StartInfo = startInfo;
            process.Start();
            process.Close();
        }

        public static void ExtractZip(string FileZipPath, string OutputFolder)
        {
            using (var fs = File.OpenRead(FileZipPath))
            using (var zipInput = new ZipInputStream(fs))
            {
                ZipEntry entry;
                while ((entry = zipInput.GetNextEntry()) != null)
                {
                    string[] skipFiles = { "dolphin.log", "dolphin.ini", "gfx.ini", "vcruntime140_1.dll", "gckeynew.ini", "gcpadnew.ini", "hotkeys.ini", "logger.ini", "debugger.ini", "wiimotenew.ini" };
                    if (skipFiles.Any(name => entry.Name.ToLower().Contains(name)))
                        continue;

                    Console.WriteLine("Extracting " + entry.Name);
                    string fullPath = Path.Combine(OutputFolder, entry.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                    using (var streamWriter = File.Create(fullPath))
                    {
                        zipInput.CopyTo(streamWriter);
                    }
                }
            }
        }
    }
}
