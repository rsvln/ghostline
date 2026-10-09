using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ghostline
{
    // A physical gateway. There can be several gateways of the same type (for example
    // two GoIP boxes on different IPs), each with its own id, address and credentials.
    public class Gateway
    {
        public string id { get; set; }
        public string type { get; set; }   // "yeastar" / "goip" / "quectel"
        public string ip { get; set; }
        public int amiPort { get; set; }   // yeastar, quectel
        public int httpPort { get; set; }  // yeastar, goip
        public string user { get; set; }
        public string password { get; set; }
    }

    public class Channel
    {
        // Id of the gateway (Gateway.id) this SIM line belongs to.
        public string gateway { get; set; }
        public string name { get; set; }
        public string pattern { get; set; }
        // For yeastar/goip: the line/port number as a string.
        // For quectel: the device name from quectel.conf ("gsm1", "gsm2", ...).
        public string line { get; set; }
        // Own number of this SIM (+7...), shown in the "From / To" header of transcripts for
        // external readers; when empty, the line name is used.
        public string number { get; set; }
        // What to send to Telegram for calls on this line (mode "full" only).
        // No section: everything on, common chats.
        public ChannelCalls calls { get; set; }
    }

    public class ChannelCalls
    {
        public bool voice { get; set; } = true;       // voice message with the call recording
        public bool transcript { get; set; } = true;  // transcript, added to the caption later
        public bool missed { get; set; } = true;      // notification about a missed incoming call
        public List<string> chatIds { get; set; }     // empty: the common telegram.chat_ids
    }

    public class CallsSettings
    {
        public CdrDbSettings cdrDb { get; set; }
        // PBX recordings directory over HTTP (Apache alias to /var/spool/asterisk/monitor).
        public string recordingsUrl { get; set; }
        public int pollSeconds { get; set; } = 15;
        public TranscribeSettings transcribe { get; set; }
    }

    public class CdrDbSettings
    {
        public string host { get; set; }
        public int port { get; set; } = 3306;
        public string database { get; set; } = "asteriskcdrdb";
        public string user { get; set; }
        public string password { get; set; }
    }

    public class TranscribeSettings
    {
        public bool enabled { get; set; }
        // OpenAI-compatible /v1/audio/transcriptions (speaches / faster-whisper).
        public string url { get; set; }
        public string model { get; set; }
        public string language { get; set; } = "ru";
        // Whisper prompt (initial_prompt): names and terms that occur in the calls;
        // proper names and terms produce most errors without it. The contact name of the
        // other side is added automatically.
        public string prompt { get; set; }
    }

    // Settings sections, as in frte2tg: telegram, web, logger, ...
    public class Settings
    {
        // "sms": SMS only, as before; "full": also calls (journal, recordings,
        // transcription, Telegram).
        public string mode { get; set; } = "sms";
        public LocaleSettings locale { get; set; } = new();
        public TelegramSettings telegram { get; set; } = new();
        public WebSettings web { get; set; } = new();
        public LoggerSettings logger { get; set; } = new();
        public List<Gateway> gateways { get; set; }
        public List<Channel> channels { get; set; }
        public CallsSettings calls { get; set; }
        public GoipSettings goip { get; set; } = new();

        [YamlIgnore]
        public bool FullMode => string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase);
    }

    // Languages: files locales/<language>.json next to the app (en, ru). As in frte2tg.
    public class LocaleSettings
    {
        public string @default { get; set; } = "en";
        public string web { get; set; }        // empty: default
        public string telegram { get; set; }   // empty: default
        public string transcript { get; set; } // downloaded transcripts (PDF, .txt); empty: default
    }

    public class TelegramSettings
    {
        public string token { get; set; }
        // Where SMS go (and calls, unless a line has its own chats). The bot accepts
        // commands to send SMS only from these chats.
        public List<string> chatIds { get; set; } = new();
    }

    // Web interface; empty user and password: no authentication.
    public class WebSettings
    {
        public int port { get; set; } = 8889;
        public string user { get; set; }
        public string password { get; set; }
    }

    public class LoggerSettings
    {
        // Also write SMS to /var/log/ghostline/ghostline_DATE.log.
        public bool file { get; set; }
    }

    public class GoipSettings
    {
        // SMTP receiver: GoIP gateways deliver incoming SMS by e-mail.
        public SmtpSettings smtp { get; set; } = new();
    }

    public class SmtpSettings
    {
        public bool enabled { get; set; }
        public int port { get; set; } = 25;
    }

    // Format of ghostline.json (yetgsms, before 2026-09-27): everything at the root, camelCase.
    // Only needed for the migration.
    internal class LegacyJsonSettings
    {
        public string mode { get; set; } = "sms";
        public CallsSettings calls { get; set; }
        public string tgToken { get; set; }
        public bool smtpForGoip { get; set; }
        public bool LogToFile { get; set; }
        public int webUiPort { get; set; } = 8889;
        public string webUiUser { get; set; }
        public string webUiPassword { get; set; }
        public List<Gateway> gateways { get; set; }
        public List<Channel> channels { get; set; }
        public List<string> tgChatIds { get; set; }

        public Settings ToSettings() => new()
        {
            mode = mode,
            telegram = new TelegramSettings { token = tgToken, chatIds = tgChatIds ?? new() },
            web = new WebSettings { port = webUiPort, user = webUiUser, password = webUiPassword },
            logger = new LoggerSettings { file = LogToFile },
            gateways = gateways,
            channels = channels,
            calls = calls,
            goip = new GoipSettings { smtp = new SmtpSettings { enabled = smtpForGoip } }
        };
    }

    // Settings file: YAML with sections (telegram, web, logger, calls...), keys in snake_case.
    // An unknown key is an error (a typo must not silently disable an option).
    // Before 2026-09-27 the settings were JSON (ghostline.json); on the first start
    // they are converted to YAML and the old file is renamed to .json.migrated.
    internal static class SettingsFile
    {
        private static IDeserializer Deserializer => new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        public static Settings Parse(string yaml) =>
            Deserializer.Deserialize<Settings>(yaml) ?? throw new InvalidDataException("empty config");

        public static string Serialize(Settings s) => new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build()
            .Serialize(s);

        // YAML error to "line N, column M: what is wrong", for the web editor and the log.
        public static string Describe(Exception ex)
        {
            if (ex is not YamlDotNet.Core.YamlException yex) return ex.Message;
            var inner = yex;
            while (inner.InnerException is YamlDotNet.Core.YamlException next)
                inner = next;
            string message = inner.InnerException?.Message ?? inner.Message;
            if (message.StartsWith("No node deserializer"))
                message = "unexpected value (wrong type or indentation)";
            return $"line {inner.Start.Line}, column {inner.Start.Column}: {message}";
        }

        // No ghostline.yaml but a ghostline.json next to it: convert.
        public static void MigrateFromJson(string yamlPath)
        {
            string jsonPath = Path.ChangeExtension(yamlPath, ".json");
            if (File.Exists(yamlPath) || !File.Exists(jsonPath)) return;

            var s = Newtonsoft.Json.JsonConvert.DeserializeObject<LegacyJsonSettings>(File.ReadAllText(jsonPath)).ToSettings();
            File.WriteAllText(yamlPath, "# ghostline settings. Converted from " + Path.GetFileName(jsonPath) +
                                        " " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\n" + Serialize(s));
            File.Move(jsonPath, jsonPath + ".migrated");
            Console.WriteLine($"Config migrated: {jsonPath} -> {yamlPath}");
        }
    }

    internal partial class Program
    {
        // gatewayId == null: search all channels (needed for SMTPReadData/goip,
        // where the pattern is already unique by the device serial number).
        // gatewayId set: search only the channels of that gateway (needed for
        // AMI events: two identical-looking events from different physical boxes
        // must not match a channel of the other gateway).
        private static Channel getChannel(string str, string gatewayId = null)
        {
            foreach (Channel ch in settings.channels)
            {
                if (gatewayId != null && ch.gateway != gatewayId) continue;
                if (str.ToUpper().StartsWith(ch.pattern.ToUpper()))
                {
                    return ch;
                }
            }
            return null;
        }

        private static Channel getChannelByLine(string gatewayId, string line)
        {
            foreach (Channel ch in settings.channels)
            {
                if (ch.gateway == gatewayId && ch.line == line)
                    return ch;
            }
            return null;
        }

        internal static Gateway getGatewayById(string id) =>
            settings.gateways?.FirstOrDefault(g => g.id == id);

        internal static Channel getChannelType(string str)
        {
            foreach (Channel ch in settings.channels)
            {
                if (str.ToUpper() == ch.name.ToUpper())
                    return ch;
            }
            return null;
        }

    }
}
