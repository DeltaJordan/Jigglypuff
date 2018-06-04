using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus.CommandsNext;
using Jigglypuff.Core.Exceptions;

namespace Jigglypuff.Core.Music
{
    public static class DownloadHelper
    {
        /// <summary>
        /// Download the Video from YouTube url and extract it
        /// </summary>
        /// <param name="url">URL to the YouTube Video</param>
        /// <returns>The File Path to the downloaded mp3</returns>
        public static async Task<string> DownloadFromYouTube(CommandContext ctx, string url)
        {
            TaskCompletionSource<string> tcs = new TaskCompletionSource<string>();

            string downloadPath = Directory.CreateDirectory(Path.Combine(Globals.AppPath, "Queue", ctx.Guild.Id.ToString())).FullName;

            new Thread(() => {
                string file;
                int count = 0;
                do
                {
                    file = Path.Combine(downloadPath, "botsong" + ++count + ".mp3");
                } while (File.Exists(file));

                //youtube-dl.exe
                Process youtubedl;

                //Download Video
                ProcessStartInfo youtubedlDownload = new ProcessStartInfo()
                {
                    FileName = "youtube-dl",
                    Arguments = $"-x --audio-format mp3 -o \"{file.Replace(".mp3", ".%(ext)s")}\" {url}",
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };
                youtubedl = Process.Start(youtubedlDownload);
                //Wait until download is finished
                youtubedl?.WaitForExit();
                tcs.SetResult(file);
            }).Start();

            string result = await tcs.Task;
            if (result == null)
                throw new OutputException("The song could not be downloaded.");

            //Remove \n at end of Line
            result = result.Replace("\n", "").Replace(Environment.NewLine, "");

            return result;
        }
    }
}
