using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Net;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Ionic.Zip;

namespace DolphinUpdater
{
    class Program
    {
        static WebClient Client = new WebClient();
        static string updateData = Client.DownloadString("https://raw.githubusercontent.com/Birdthulu/birdthulu.github.io/master/Update.json");
        static dynamic data = JsonConvert.DeserializeObject<dynamic>(updateData);
        static string downloadWindows = data["download-page-windows"].ToString();
        static string currentPath = Directory.GetCurrentDirectory();
        static string path = Directory.GetCurrentDirectory() + "/temp/";
        static string zipPath = path + "temp.zip";
        static string dolphinPath = currentPath + "/Dolphin.exe";
        private static int counter;

        static async Task Main(string[] args)
        {
            await Task.Run(() => DownloadZip(downloadWindows));
            ExtractZip();

            Directory.Delete(path, true);

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
                using (var client = new WebClient())
                {
                    client.DownloadProgressChanged += new DownloadProgressChangedEventHandler(client_DownloadProgressChanged);
                    await client.DownloadFileTaskAsync(new Uri(downloadlink), zipPath);
                }
            }
        }

        private static void ExtractZip()
        {
            using (ZipFile zip = ZipFile.Read(zipPath))
            {
                foreach (ZipEntry e in zip)
                {
                    Console.WriteLine("Extracting " + e.FileName);
                    e.Extract(currentPath, ExtractExistingFileAction.OverwriteSilently);
                }
            }

            Console.WriteLine("Finished!");
        }

        private static void client_DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            counter++;
            if (counter % 5500 == 0)
            {
                Console.WriteLine("Downloaded "
                                  + ((e.BytesReceived / 1024f) / 1024f).ToString("#0.##") + "mb"
                                  + " of "
                                  + ((e.TotalBytesToReceive / 1024f) / 1024f).ToString("#0.##") + "mb"
                                  + "  (" + e.ProgressPercentage + "%)"
                    );
            }
        }
    }
}
