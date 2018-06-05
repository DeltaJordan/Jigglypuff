using System;
using DSharpPlus.Entities;

namespace Jigglypuff.Core.Music
{
    public class JigglySong
    {
        public enum SongType
        {
            Soundcloud,
            Youtube
        }

        public Guid Guid { get; set; }
        public string File { get; set; }
        public SongType Type { get; set; }
        public DiscordMember Queuer { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Id { get; set; }
    }
}
