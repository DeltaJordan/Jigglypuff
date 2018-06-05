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
        public static readonly Dictionary<ulong, List<JigglySong>> GuildQueues = new Dictionary<ulong, List<JigglySong>>();

        public static readonly Dictionary<ulong, MusicStatus> GuildMusicStatuses = new Dictionary<ulong, MusicStatus>();

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
                GuildQueues[ctx.Guild.Id].Clear();
            }
            GuildMusicStatuses[ctx.Guild.Id].Skip = true;
            Thread.Sleep(500);
            GuildMusicStatuses.Remove(ctx.Guild.Id);
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

                GuildMusicStatuses[ctx.Guild.Id].Repeat = repeatType;

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
                
                if (!GuildMusicStatuses.TryGetValue(ctx.Guild.Id, out MusicStatus musicStatus))
                {
                    GuildMusicStatuses.Add(ctx.Guild.Id, new MusicStatus
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

                return;
            }

            if (!GuildMusicStatuses.TryGetValue(ctx.Guild.Id, out MusicStatus _))
            {
                GuildMusicStatuses.Add(ctx.Guild.Id, new MusicStatus {Skip = false});
            }

            PlayMusic(ctx);
        }

        [Command("skip"), Aliases("s"), Description("Skips the currently playing song.")]
        public async Task Skip(CommandContext ctx)
        {
            GuildMusicStatuses[ctx.Guild.Id].Skip = true;
        }

        [Command("queueplaylist"), Aliases("qp"), Description("Enqueues the requested playlist. Note that at the moment this only retrieves the first 50 videos.")]
        public async Task QueuePlaylist(CommandContext ctx, string playlistUrl, [Description("Shuffle the playlist when adding to queue. \"true\" to enable.")] bool shuffle = false)
        {
            if (ctx.Client.GetVoiceNext()?.GetConnection(ctx.Guild)?.Channel != null && ctx.Client.GetVoiceNext().GetConnection(ctx.Guild).Channel.Users.All(e => e.Id != ctx.User.Id))
            {
                throw new OutputException("You must join a voice channel to queue songs.");
            }

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

            int resultQueueCount = GuildQueues.ContainsKey(ctx.Guild.Id) ? GuildQueues[ctx.Guild.Id].Count + playlistItemsListResponse.Items.Count : playlistItemsListResponse.Items.Count;

            await ctx.RespondAsync($"Queuing {playlistItemsListResponse.Items.Count} songs, please be patient if this is the first items to be added to queue. " +
                                    "(If you try to play music and nothing happens most likely the first song is still pending)");

            if (!GuildMusicStatuses.TryGetValue(ctx.Guild.Id, out MusicStatus musicStatus))
            {
                GuildMusicStatuses.Add(ctx.Guild.Id, new MusicStatus
                {
                    Skip = false
                });
            }

            while (GuildMusicStatuses[ctx.Guild.Id].Queuing) {}

            GuildMusicStatuses[ctx.Guild.Id].Queuing = true;

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

                if (!GuildQueues.ContainsKey(ctx.Guild.Id))
                {
                    GuildQueues.Add(ctx.Guild.Id, new List<JigglySong>());
                }

                Video parsedVideo = videoListResponse.Items.First();

                if (!string.IsNullOrWhiteSpace(parsedVideo.ContentDetails?.Duration) && XmlConvert.ToTimeSpan(parsedVideo.ContentDetails.Duration).Minutes > 15)
                {
                    await ctx.RespondAsync("This video is too long, please try to find something shorter than 15 minutes.");
                }

                Guid guid = Guid.NewGuid();

                AddSong(guid, parsedVideo.Snippet.Title, parsedVideo.Id, parsedVideo.Snippet.ChannelTitle, JigglySong.SongType.Youtube, ctx.Member);

                if (!DownloadHelper.GuildMusicTasks.ContainsKey(ctx.Guild.Id))
                {
                    DownloadHelper.GuildMusicTasks.Add(ctx.Guild.Id, new List<(Guid guid, Func<string> downloadTask)>());
                }

                DownloadHelper.GuildMusicTasks[ctx.Guild.Id].Add((guid, () => DownloadHelper.DownloadFromYouTube(ctx, $"https://www.youtube.com/watch?v={id}")));

                if (!DownloadHelper.IsDownloadLoopRunning)
                {
                    new Thread(() => DownloadHelper.DownloadQueue(ctx.Guild.Id)).Start();
                }
            }

            GuildMusicStatuses[ctx.Guild.Id].Queuing = false;

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
                if (!GuildQueues.TryGetValue(ctx.Guild.Id, out List<JigglySong> resultSongs))
                {
                    await ctx.RespondAsync("There is nothing currently queued.");
                    return;
                }

                for (int i = 0; i < resultSongs.Count; i += 5)
                {

                    DiscordEmbedBuilder queueBuilder = new DiscordEmbedBuilder
                    {
                        Title = $"Current Queue {i + 1}-{i + 5}",
                        Color = DiscordColor.Aquamarine
                    };

                    string tracks = string.Empty;

                    for (int j = i; j < i + 5; j++)
                    {
                        if (j >= resultSongs.Count)
                        {
                            break;
                        }

                        JigglySong resultSong = resultSongs[j];

                        tracks += $"{j + 1}: [**{resultSong.Title}** by **{resultSong.Artist}**](https://www.youtube.com/watch?v={resultSong.Id})\n";
                    }

                    queueBuilder.Description = tracks;

                    await ctx.RespondAsync(string.Empty, false, queueBuilder.Build());
                }

                return;
            }

            if (ctx.Client.GetVoiceNext()?.GetConnection(ctx.Guild)?.Channel != null && ctx.Client.GetVoiceNext().GetConnection(ctx.Guild).Channel.Users.All(e => e.Id != ctx.User.Id))
            {
                throw new OutputException("You must join a voice channel to queue songs.");
            }

            YouTubeService youtubeService = new YouTubeService(new BaseClientService.Initializer
            {
                ApiKey = Globals.BotSettings.YoutubeApiKey,
                ApplicationName = this.GetType().ToString()
            });

            try
            {
                Uri uri = new Uri(queryString);

                if (uri.Host != "youtu.be" && !uri.Host.ToLower().Contains("youtube"))
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

                if (!GuildQueues.ContainsKey(ctx.Guild.Id))
                {
                    GuildQueues.Add(ctx.Guild.Id, new List<JigglySong>());
                }

                Video parsedVideo = videoListResponse.Items.First();

                if (!string.IsNullOrWhiteSpace(parsedVideo.ContentDetails?.Duration) && XmlConvert.ToTimeSpan(parsedVideo.ContentDetails.Duration).Minutes > 15)
                {
                    await ctx.RespondAsync("This video is too long, please try to find something shorter than 15 minutes.");
                }

                Guid guid = Guid.NewGuid();

                AddSong(guid, parsedVideo.Snippet.Title, parsedVideo.Id, parsedVideo.Snippet.ChannelTitle, JigglySong.SongType.Youtube, ctx.Member);
                
                if (!DownloadHelper.GuildMusicTasks.ContainsKey(ctx.Guild.Id))
                {
                    DownloadHelper.GuildMusicTasks.Add(ctx.Guild.Id, new List<(Guid guid, Func<string> downloadTask)>());
                }

                DownloadHelper.GuildMusicTasks[ctx.Guild.Id].Add((guid, () => DownloadHelper.DownloadFromYouTube(ctx, queryString)));

                if (!DownloadHelper.IsDownloadLoopRunning)
                {
                    new Thread(() => DownloadHelper.DownloadQueue(ctx.Guild.Id)).Start();
                }

                DiscordEmbedBuilder confirmationBuilder = new DiscordEmbedBuilder
                {
                    Description = $"**✅ Successfully added [{parsedVideo.Snippet.Title}](https://www.youtube.com/watch?v={parsedVideo.Id}) to queue position {GuildQueues[ctx.Guild.Id].Count}**"
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
                    await ctx.RespondAsync($"🖍*Jigglypuff wrote on {ctx.User.Mention}'s face!*🖍\nMaybe they should have picked a song...");
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

                    if (!GuildQueues.ContainsKey(ctx.Guild.Id))
                    {
                        GuildQueues.Add(ctx.Guild.Id, new List<JigglySong>());
                    }

                    JigglySong selectedSong = videos[result];

                    DiscordEmbedBuilder confirmationBuilder = new DiscordEmbedBuilder
                    {
                        Description = $"**✅ Successfully added [{videos[result].Title}](https://www.youtube.com/watch?v={videos[result].Id}) to queue position {GuildQueues[ctx.Guild.Id].Count + 1}**"
                    };

                    if (GuildQueues[ctx.Guild.Id].Count == 0)
                    {
                        confirmationBuilder.Description += "\nPlease be patient; it takes a bit for the first song to cache.";
                    }

                    await ctx.RespondAsync(string.Empty, false, confirmationBuilder.Build());

                    if (!GuildMusicStatuses.TryGetValue(ctx.Guild.Id, out MusicStatus musicStatus))
                    {
                        GuildMusicStatuses.Add(ctx.Guild.Id, new MusicStatus
                        {
                            Skip = false
                        });
                    }

                    Guid guid = Guid.NewGuid();

                    AddSong(guid, selectedSong.Title, selectedSong.Id, selectedSong.Artist, selectedSong.Type, selectedSong.Queuer);

                    if (!DownloadHelper.GuildMusicTasks.ContainsKey(ctx.Guild.Id))
                    {
                        DownloadHelper.GuildMusicTasks.Add(ctx.Guild.Id, new List<(Guid guid, Func<string> downloadTask)>());
                    }

                    DownloadHelper.GuildMusicTasks[ctx.Guild.Id].Add((guid, () => DownloadHelper.DownloadFromYouTube(ctx, $"https://www.youtube.com/watch?v={videos[result].Id}")));

                    if (!DownloadHelper.IsDownloadLoopRunning)
                    {
                        new Thread(() => DownloadHelper.DownloadQueue(ctx.Guild.Id)).Start();
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

        private static void AddSong(Guid guid, string title, string id, string artist, JigglySong.SongType type, DiscordMember member)
        {
            GuildQueues[member.Guild.Id].Add(new JigglySong
            {
                Guid = guid,
                Title = title,
                Id = id,
                File = null,
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
            if (GuildMusicStatuses[ctx.Guild.Id].Playing)
            {
                return;
            }
            
            if (GuildQueues[ctx.Guild.Id].Count == 0)
            {
                throw new OutputException("No songs in queue! If you queued a song and this message shows, either it is still being locally queued or it silently failed to be retrieved.");
            }

            GuildMusicStatuses[ctx.Guild.Id].Playing = true;

            while (true)
            {
                VoiceNextExtension vnext = ctx.Client.GetVoiceNext();

                VoiceNextConnection vnc = vnext.GetConnection(ctx.Guild);

                if (vnc == null || !GuildQueues[ctx.Guild.Id].Any())
                {
                    break;
                }

                if (GuildQueues[ctx.Guild.Id].First().File == null)
                {
                    await ctx.RespondAsync("The next song is queuing, please wait...");
                    while (GuildQueues[ctx.Guild.Id].First().File == null)
                    {
                    }

                    if (GuildQueues[ctx.Guild.Id].First().File == "error")
                    {
                        await ctx.RespondAsync($"Failed to play **{GuildQueues[ctx.Guild.Id].First().Title}** by **{GuildQueues[ctx.Guild.Id].First().Artist}**, " +
                                               $"queued by {GuildQueues[ctx.Guild.Id].First().Queuer.Mention}");
                        GuildQueues[ctx.Guild.Id].RemoveAt(0);
                        await PlaySong(ctx);
                        return;
                    }
                }

                DiscordEmbedBuilder nowplayingBuilder = new DiscordEmbedBuilder
                {
                    Description = $"🎶 Now playing [{GuildQueues[ctx.Guild.Id].First().Title}](https://www.youtube.com/watch?v={GuildQueues[ctx.Guild.Id].First().Id}) 🎶\n\n" +
                                  $"[{GuildQueues[ctx.Guild.Id].First().Queuer.Mention}]{(GuildMusicStatuses[ctx.Guild.Id].Repeat == MusicStatus.RepeatType.None ? "" : " [🔁]")}"
                };

                GuildMusicStatuses[ctx.Guild.Id].Skip = false;

                await ctx.RespondAsync(null, false, nowplayingBuilder.Build());

                string songFile = GuildQueues[ctx.Guild.Id].First().File;

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
                    if (GuildMusicStatuses[ctx.Guild.Id].Skip)
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
                    if (GuildMusicStatuses[ctx.Guild.Id].Repeat == MusicStatus.RepeatType.None)
                    {
                        while (true)
                        {
                            try
                            {
                                File.Delete(songFile);
                                break;
                            }
                            catch
                            {
                                // Wait for processes to release file.
                            }
                        }
                    }
                }
                catch
                {
                    // Consume errors.
                }

                await vnc.SendSpeakingAsync(false);

                switch (GuildMusicStatuses[ctx.Guild.Id].Repeat)
                {
                    case MusicStatus.RepeatType.None:
                        GuildQueues[ctx.Guild.Id].RemoveAt(0);
                        break;
                    case MusicStatus.RepeatType.All:
                        JigglySong jigglySong = GuildQueues[ctx.Guild.Id][0];
                        GuildQueues[ctx.Guild.Id].Add(jigglySong);
                        GuildQueues[ctx.Guild.Id].RemoveAt(0);
                        break;
                    case MusicStatus.RepeatType.One:
                        // The Song is still number one in queue ;D
                        break;
                    default:
                        GuildQueues[ctx.Guild.Id].RemoveAt(0);
                        break;
                }

                GuildMusicStatuses[ctx.Guild.Id].Skip = false;
            }

            ctx.Client.GetVoiceNext().GetConnection(ctx.Guild)?.Disconnect();

            Directory.Delete(Path.Combine(Globals.AppPath, "Queue", ctx.Guild.Id.ToString()), true);

            GuildMusicStatuses[ctx.Guild.Id].Playing = false;
        }
    }
}
