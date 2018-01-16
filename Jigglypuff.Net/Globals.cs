using System;
using System.IO;
using System.Reflection;

namespace Jigglypuff.Net
{
    public static class Globals
    {
        public static Settings BotSettings { get; set; }
        public static readonly string AppPath = Directory.GetParent(new Uri(Assembly.GetEntryAssembly().CodeBase).LocalPath).FullName;
    }
}
