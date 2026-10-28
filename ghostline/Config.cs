using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ghostline
{
    // A physical gateway. There can be several gateways of the same type (for example
    // two GoIP boxes on different IPs), each with its own id, address and credentials.
    public class Gateway
    {
        public string id { get; set; }
        public string type { get; set; }   // "quectel" / "dongle" / "yeastar" / "goip"
        public string ip { get; set; }
        public int amiPort { get; set; }   // quectel, dongle, yeastar
        public int httpPort { get; set; }  // yeastar, goip
        public string user { get; set; }
        public string password { get; set; }
        // quectel / dongle: poll the lines' registration, operator, network type and signal (Status tab,
        // /status, alerts). The AMI user needs "reporting" in "write".
        public bool monitor { get; set; } = true;
    }

    public class Channel
    {
        // Id of the gateway (Gateway.id) this SIM line belongs to.
        public string gateway { get; set; }
        public string name { get; set; }
        public string pattern { get; set; }
        // For yeastar/goip: the line/port number as a string.
        // For quectel / dongle: the device name from quectel.conf / dongle.conf ("gsm1", "hw1", ...).
        public string line { get; set; }
        // Own number of this SIM (+7...), shown on the Status tab and in the "From / To" header of
        // transcripts. A number the SIM itself reports (quectel / dongle, AT+CNUM) wins; many SIMs
        // report none, then this one is used; neither: the line name.
        public string number { get; set; }
        // Where this line goes; no section: the common clients.telegram.chat_ids / clients.ntfy.topic.
        public LineClients clients { get; set; }
        // What to send for calls on this line (mode "full" only).
        // No section: everything on, the line's chats.
        public ChannelCalls calls { get; set; }
        // The SIP phone of this line (pbx.mode: managed only): the SIM rings it, and it calls out
        // through the SIM.
        public LinePhone phone { get; set; }
    }

    public class LinePhone
    {
        public string extension { get; set; }               // "11"; also the SIP user name
        public string secret { get; set; }                  // generated on the PBX tab
        public string dial { get; set; } = "ru";            // ru | ru+intl: which numbers it may call
        public string encryption { get; set; } = "sdes";    // sdes (SRTP) | no
    }

    // pbx.mode "freepbx" (the default): FreePBX owns the PBX config, ghostline only connects to it.
    // "managed": ghostline writes the PJSIP and dialplan files of a plain Asterisk (the PBX tab).
    public class PbxSettings
    {
        public string mode { get; set; } = "freepbx";
        public string asterisk { get; set; }                // gateway id whose AMI reloads the config
        // The shared directory for the generated files: as ghostline sees it, and as Asterisk sees it
        // (its pjsip.conf / extensions.conf #include from there).
        public string confDir { get; set; }
        public string asteriskDir { get; set; } = "/etc/asterisk/ghostline";
        public PbxTls tls { get; set; } = new();
        public int udpPort { get; set; }                    // 0: no plain-UDP transport
        public string externalAddress { get; set; }         // public name or IP for NAT
        public List<string> localNets { get; set; } = new();
        public string recordingsPath { get; set; } = "/var/spool/asterisk/monitor";   // on the Asterisk side
        // Modems ghostline writes into quectel_ghostline.conf / dongle_ghostline.conf (PBX tab).
        public List<PbxModem> modems { get; set; } = new();

        [YamlIgnore]
        public bool Managed => string.Equals(mode, "managed", StringComparison.OrdinalIgnoreCase);
    }

    // A modem of the managed Asterisk. Ports by path only (never discovery by IMEI/IMSI, which sends AT
    // commands to every port of the host).
    public class PbxModem
    {
        public string name { get; set; }        // device name: Quectel/<name>, Dongle/<name>; channels[].line
        public string driver { get; set; }      // quectel | dongle
        public string data { get; set; }        // AT port, /dev/serial/by-path/...
        public string audio { get; set; }       // serial audio port; empty with a UAC sound card
        public string alsadev { get; set; }     // UAC sound card (quectel): hw:CARD=<id>,DEV=0
        public bool noqpcmv { get; set; }       // quectel on the ASR platform (EC200A)
    }

    public class PbxTls
    {
        // Certificate and key: relative paths are inside conf_dir (then ghostline can read the
        // certificate and warn before it expires), absolute ones are on the Asterisk side.
        public string cert { get; set; }
        public string key { get; set; }
        public int port { get; set; } = 5061;
    }

    public class LineClients
    {
        public LineTelegram telegram { get; set; }
        public LineNtfy ntfy { get; set; }
    }

    public class LineTelegram
    {
        public List<string> chatIds { get; set; }     // chats of the line (SMS and calls); empty: telegram.chat_ids
    }

    public class LineNtfy
    {
        public string topic { get; set; }             // topic of the line; empty: ntfy.topic
    }

    public class ChannelCalls
    {
        public bool voice { get; set; } = true;       // voice message with the call recording
        public bool transcript { get; set; } = true;  // transcript, added to the caption later
        public bool missed { get; set; } = true;      // notification about a missed incoming call
        public List<string> chatIds { get; set; }     // Telegram chats for calls only; empty: the line's chats
    }

    public class CallsSettings
    {
        // CDR source, one of the two: the FreePBX MySQL database, or a SQLite file that Asterisk writes
        // with cdr_sqlite3_custom (a directory shared with the PBX). cdr_sqlite wins when both are set.
        public CdrDbSettings cdrDb { get; set; }
        public string cdrSqlite { get; set; }
        // Recordings, one of the two: the PBX recordings directory over HTTP (Apache alias to
        // /var/spool/asterisk/monitor), or that directory itself, shared with the PBX. recordings_dir wins.
        public string recordingsUrl { get; set; }
        public string recordingsDir { get; set; }

        [YamlIgnore]
        internal bool CdrConfigured => !string.IsNullOrEmpty(cdrSqlite) || cdrDb != null;
        [YamlIgnore]
        internal bool RecordingsConfigured => !string.IsNullOrEmpty(recordingsDir) || !string.IsNullOrEmpty(recordingsUrl);
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

    // Settings sections, as in frte2tg: clients, web, logger, ...
    public class Settings
    {
        // "sms": SMS only, as before; "full": also calls (journal, recordings,
        // transcription, notifications).
        public string mode { get; set; } = "sms";
        public LocaleSettings locale { get; set; } = new();
        // Notification channels, both optional: Telegram and ntfy.
        public ClientsSettings clients { get; set; } = new();
        public WebSettings web { get; set; } = new();
        public LoggerSettings logger { get; set; } = new();
        public List<Gateway> gateways { get; set; }
        public List<Channel> channels { get; set; }
        public CallsSettings calls { get; set; }
        public GoipSettings goip { get; set; } = new();
        public MonitorSettings monitor { get; set; } = new();
        public PbxSettings pbx { get; set; } = new();

        // Before 2026-10-25 "telegram" was a top-level section; such configs are still read. It is
        // moved into clients after parsing (SettingsFile.Parse): a "clients" section further down
        // the file would replace it if it were moved here.
        [YamlMember(Alias = "telegram")]
        public TelegramSettings legacyTelegram
        {
            get => null;
            set => LegacyTelegramValue = value;
        }

        [YamlIgnore]
        internal TelegramSettings LegacyTelegramValue { get; private set; }

        [YamlIgnore]
        public bool FullMode => string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase);
    }

    // A client is on when its section is there (and has a token / url); no section: off.
    public class ClientsSettings
    {
        public TelegramSettings telegram { get; set; }
        public NtfySettings ntfy { get; set; }
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

    // ntfy (self-hosted push, https://ntfy.sh): SMS and calls to the topic of the line (channels[].ntfy.topic,
    // or this common topic), and back: a message written in the topic in the ntfy app is sent as an SMS.
    // Empty url: off.
    public class NtfySettings
    {
        public string url { get; set; }
        public string token { get; set; }
        public string topic { get; set; }
    }

    // Line monitoring (gateways[].monitor): a message to the line's chats and topic when the line has
    // had no network for alert_minutes, and when it is back. 0: no messages, only the Status tab.
    public class MonitorSettings
    {
        public int alertMinutes { get; set; } = 2;
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
            clients = new ClientsSettings { telegram = new TelegramSettings { token = tgToken, chatIds = tgChatIds ?? new() } },
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

        public static Settings Parse(string yaml)
        {
            var s = Deserializer.Deserialize<Settings>(yaml) ?? throw new InvalidDataException("empty config");
            s.clients ??= new ClientsSettings();
            if (s.LegacyTelegramValue != null)
                s.clients.telegram ??= s.LegacyTelegramValue;
            return s;
        }

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
