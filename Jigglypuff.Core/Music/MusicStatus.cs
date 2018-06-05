namespace Jigglypuff.Core.Music
{
    public class MusicStatus
    {
        public enum RepeatType
        {
            None,
            One,
            All
        }

        public bool Playing { get; set; }
        public bool Skip { get; set; }
        public RepeatType Repeat { get; set; }
        public bool Queuing { get; set; }
        public int QueueCount { get; set; }
    }
}
