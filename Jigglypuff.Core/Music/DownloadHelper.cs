using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus.CommandsNext;
using Jigglypuff.Core.Commands;
using Jigglypuff.Core.Exceptions;

namespace Jigglypuff.Core.Music
{
    public static class DownloadHelper
    {
        public static Dictionary<ulong, List<(Guid guid, Func<string> downloadTask)>> GuildMusicTasks = new Dictionary<ulong, List<(Guid guid, Func<string> downloadTask)>>();

        public static bool IsDownloadLoopRunning;

        public static void DownloadQueue(ulong guildId)
        {
            IsDownloadLoopRunning = true;

            while (true)
            {
                try
                {
                    if (!GuildMusicTasks.ContainsKey(guildId) || GuildMusicTasks[guildId].Count == 0)
                    {
                        continue;
                    }

                    string file = GuildMusicTasks[guildId][0].downloadTask.Invoke();

                    for (int i = 0; i < MusicCommands.GuildQueues[guildId].Count; i++)
                    {
                        JigglySong jigglySong = MusicCommands.GuildQueues[guildId][i];
                        if (jigglySong.Guid == GuildMusicTasks[guildId][0].guid)
                        {
                            jigglySong.File = file;
                        }
                    }

                    GuildMusicTasks[guildId].RemoveAt(0);
                }
                catch (Exception exception)
                {
                    Console.WriteLine(exception);

                    MusicCommands.GuildQueues[guildId].First(e => e.Guid == GuildMusicTasks[guildId][0].guid).File = "error";
                    GuildMusicTasks[guildId].RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// Download the Video from YouTube url and extract it
        /// </summary>
        /// <param name="url">URL to the YouTube Video</param>
        /// <returns>The File Path to the downloaded mp3</returns>
        public static string DownloadFromYouTube(CommandContext ctx, string url)
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

            string result = tcs.Task.Result;
            if (result == null)
            {
                MusicCommands.GuildMusicStatuses[ctx.Guild.Id].Queuing = false;

                throw new OutputException("The song could not be downloaded.");
            }

            //Remove \n at end of Line
            result = result.Replace("\n", "").Replace(Environment.NewLine, "");

            return result;
        }
    }
}
