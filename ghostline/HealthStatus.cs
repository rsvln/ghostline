using System.Collections.Concurrent;

namespace ghostline
{
    public class ChannelHealth
    {
        public string Kind { get; set; }
        public bool Configured { get; set; }
        public bool Connected { get; set; }
        public DateTime? LastActivityAt { get; set; }
        public DateTime? LastErrorAt { get; set; }
        public string LastError { get; set; }
        // Up, but a part of it is not (one bot of several): LastError says which.
        public bool Partial { get; set; }
    }

    // Current state of gateways and Telegram, in memory only, not a history.
    // Key: the gateway type ("yeastar" / "goip" / "quectel" / "telegram"), not the SIM channel name:
    // there is one physical connection per gateway, not per channel.name in the config.
    public static class HealthStatus
    {
        private static readonly ConcurrentDictionary<string, ChannelHealth> _health = new();

        private static ChannelHealth Get(string kind) =>
            _health.GetOrAdd(kind, k => new ChannelHealth { Kind = k });

        public static void MarkConfigured(string kind)
        {
            Get(kind).Configured = true;
        }

        public static void MarkConnected(string kind)
        {
            var h = Get(kind);
            h.Configured = true;
            h.Connected = true;
            h.Partial = false;
            h.LastActivityAt = DateTime.Now;
            h.LastError = null;
            h.LastErrorAt = null;
        }

        // Up, but a part of it fails: "warn" (yellow) until the next MarkConnected or MarkDisconnected.
        public static void MarkPartial(string kind, string error)
        {
            var h = Get(kind);
            h.Configured = true;
            h.Connected = true;
            h.Partial = true;
            if (h.LastError != error) h.LastErrorAt = DateTime.Now;
            h.LastError = error;
        }

        // A health check passed: the service is up, but nothing was done, so the last activity stays.
        public static void MarkReachable(string kind)
        {
            var h = Get(kind);
            h.Configured = true;
            h.Connected = true;
            h.Partial = false;
            h.LastError = null;
            h.LastErrorAt = null;
        }

        public static void MarkDisconnected(string kind)
        {
            var h = Get(kind);
            h.Connected = false;
            h.Partial = false;
        }

        public static void MarkActivity(string kind)
        {
            Get(kind).LastActivityAt = DateTime.Now;
        }

        public static void MarkError(string kind, string error)
        {
            var h = Get(kind);
            h.LastError = error;
            h.LastErrorAt = DateTime.Now;
        }

        public static Dictionary<string, ChannelHealth> Snapshot() =>
            new Dictionary<string, ChannelHealth>(_health);

        public static ChannelHealth Find(string kind) => _health.TryGetValue(kind, out var h) ? h : null;

        // "ok", "warn" (up, but a part of it is not), "err" or "unknown". Components with a permanent connection (AMI gateways, Telegram,
        // the CDR database, the transcription service) go by that connection, so an old transient
        // error does not paint a working component red. Stateless ones (goip over HTTP/SMTP) go by
        // whichever is newer: the last activity or the last error.
        public static string State(string kind, bool connectionBased)
        {
            var h = Find(kind);
            if (h == null) return "unknown";
            if (connectionBased)
                return h.Connected ? (h.Partial ? "warn" : "ok") : h.LastErrorAt != null ? "err" : "unknown";
            if (h.LastErrorAt != null && (h.LastActivityAt == null || h.LastErrorAt > h.LastActivityAt)) return "err";
            return h.LastActivityAt != null ? "ok" : "unknown";
        }
    }
}
