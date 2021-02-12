using System;
using System.Diagnostics;
using System.Net;
using System.IO;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;

namespace DolphinUpdater
{
    class Program
    {
        static string currentPath = Directory.GetCurrentDirectory();
        static string path;
        static string zipPath;
        private static int counter;

        static async Task Main(string[] args)
        {
            if (args.Length == 0)
                Environment.Exit(0);

            string downloadLink = args[0];
            path = args[1] + "/temp/";
            zipPath = path + "temp.zip";

            await Task.Run(() => DownloadZip(downloadLink));
            ExtractZip(zipPath, currentPath);

            Directory.Delete(path, true);

            string dolphinPath = currentPath + "/Dolphin.exe";
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

        private static async Task DownloadZip(string downloadlink)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            if (!File.Exists(zipPath))
            {
                Console.WriteLine("Starting download....");
                using (var client = new WebClient())
                {
                    client.DownloadProgressChanged += new DownloadProgressChangedEventHandler(client_DownloadProgressChanged);
                    await client.DownloadFileTaskAsync(new Uri(downloadlink), zipPath);
                }
            }
        }

        public static void ExtractZip(string FileZipPath, string OutputFolder)
        {
            ZipFile file = null;
            try
            {
                FileStream fs = File.OpenRead(FileZipPath);
                file = new ZipFile(fs);

                foreach (ZipEntry zipEntry in file)
                {
                    if (!zipEntry.IsFile)
                    {
                        continue;
                    }

                    String entryFileName = zipEntry.Name;
                    String entryFileNamePathless = Path.GetFileName(entryFileName);

                    string[] skipFiles = { "dolphin.log", "dolphin.ini" };

                    if (Array.Exists(skipFiles, element => element.Equals(entryFileNamePathless.ToLower())) == true) {}
                    else
                    {
                        Console.WriteLine("Extracting " + entryFileName);
                        byte[] buffer = new byte[4096];
                        Stream zipStream = file.GetInputStream(zipEntry);

                        String fullZipToPath = Path.Combine(OutputFolder, entryFileName);
                        string directoryName = Path.GetDirectoryName(fullZipToPath);

                        if (directoryName.Length > 0)
                        {
                            Directory.CreateDirectory(directoryName);
                        }

                        using (FileStream streamWriter = File.Create(fullZipToPath))
                        {
                            ICSharpCode.SharpZipLib.Core.StreamUtils.Copy(zipStream, streamWriter, buffer);
                        }
                    }
                }
            }
            finally
            {
                if (file != null)
                {
                    file.IsStreamOwner = true;
                    file.Close();
                }
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
    }
}
