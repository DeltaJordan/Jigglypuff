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

        public SongType Type { get; set; }
        public DiscordMember Queuer { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Id { get; set; }
    }
}
