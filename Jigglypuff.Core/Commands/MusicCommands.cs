using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using DSharpPlus;
using DSharpPlus.CommandsNext;
using DSharpPlus.CommandsNext.Attributes;
using DSharpPlus.Entities;
using DSharpPlus.Interactivity;
using DSharpPlus.VoiceNext;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Jigglypuff.Core.Exceptions;
using Jigglypuff.Core.Extensions;
using Jigglypuff.Core.Music;
using VideoLibrary;
using Video = Google.Apis.YouTube.v3.Data.Video;

namespace Jigglypuff.Core.Commands
{
    [Group("music"), Aliases("m")]
    public class MusicCommands : BaseCommandModule
    {
        private static Dictionary<ulong, List<JigglySong>> guildQueues = new Dictionary<ulong, List<JigglySong>>();

        private static Dictionary<ulong, MusicStatus> guildMusicStatuses = new Dictionary<ulong, MusicStatus>();

        [Command("join"), Aliases("j")]
        public async Task Join(CommandContext ctx)
        {
            VoiceNextExtension vnext = ctx.Client.GetVoiceNext();

            VoiceNextConnection vnc = vnext.GetConnection(ctx.Guild);
            if (vnc != null)
            {
                throw new OutputException("Already connected in this guild.");
            }

            DiscordChannel chn = ctx.Member?.VoiceState?.Channel;
            if (chn == null)
            {
                throw new OutputException("You need to be in a voice channel.");
            }

            vnc = await vnext.ConnectAsync(chn);

            await ctx.RespondAsync($"Connected to channel {vnc.Channel.Name} successfully.");
        }

        [Command("leave"), Aliases("l"), Description("Causes Jigglypuff to leave the currently joined voice channel.")]
        public async Task Leave(CommandContext ctx, bool clearQueue = false)
        {
            VoiceNextExtension vnext = ctx.Client.GetVoiceNext();

            VoiceNextConnection vnc = vnext.GetConnection(ctx.Guild);
            if (vnc == null)
            {
                throw new OutputException("Not connected in this guild.");
            }

            if (clearQueue)
            {
                guildQueues[ctx.Guild.Id].Clear();
            }
            guildMusicStatuses[ctx.Guild.Id].Skip = true;
            Thread.Sleep(500);
            guildMusicStatuses.Remove(ctx.Guild.Id);
            Directory.Delete(Path.Combine(Globals.AppPath, "Queue", ctx.Guild.Id.ToString()), true);

            vnc.Disconnect();
            await ctx.RespondAsync("Left connected channel.");
        }

