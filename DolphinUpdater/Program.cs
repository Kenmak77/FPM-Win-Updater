// DolphinUpdater with aria2c, rclone, fallback + scoop integration and update check
using System;
using System.Diagnostics;
using System.Net;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using Ionic.Zip;

namespace DolphinUpdater
{
    class Program
    {
        static string path;
        static string tempPath;
        static string updatedPath;
        static string zipPath;
        private static int counter;

        static async Task Main(string[] args)
        {
            if (args.Length == 0)
                Environment.Exit(0);

            string downloadLink = args[0];
            path = args[1];
            tempPath = path + "/temp/";
            zipPath = tempPath + "temp.zip";

            CloseDolphin();

            await EnsureToolInstalledAndUpdated("aria2");
            await EnsureToolInstalledAndUpdated("rclone");

            if (File.Exists(zipPath))
                File.Delete(zipPath);

            await DownloadZip(downloadLink);
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

        private static async Task EnsureToolInstalledAndUpdated(string tool)
        {
            if (!IsToolAvailable(tool))
            {
                Console.WriteLine($"{tool} is not installed. Install it using Scoop? (Faster Download) (y/n)");
                if (Console.ReadKey(true).Key == ConsoleKey.Y)
                {
                    Console.WriteLine($"Installing {tool} via Scoop...");
                    await RunPowerShell($"if (-not (Get-Command scoop -ErrorAction SilentlyContinue)) {{ iwr get.scoop.sh -UseBasicParsing | iex }}; scoop install {tool}");
                }
            }
            else
            {
                Console.WriteLine($"{tool} is already installed. Update it with Scoop? (y/n)");
                if (Console.ReadKey(true).Key == ConsoleKey.Y)
                {
                    Console.WriteLine($"Updating {tool} via Scoop...");
                    await RunPowerShell($"scoop update {tool}");
                }
            }
        }

        private static async Task RunPowerShell(string command)
        {
            var ps = new ProcessStartInfo("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var proc = Process.Start(ps))
            {
                await Task.Run(() => proc.WaitForExit());
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

        private static async Task DownloadZip(string downloadLink)
        {
            if (!Directory.Exists(tempPath))
                Directory.CreateDirectory(tempPath);

            if (File.Exists(zipPath))
                File.Delete(zipPath);

            string zipFileName = Path.GetFileName(zipPath);
            string zipDir = Path.GetDirectoryName(zipPath);

            // aria2c
            if (IsToolAvailable("aria2c"))
            {
                Console.WriteLine("Downloading with aria2c...");
                string args = $"-x 16 -s 16 -o \"{zipFileName}\" \"{downloadLink}\"";
                var psi = new ProcessStartInfo("aria2c", args)
                {
                    WorkingDirectory = zipDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(psi))
                {
                    process.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    await Task.Run(() => process.WaitForExit());
                    if (File.Exists(zipPath)) return;
                    Console.WriteLine("aria2c failed.");
                }
            }

            // rclone
            if (IsToolAvailable("rclone"))
            {
                Console.WriteLine("Downloading with rclone...");
                string args = $"copyurl \"{downloadLink}\" \"{zipFileName}\" --multi-thread-streams=8 -P";
                var psi = new ProcessStartInfo("rclone", args)
                {
                    WorkingDirectory = zipDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(psi))
                {
                    process.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    await Task.Run(() => process.WaitForExit());
                    if (File.Exists(zipPath)) return;
                    Console.WriteLine("rclone failed.");
                }
            }

            // fallback HTTP
            Console.WriteLine("Downloading with WebClient...");
            using (var client = new WebClient())
            {
                client.DownloadProgressChanged += new DownloadProgressChangedEventHandler(client_DownloadProgressChanged);
                await client.DownloadFileTaskAsync(new Uri(downloadLink), zipPath);
            }
        }

        private static void client_DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            counter++;
            if (counter % 5500 == 0)
            {
                Console.Clear();
                Console.WriteLine("\rDownloaded "
                                  + ((e.BytesReceived / 1024f) / 1024f).ToString("#0.##") + "mb"
                                  + " of "
                                  + ((e.TotalBytesToReceive / 1024f) / 1024f).ToString("#0.##") + "mb"
                                  + "  (" + e.ProgressPercentage + "%)"
                    );
            }
        }

        private static void ExtractZip(string FileZipPath, string OutputFolder)
        {
            using (ZipFile zip = ZipFile.Read(FileZipPath))
            {
                foreach (ZipEntry e in zip)
                {
                    string[] skipFiles = { "dolphin.log", "dolphin.ini", "gfx.ini", "vcruntime140_1.dll", "gckeynew.ini", "gcpadnew.ini", "hotkeys.ini", "logger.ini", "debugger.ini", "wiimotenew.ini" };

                    if (!skipFiles.Any(e.FileName.ToLower().Contains))
                    {
                        Console.WriteLine("Extracting " + e.FileName);
                        e.Extract(OutputFolder, ExtractExistingFileAction.OverwriteSilently);
                    }
                }
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
    }
}
