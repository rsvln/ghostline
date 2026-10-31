namespace ghostline
{
    public record LogEntry(DateTime ts, string level, string area, string message);

    // The system log: what ghostline itself does (not the SMS and calls, those are the journal in the DB).
    // Every entry goes to stdout with the systemd priority prefix, so journald keeps the level
    // (journalctl -p warning -u ghostline); to /var/log/ghostline/system-DATE.log; and into a ring buffer
    // for the System tab. Entries below logger.level are dropped everywhere.
    public static class SysLog
    {
        public static readonly string[] Levels = { "error", "warning", "info", "debug" };
        private static readonly string[] Priority = { "<3>", "<4>", "<6>", "<7>" };

        private const int BufferSize = 2000;
        private const int KeepDays = 14;
        private static readonly LinkedList<LogEntry> _buffer = new();
        private static readonly object _lock = new();
        private static int _max = 2;            // info
        private static string _dir = "/var/log/ghostline/";
        private static bool _fileOk = true;
        private static DateTime _cleaned;
        // systemd sets JOURNAL_STREAM for a service whose stdout goes to journald; elsewhere (Docker,
        // a terminal) the priority prefix would only clutter the output.
        private static readonly bool UnderJournald = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JOURNAL_STREAM"));

        // logger.level: error | warning | info | debug; anything else: info.
        public static void Configure(string level, string dir = null)
        {
            int i = Array.IndexOf(Levels, (level ?? "").Trim().ToLowerInvariant());
            _max = i >= 0 ? i : 2;
            if (dir != null) _dir = dir;
            _fileOk = true;
        }

        public static void Error(string area, string message) => Write(0, area, message);
        public static void Warn(string area, string message) => Write(1, area, message);
        public static void Info(string area, string message) => Write(2, area, message);
        public static void Debug(string area, string message) => Write(3, area, message);

        private static void Write(int level, string area, string message)
        {
            if (level > _max) return;
            // Milliseconds, as in the file, so the entries read back from it match the buffer exactly.
            var now = DateTime.Now;
            var e = new LogEntry(now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond)), Levels[level], area, message);
            Console.WriteLine($"{(UnderJournald ? Priority[level] : "")}{area}: {message}");
            lock (_lock)
            {
                _buffer.AddLast(e);
                if (_buffer.Count > BufferSize) _buffer.RemoveFirst();
                WriteFile(e);
            }
        }

        private static void WriteFile(LogEntry e)
        {
            if (!_fileOk) return;
            try
            {
                Directory.CreateDirectory(_dir);
                File.AppendAllText(Path.Combine(_dir, $"system-{e.ts:yyyy-MM-dd}.log"),
                    $"{e.ts:yyyy-MM-dd HH:mm:ss.fff}\t{e.level}\t{e.area}\t{e.message.Replace("\n", "\\n").Replace("\r", "")}\n");
                if (e.ts.Date != _cleaned)
                {
                    _cleaned = e.ts.Date;
                    foreach (var f in Directory.GetFiles(_dir, "system-*.log"))
                        if (File.GetLastWriteTime(f) < e.ts.AddDays(-KeepDays)) File.Delete(f);
                }
            }
            catch (Exception ex)
            {
                // No log directory (a container without the volume): the buffer and journald still work.
                _fileOk = false;
                Console.WriteLine($"{(UnderJournald ? "<4>" : "")}log: cannot write {_dir}: {ex.Message}");
            }
        }

        // Newest first: entries of this level and more severe, optionally of one area. After a restart
        // the buffer is short, so today's and yesterday's files fill it up.
        public static List<LogEntry> Read(string level, string area, int limit)
        {
            int max = Array.IndexOf(Levels, level ?? "");
            if (max < 0) max = 2;
            List<LogEntry> entries;
            lock (_lock) entries = _buffer.ToList();
            if (entries.Count < limit)
            {
                var first = entries.Count > 0 ? entries[0].ts : DateTime.MaxValue;
                var older = new List<LogEntry>();
                foreach (var day in new[] { DateTime.Today.AddDays(-1), DateTime.Today })
                    older.AddRange(ReadFile(Path.Combine(_dir, $"system-{day:yyyy-MM-dd}.log")).Where(x => x.ts < first));
                entries = older.Concat(entries).ToList();
            }
            return entries.Where(x => Array.IndexOf(Levels, x.level) <= max)
                          .Where(x => string.IsNullOrEmpty(area) || x.area == area)
                          .Reverse().Take(limit).ToList();
        }

        public static List<string> Areas()
        {
            lock (_lock) return _buffer.Select(x => x.area).Distinct().OrderBy(x => x).ToList();
        }

        private static IEnumerable<LogEntry> ReadFile(string path)
        {
            if (!File.Exists(path)) yield break;
            string[] lines;
            try { lines = File.ReadAllLines(path); } catch { yield break; }
            foreach (var line in lines)
            {
                var p = line.Split('\t', 4);
                if (p.Length == 4 && DateTime.TryParse(p[0], out var ts))
                    yield return new LogEntry(ts, p[1], p[2], p[3].Replace("\\n", "\n"));
            }
        }
    }
}
