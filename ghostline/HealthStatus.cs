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
    }

    // Оперативное состояние шлюзов/Telegram — только в памяти, не история.
    // Ключ — тип шлюза ("yeastar" / "goip" / "quectel" / "telegram"), не имя SIM-канала:
    // физическое соединение одно на шлюз, а не на каждый channel.name в конфиге.
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
            h.LastActivityAt = DateTime.Now;
            h.LastError = null;
            h.LastErrorAt = null;
        }

        public static void MarkDisconnected(string kind)
        {
            Get(kind).Connected = false;
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
    }
}