        [Command("repeat"), Aliases("r")]
        public async Task Repeat(CommandContext ctx, [RemainingText] string value)
        {
            if (Enum.TryParse(typeof(MusicStatus.RepeatType), value, true, out object result))
            {
                MusicStatus.RepeatType repeatType = result is MusicStatus.RepeatType type ? type : MusicStatus.RepeatType.None;

                if (repeatType == MusicStatus.RepeatType.None)
                {
                    throw new OutputException($"Invalid input. Valid inputs: `One`, `All`, `None`.");
                }

                guildMusicStatuses[ctx.Guild.Id].Repeat = repeatType;

                switch (repeatType)
                {
                    case MusicStatus.RepeatType.None:
                        await ctx.RespondAsync("Now not repeating any songs.");
                        break;
                    case MusicStatus.RepeatType.One:
                        await ctx.RespondAsync("Now repeating current song.");
                        break;
                    case MusicStatus.RepeatType.All:
                        await ctx.RespondAsync("Now repeating all songs in queue.");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            else
            {
                throw new OutputException("Invalid input. Valid inputs: `One`, `All`, `None`.");
            }
        }

        [Command("play"), Aliases("p"), Description("Begins playing enqueued songs and is also used as an alias for pausing if already playing.")]
        public async Task Play(CommandContext ctx, [RemainingText] string queryString)
        {
            VoiceNextExtension vnext = ctx.Client.GetVoiceNext();

            VoiceNextConnection vnc = vnext.GetConnection(ctx.Guild);
            if (vnc == null)
            {

                DiscordChannel chn = ctx.Member?.VoiceState?.Channel;
                if (chn == null)
                {
                    throw new OutputException("You need to be in a voice channel.");
                }

                await vnext.ConnectAsync(chn);
            }

            if (!string.IsNullOrWhiteSpace(queryString))
            {
                await this.Queue(ctx, queryString);
                return;
            }

            if (!guildMusicStatuses.TryGetValue(ctx.Guild.Id, out MusicStatus _))
            {
                guildMusicStatuses.Add(ctx.Guild.Id, new MusicStatus {Skip = false});
            }

            PlayMusic(ctx);
        }

        [Command("skip"), Aliases("s"), Description("Skips the currently playing song.")]
        public async Task Skip(CommandContext ctx)
        {
            guildMusicStatuses[ctx.Guild.Id].Skip = true;
        }

        [Command("queueplaylist"), Aliases("qp"), Description("Enqueues the requested playlist. Note that at the moment this only retrieves the first 50 videos.")]
        public async Task QueuePlaylist(CommandContext ctx, string playlistUrl, [Description("Shuffle the playlist when adding to queue. \"true\" to enable.")] bool shuffle = false)
        {
            Uri playlistUri;

            try
            {
                playlistUri = new Uri(playlistUrl);
            }
            catch
            {
                throw new OutputException("Invalid url.");
            }

            NameValueCollection query = HttpUtility.ParseQueryString(playlistUri.Query);

            if (!query.AllKeys.Contains("list"))
            {
                throw new OutputException("Url must point to a YouTube playlist, specifically with a \"list=\" query.");
            }

            YouTubeService youtubeService = new YouTubeService(new BaseClientService.Initializer
            {
                ApiKey = Globals.BotSettings.YoutubeApiKey,
                ApplicationName = this.GetType().ToString()
            });

            PlaylistsResource.ListRequest playlistListRequest = youtubeService.Playlists.List("snippet");
            playlistListRequest.Id = query["list"];
            playlistListRequest.MaxResults = 1;

            PlaylistListResponse playlistListResponse = playlistListRequest.Execute();

            PlaylistItemsResource.ListRequest playlistItemsListRequest = youtubeService.PlaylistItems.List("snippet");
            playlistItemsListRequest.PlaylistId = query["list"];
            playlistItemsListRequest.MaxResults = 50;

            PlaylistItemListResponse playlistItemsListResponse = playlistItemsListRequest.Execute();

            if (!playlistItemsListResponse.Items.Any())
            {
                throw new OutputException("Unable to retrieve playlist or playlist is empty.");
            }

            if (shuffle)
            {
                playlistItemsListResponse.Items.Shuffle();
            }

            int resultQueueCount = guildQueues.ContainsKey(ctx.Guild.Id) ? guildQueues[ctx.Guild.Id].Count + playlistItemsListResponse.Items.Count : playlistItemsListResponse.Items.Count;

            await ctx.RespondAsync($"Queuing {playlistItemsListResponse.Items.Count} songs, please be patient if this is the first items to be added to queue. " +
                                    "(If you try to play music and nothing happens most likely the first song is still pending)");

            foreach (PlaylistItem playlistItem in playlistItemsListResponse.Items)
            {
                string id = playlistItem.Snippet.ResourceId.VideoId;

                VideosResource.ListRequest idSearch = youtubeService.Videos.List("id,snippet");
                idSearch.Id = id;
                idSearch.MaxResults = 1;

                VideoListResponse videoListResponse = await idSearch.ExecuteAsync();

                if (videoListResponse.Items.Count == 0)
                {
                    await ctx.RespondAsync("Video link cannot be parsed.");
                    return;
                }

                if (!guildQueues.ContainsKey(ctx.Guild.Id))
                {
                    guildQueues.Add(ctx.Guild.Id, new List<JigglySong>());
                }

                Video parsedVideo = videoListResponse.Items.First();

                if (!string.IsNullOrWhiteSpace(parsedVideo.ContentDetails?.Duration) && XmlConvert.ToTimeSpan(parsedVideo.ContentDetails.Duration).Minutes > 15)
                {
                    await ctx.RespondAsync("This video is too long, please try to find something shorter than 15 minutes.");
                }

                AddSong(parsedVideo.Snippet.Title, parsedVideo.Id, await DownloadHelper.DownloadFromYouTube(ctx, $"https://www.youtube.com/watch?v={id}"), parsedVideo.Snippet.ChannelTitle, JigglySong.SongType.Youtube, ctx.Member);
            }

            DiscordEmbedBuilder confirmationBuilder = new DiscordEmbedBuilder
            {
                Description = $"**✅ Successfully added [{playlistListResponse.Items.First().Snippet.Title}](https://www.youtube.com/playlist?list={query["list"]}) " +
                              $"to queue positions {resultQueueCount + 1 - playlistItemsListResponse.Items.Count}-{resultQueueCount}**"
            };

            await ctx.RespondAsync(null, false, confirmationBuilder.Build());
        }

        [Command("queue"), Aliases("q"), Description("Enqueues the requested song or displays the queue if nothing is requested.")]
        public async Task Queue(CommandContext ctx, [Description("A search query or a direct link to the video."), RemainingText] string queryString)
        {
            if (string.IsNullOrWhiteSpace(queryString))
            {
                if (!guildQueues.TryGetValue(ctx.Guild.Id, out List<JigglySong> resultSongs))
                {
                    await ctx.RespondAsync("There is nothing currently queued.");
                    return;
                }

                DiscordEmbedBuilder queueBuilder = new DiscordEmbedBuilder
                {
                    Title = "Current Queue",
                    Color = DiscordColor.Aquamarine
                };

                string tracks = string.Empty;

                for (int i = 0; i < resultSongs.Count; i++)
                {
                    JigglySong resultSong = resultSongs[i];

                    tracks += $"{i + 1}: [**{resultSong.Title}** by **{resultSong.Artist}**](https://www.youtube.com/watch?v={resultSong.Id})";
                }

                queueBuilder.Description = tracks;

                await ctx.RespondAsync(string.Empty, false, queueBuilder.Build());
                return;
            }

            YouTubeService youtubeService = new YouTubeService(new BaseClientService.Initializer
            {
                ApiKey = Globals.BotSettings.YoutubeApiKey,
                ApplicationName = this.GetType().ToString()
            });

            try
            {
                Uri uri = new Uri(queryString);

                if (uri.Host != "youtu.be" || !uri.Host.Contains("youtube"))
                {
                    throw new ArgumentException();
                }

                NameValueCollection query = HttpUtility.ParseQueryString(uri.Query);

                string id = query.AllKeys.Contains("v") ? query["v"] : uri.Segments.Last();

                VideosResource.ListRequest idSearch = youtubeService.Videos.List("id,snippet");
                idSearch.Id = id;
                idSearch.MaxResults = 1;

                VideoListResponse videoListResponse = await idSearch.ExecuteAsync();

                if (videoListResponse.Items.Count == 0)
                {
                    await ctx.RespondAsync("Video link cannot be parsed.");
                    return;
                }

                if (!guildQueues.ContainsKey(ctx.Guild.Id))
                {
                    guildQueues.Add(ctx.Guild.Id, new List<JigglySong>());
                }

                Video parsedVideo = videoListResponse.Items.First();

                if (!string.IsNullOrWhiteSpace(parsedVideo.ContentDetails?.Duration) && XmlConvert.ToTimeSpan(parsedVideo.ContentDetails.Duration).Minutes > 15)
                {
                    await ctx.RespondAsync("This video is too long, please try to find something shorter than 15 minutes.");
                }

                AddSong(parsedVideo.Snippet.Title, parsedVideo.Id, await DownloadHelper.DownloadFromYouTube(ctx, queryString), parsedVideo.Snippet.ChannelTitle, JigglySong.SongType.Youtube, ctx.Member);

                DiscordEmbedBuilder confirmationBuilder = new DiscordEmbedBuilder
                {
                    Description = $"**✅ Successfully added [{parsedVideo.Snippet.Title}](https://www.youtube.com/watch?v={parsedVideo.Id}) to queue position {guildQueues[ctx.Guild.Id].Count}**"
                };

                await ctx.RespondAsync(null, false, confirmationBuilder.Build());
            }
            catch
            {

                SearchResource.ListRequest searchListRequest = youtubeService.Search.List("snippet");
                searchListRequest.Q = queryString.Replace(" ", "+");
                searchListRequest.MaxResults = 10;
                searchListRequest.Type = "video";

                // Call the search.list method to retrieve results matching the specified query term.
                SearchListResponse searchListResponse = await searchListRequest.ExecuteAsync();

                DiscordEmbedBuilder builder = new DiscordEmbedBuilder
                {
                    Title = "Enter the number of your selection."
                };

                List<JigglySong> videos = new List<JigglySong>();
                string selections = string.Empty;

                // Add each result to the appropriate list, and then display the lists of
                // matching videos, channels, and playlists.
                for (int i = 0; i < 5; i++)
                {
                    if (!searchListResponse.Items.Any())
                    {
                        await ctx.RespondAsync("No available tracks less than 15 minutes.");
                        return;
                    }

                    SearchResult searchResult = searchListResponse.Items[i];

                    VideosResource.ListRequest listRequest = youtubeService.Videos.List("snippet");
                    listRequest.Id = searchResult.Id.VideoId;

                    if (!string.IsNullOrWhiteSpace((await listRequest.ExecuteAsync()).Items.First().ContentDetails?.Duration) && XmlConvert.ToTimeSpan((await listRequest.ExecuteAsync()).Items.First().ContentDetails.Duration).Minutes > 15)
                    {
                        searchListResponse.Items.RemoveAt(i);
                        i--;
                        continue;
                    }

                    selections += $"{i + 1}: {searchResult.Snippet.Title}\n";
                    videos.Add(new JigglySong
                    {
                        Title = searchResult.Snippet.Title,
                        Id = searchResult.Id.VideoId,
                        Artist = searchResult.Snippet.ChannelTitle,
                        Type = JigglySong.SongType.Youtube,
                        Queuer = ctx.Member
                    });
                }

                selections += "c: Cancel";

                builder.Description = selections;

                DiscordMessage resultsMessage = await ctx.RespondAsync(string.Empty, false, builder.Build());

                int result = -1;

                MessageContext msgContext = await ctx.Client.GetInteractivity().WaitForMessageAsync(e => e.Author.Id == ctx.Message.Author.Id && (e.Content.ToLower() == "c" || int.TryParse(e.Content, out result) && result > 0 && result <= videos.Count), TimeSpan.FromSeconds(30));

                if (msgContext == null)
                {
                    await ctx.RespondAsync($"🖋*Jigglypuff wrote on {ctx.User.Mention}'s face!*🖋\nMaybe they should have picked a song...");
                    await resultsMessage.DeleteAsync();
                    return;
                }

                result--;

                if (result >= 0)
                {
                    if ((await ctx.Guild.GetMemberAsync(ctx.Client.CurrentUser.Id)).PermissionsIn(ctx.Channel).HasPermission(Permissions.ManageMessages))
                    {
                        await msgContext.Message.DeleteAsync();
                    }

                    if (!guildQueues.ContainsKey(ctx.Guild.Id))
                    {
                        guildQueues.Add(ctx.Guild.Id, new List<JigglySong>());
                    }

                    JigglySong selectedSong = videos[result];

                    DiscordEmbedBuilder confirmationBuilder = new DiscordEmbedBuilder
                    {
                        Description = $"**✅ Successfully added [{videos[result].Title}](https://www.youtube.com/watch?v={videos[result].Id}) to queue position {guildQueues[ctx.Guild.Id].Count + 1}**"
                    };

                    if (guildQueues[ctx.Guild.Id].Count == 0)
                    {
                        confirmationBuilder.Description += "\nPlease be patient; it takes a bit for the first song to cache.";
                    }

                    await ctx.RespondAsync(string.Empty, false, confirmationBuilder.Build());

                    AddSong(selectedSong.Title, selectedSong.Id, await DownloadHelper.DownloadFromYouTube(ctx, $"https://www.youtube.com/watch?v={videos[result].Id}"), selectedSong.Artist, selectedSong.Type, selectedSong.Queuer);
                    
                    if (!guildMusicStatuses.TryGetValue(ctx.Guild.Id, out MusicStatus musicStatus))
                    {
                        guildMusicStatuses.Add(ctx.Guild.Id, new MusicStatus
                        {
                            Skip = false
                        });

                        if (ctx.Client.GetVoiceNext().GetConnection(ctx.Guild) != null)
                        {
                            PlayMusic(ctx);
                        }
                    }
                    else
                    {
                        if (!musicStatus.Skip && ctx.Client.GetVoiceNext().GetConnection(ctx.Guild) != null)
                        {
                            PlayMusic(ctx);
                        }
                    }
                }
                else
                {
                    DiscordEmbedBuilder confirmationBuilder = new DiscordEmbedBuilder
                    {
                        Title = "🚫 Canceled queue."
                    };

                    await ctx.RespondAsync(string.Empty, false, confirmationBuilder.Build());
                }
            }
        }

        private static void AddSong(string title, string id, string file, string artist, JigglySong.SongType type, DiscordMember member)
        {
            guildQueues[member.Guild.Id].Add(new JigglySong
            {
                Title = title,
                Id = id,
                File = file,
                Artist = artist,
                Type = JigglySong.SongType.Youtube,
                Queuer = member
            });
        }

        private static async void PlayMusic(CommandContext ctx)
        {
            await PlaySong(ctx);
        }

        private static async Task PlaySong(CommandContext ctx)
        {
            if (guildMusicStatuses[ctx.Guild.Id].Playing)
            {
                return;
            }
            
            if (guildQueues[ctx.Guild.Id].Count == 0)
            {
                throw new OutputException("No songs in queue! If you queued a song and this message shows, either it is still being locally queued or it silently failed to be retrieved.");
            }

            guildMusicStatuses[ctx.Guild.Id].Playing = true;

            while (true)
            {
                VoiceNextExtension vnext = ctx.Client.GetVoiceNext();

                VoiceNextConnection vnc = vnext.GetConnection(ctx.Guild);

                if (vnc == null || !guildQueues[ctx.Guild.Id].Any())
                {
                    break;
                }

                DiscordEmbedBuilder nowplayingBuilder = new DiscordEmbedBuilder
                {
                    Description = $"🎶 Now playing [{guildQueues[ctx.Guild.Id].First().Title}](https://www.youtube.com/watch?v={guildQueues[ctx.Guild.Id].First().Id}) 🎶\n\n" +
                                  $"[{guildQueues[ctx.Guild.Id].First().Queuer.Mention}]{(guildMusicStatuses[ctx.Guild.Id].Repeat == MusicStatus.RepeatType.None ? "" : " [🔁]")}"
                };

                guildMusicStatuses[ctx.Guild.Id].Skip = false;

                await ctx.RespondAsync(null, false, nowplayingBuilder.Build());

                string songFile = guildQueues[ctx.Guild.Id].First().File;

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $@"-i ""{songFile}"" -ac 2 -f s16le -ar 48000 pipe:1",
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };

                Process ffmpeg = Process.Start(startInfo);

                Stream ffout = ffmpeg.StandardOutput.BaseStream;

                await vnc.SendSpeakingAsync(); // send a speaking indicator

                byte[] buff = new byte[3840]; // buffer to hold the PCM data
                while (await ffout.ReadAsync(buff, 0, buff.Length) > 0)
                {
                    if (guildMusicStatuses[ctx.Guild.Id].Skip)
                    {
                        break;
                    }

                    await vnc.SendAsync(buff, 20); // we're sending 20ms of data

                    buff = new byte[3840];
                }

                try
                {
                    ffout.Flush();
                    ffout.Dispose();
                    ffmpeg.Dispose();
                    if (guildMusicStatuses[ctx.Guild.Id].Repeat == MusicStatus.RepeatType.None)
                    {
                        File.Delete(songFile);
                    }
                }
                catch
                {
                    // Consume errors.
                }

                await vnc.SendSpeakingAsync(false);

                switch (guildMusicStatuses[ctx.Guild.Id].Repeat)
                {
                    case MusicStatus.RepeatType.None:
                        guildQueues[ctx.Guild.Id].RemoveAt(0);
                        break;
                    case MusicStatus.RepeatType.All:
                        JigglySong jigglySong = guildQueues[ctx.Guild.Id][0];
                        guildQueues[ctx.Guild.Id].Add(jigglySong);
                        guildQueues[ctx.Guild.Id].RemoveAt(0);
                        break;
                    case MusicStatus.RepeatType.One:
                        // The Song is still number one in queue ;D
                        break;
                    default:
                        guildQueues[ctx.Guild.Id].RemoveAt(0);
                        break;
                }

                guildMusicStatuses[ctx.Guild.Id].Skip = false;
            }

            ctx.Client.GetVoiceNext().GetConnection(ctx.Guild)?.Disconnect();

            guildMusicStatuses[ctx.Guild.Id].Playing = false;
        }
    }
}
