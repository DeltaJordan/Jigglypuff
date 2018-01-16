using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.CommandsNext;
using DSharpPlus.CommandsNext.Attributes;
using DSharpPlus.Entities;
using DSharpPlus.Interactivity;
using DSharpPlus.VoiceNext;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Jigglypuff.Net.Exceptions;
using Jigglypuff.Net.Music;
using NAudio.Wave;
using YoutubeExplode;
using YoutubeExplode.Models.MediaStreams;

namespace Jigglypuff.Net.Commands
{
    [Group("music"), Aliases("m")]
    public class MusicCommands
    {
        private static Dictionary<ulong, List<JigglySong>> guildQueues = new Dictionary<ulong, List<JigglySong>>();

        private static Dictionary<ulong, MusicStatus> guildMusicStatuses = new Dictionary<ulong, MusicStatus>();

        [Command("join"), Aliases("j")]
        public async Task Join(CommandContext ctx, string voiceChannel = null)
        {
            VoiceNextClient vnext = ctx.Client.GetVoiceNextClient();

            VoiceNextConnection vnc = vnext.GetConnection(ctx.Guild);
            if (vnc != null)
            {
                throw new OutputException("Already connected in this guild.");
            }

            DiscordChannel chn = ctx.Member?.VoiceState?.Channel;
            if (chn == null && voiceChannel == null)
            {
                throw new OutputException("You need to be in a voice channel. Alternatively, you can enter the name of a channel for the bot to join.");
            }

            if (voiceChannel != null)
            {
                chn = (await ctx.Guild.GetChannelsAsync()).FirstOrDefault(e => e.Type == ChannelType.Voice && e.Name.ToLower().Contains(voiceChannel.ToLower()));

                if (chn == null)
                {
                    throw new OutputException($"Cannot find the channel \"{voiceChannel}\".");
                }
            }

            vnc = await vnext.ConnectAsync(chn);

            await ctx.RespondAsync($"Connected to channel {vnc.Channel.Name} successfully.");
        }

        [Command("leave"), Aliases("l"), Description("Causes Jigglypuff to leave the currently joined voice channel.")]
        public async Task Leave(CommandContext ctx)
        {
            VoiceNextClient vnext = ctx.Client.GetVoiceNextClient();

            VoiceNextConnection vnc = vnext.GetConnection(ctx.Guild);
            if (vnc == null)
            {
                throw new OutputException("Not connected in this guild.");
            }

            vnc.Disconnect();
            await ctx.RespondAsync("Left connected channel.");
        }

        [Command("play"), Aliases("p"), Description("Begins playing enqueued songs and is also used as an alias for pausing if already playing.")]
        public async Task Play(CommandContext ctx, [RemainingText] string queryString)
        {
            if (!string.IsNullOrWhiteSpace(queryString))
            {
                await this.Queue(ctx, queryString);
                return;
            }

            if (!guildMusicStatuses.TryGetValue(ctx.Guild.Id, out MusicStatus _))
            {
                guildMusicStatuses.Add(ctx.Guild.Id, new MusicStatus {Skip = false});
            }

            this.PlayMusic(ctx);
        }

