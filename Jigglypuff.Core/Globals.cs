using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Jigglypuff.Core
{
    public static class Globals
    {
        public static Settings BotSettings { get; set; }
        public static readonly string AppPath = Directory.GetParent(new Uri(Assembly.GetEntryAssembly().CodeBase).LocalPath).FullName;
        public static Random Random => local ?? (local = new Random(unchecked(Environment.TickCount * 31 + Thread.CurrentThread.ManagedThreadId)));

        [ThreadStatic] private static Random local;
    }
}
