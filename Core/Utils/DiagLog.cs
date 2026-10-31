using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace KeyTrigger.Core.Utils
{
    // Opt-in diagnostic log, enabled by starting KeyTrigger with --diag.
    // Records keyboard hook health, focus changes and keys that slipped past the hook, so a user whose game
    // misbehaves can send proof of what happened. It only ever names keys KeyTrigger maps.
    public static class DiagLog
    {
        private static BlockingCollection<string>? _queue;

        public static bool Enabled => _queue != null;

        public static void Start(string path)
        {
            if (_queue != null) return;

            try
            {
                var writer = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
                var queue = new BlockingCollection<string>();
                var thread = new Thread(() =>
                {
                    foreach (var line in queue.GetConsumingEnumerable())
                    {
                        try { writer.WriteLine(line); } catch { }
                    }
                })
                {
                    IsBackground = true,
                    Name = "KeyTrigger_DiagLog"
                };
                thread.Start();
                _queue = queue;
                Write($"Key Trigger diagnostic log started ({Environment.OSVersion.VersionString})");
            }
            catch
            {
                // Diagnostics must never stop the app from starting
                _queue = null;
            }
        }

        public static void Write(string message)
        {
            _queue?.TryAdd($"{DateTime.Now:HH:mm:ss.fff}  {message}");
        }
    }
}