        [Command("skip"), Aliases("s"), Description("Skips the currently playing song.")]
        public async Task Skip(CommandContext ctx)
        {
            guildMusicStatuses[ctx.Guild.Id].Skip = true;
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

                    tracks += $"{i + 1}: **{resultSong.Title}** by **{resultSong.Artist}**";
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

            if (queryString.ToLower().Contains("youtu.be/"))
            {
                Match urlMatch = Regex.Match(queryString, @"youtu\.be/");

                string id = queryString.Substring(urlMatch.Index + urlMatch.Length, 11).Replace("/", string.Empty);

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

                guildQueues[ctx.Guild.Id].Add(new JigglySong
                {
                    Id = parsedVideo.Id,
                    Title = parsedVideo.Snippet.Title,
                    Artist = parsedVideo.Snippet.ChannelTitle,
                    Type = JigglySong.SongType.Youtube,
                    Queuer = ctx.Member
                });

                DiscordEmbedBuilder confirmationBuilder = new DiscordEmbedBuilder
                {
                    Description = $"**✅ Successfully added [{parsedVideo.Snippet.Title}](https://www.youtube.com/watch?v={parsedVideo.Id}) to queue position {guildQueues[ctx.Guild.Id].Count}**"
                };

                await ctx.RespondAsync(null, false, confirmationBuilder.Build());

                return;
            }

            if (queryString.ToLower().Contains("youtube.com/watch?v="))
            {
                Match urlMatch = Regex.Match(queryString, @"youtube\.com/watch\?v=");

                string id = queryString.Substring(urlMatch.Index + urlMatch.Length, 11).Replace("/", string.Empty);

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

                guildQueues[ctx.Guild.Id].Add(new JigglySong
                {
                    Id = parsedVideo.Id,
                    Title = parsedVideo.Snippet.Title,
                    Artist = parsedVideo.Snippet.ChannelTitle,
                    Type = JigglySong.SongType.Youtube,
                    Queuer = ctx.Member
                });

                DiscordEmbedBuilder confirmationBuilder = new DiscordEmbedBuilder
                {
                    Description = $"**✅ Successfully added [{parsedVideo.Snippet.Title}](https://www.youtube.com/watch?v={parsedVideo.Id}) to queue position {guildQueues[ctx.Guild.Id].Count}**"
                };

                await ctx.RespondAsync(null, false, confirmationBuilder.Build());

                return;
            }

            SearchResource.ListRequest searchListRequest = youtubeService.Search.List("snippet");
            searchListRequest.Q = queryString;
            searchListRequest.MaxResults = 5;
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
            for (int i = 0; i < searchListResponse.Items.Count; i++)
            {
                SearchResult searchResult = searchListResponse.Items[i];
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

            MessageContext msgContext = await ctx.Client.GetInteractivityModule().WaitForMessageAsync(e => e.Author.Id == ctx.Message.Author.Id && (e.Content.ToLower() == "c" || int.TryParse(e.Content, out result) && result > 0 && result <= 5), TimeSpan.FromSeconds(30));

            if (msgContext == null)
            {
                await ctx.RespondAsync($"🖋*Jigglypuff wrote on {ctx.User.Mention}'s face!*🖋\nMaybe they should have picked a song...");
                await resultsMessage.DeleteAsync();
                return;
            }

            result--;

            if (result > 0)
            {
                if (!guildQueues.ContainsKey(ctx.Guild.Id))
                {
                    guildQueues.Add(ctx.Guild.Id, new List<JigglySong>());
                }

                guildQueues[ctx.Guild.Id].Add(videos[result]);

                DiscordEmbedBuilder confirmationBuilder = new DiscordEmbedBuilder
                {
                    Description = $"**✅ Successfully added [{videos[result].Title}](https://www.youtube.com/watch?v={videos[result].Id}) to queue position {guildQueues[ctx.Guild.Id].Count}**"
                };

                await ctx.RespondAsync(string.Empty, false, confirmationBuilder.Build());

                if (!guildMusicStatuses.TryGetValue(ctx.Guild.Id, out MusicStatus musicStatus))
                {
                    guildMusicStatuses.Add(ctx.Guild.Id, new MusicStatus
                    {
                        Skip = false
                    });

                    if (ctx.Client.GetVoiceNextClient().GetConnection(ctx.Guild) != null)
                    {
                        this.PlayMusic(ctx);
                    }
                }
                else
                {
                    if (!musicStatus.Skip && ctx.Client.GetVoiceNextClient().GetConnection(ctx.Guild) != null)
                    {
                        this.PlayMusic(ctx);
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

        private async void PlayMusic(CommandContext ctx)
        {
            await this.PlaySong(ctx);
        }

        private async Task PlaySong(CommandContext ctx)
        {
            while (true)
            {
                VoiceNextClient vnext = ctx.Client.GetVoiceNextClient();

                VoiceNextConnection vnc = vnext.GetConnection(ctx.Guild);

                if (vnc == null || !guildQueues[ctx.Guild.Id].Any())
                {
                    break;
                }
                await vnc.SendSpeakingAsync(); // send a speaking indicator

                DiscordEmbedBuilder nowplayingBuilder = new DiscordEmbedBuilder
                {
                    Description = $"🎶[{guildQueues[ctx.Guild.Id].First().Title}](https://www.youtube.com/watch?v={guildQueues[ctx.Guild.Id].First().Id})🎶\n\n" +
                                  $"[{guildQueues[ctx.Guild.Id].First().Queuer.Mention}]"
                };

                await ctx.RespondAsync(null, false, nowplayingBuilder.Build());

                Process ffmpeg;

                if (guildQueues[ctx.Guild.Id].First().Type == JigglySong.SongType.Youtube)
                {
                    ffmpeg = CreateStream($"https://www.youtube.com/watch?v={guildQueues[ctx.Guild.Id].First().Id}");
                }
                else
                {
                    // TODO Soundcloud
                    continue;
                }

                Stream ffout = ffmpeg.StandardOutput.BaseStream;

                using (MemoryStream ms = new MemoryStream())
                {
                    await ffout.CopyToAsync(ms);
                    ms.Position = 0;

                    byte[] buff = new byte[3840]; // buffer to hold the PCM data
                    int br;
                    while ((br = ms.Read(buff, 0, buff.Length)) > 0)
                    {
                        if (guildMusicStatuses[ctx.Guild.Id].Skip)
                        {
                            break;
                        }

                        if (br < buff.Length) // it's possible we got less than expected, let's null the remaining part of the buffer
                            for (int i = br; i < buff.Length; i++)
                                buff[i] = 0;

                        await vnc.SendAsync(buff, 20); // we're sending 20ms of data
                    }
                }

                await vnc.SendSpeakingAsync(false);

                guildQueues[ctx.Guild.Id].RemoveAt(0);

                guildMusicStatuses[ctx.Guild.Id].Skip = false;
            }
        }

        private static Process CreateStream(string url)
        {
            Process currentsong = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/C youtube-dl.exe -o - {url} | ffmpeg -i pipe:0 -ac 2 -f s16le -ar 48000 pipe:1",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };


            currentsong.Start();
            return currentsong;
        }
    }
}
