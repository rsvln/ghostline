using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ghostline
{
    // A physical gateway. There can be several gateways of the same type (for example
    // two GoIP boxes on different IPs), each with its own id, address and credentials.
    public class Gateway
    {
        public string id { get; set; }
        public string type { get; set; }   // "asterisk" / "yeastar" / "goip"
        public string ip { get; set; }
        public int amiPort { get; set; }   // asterisk, yeastar
        public int httpPort { get; set; }  // yeastar, goip
        public string user { get; set; }
        public string password { get; set; }
        // asterisk: poll the modems' registration, operator, network type and signal (Status tab,
        // /status, alerts). The AMI user needs "reporting" in "write".
        public bool monitor { get; set; } = true;
        // asterisk: who owns the PBX config. "freepbx" (the default): FreePBX, ghostline only connects;
        // "managed": ghostline writes the PJSIP, dialplan and modem files of a plain Asterisk.
        public string pbx { get; set; }
        public ManagedPbx managed { get; set; }
        // asterisk: its GSM modems (chan_quectel / chan_dongle devices). Under FreePBX only the name and
        // the driver; on a managed Asterisk also the ports, written into the driver config.
        public List<PbxModem> modems { get; set; }

        [YamlIgnore]
        public bool IsAsterisk => type == "asterisk";
        [YamlIgnore]
        public bool Managed => IsAsterisk && string.Equals(pbx, "managed", StringComparison.OrdinalIgnoreCase);

        public PbxModem Modem(string name) => modems?.FirstOrDefault(m => m.name == name);
    }

    public class Channel
    {
        // Id of the gateway (Gateway.id) this SIM line belongs to.
        public string gateway { get; set; }
        public string name { get; set; }
        public string pattern { get; set; }
        // asterisk: the modem of the gateway (gateways[].modems[].name: "gsm1", "hw1").
        public string modem { get; set; }
        // yeastar / goip: the port number as a string.
        public string port { get; set; }
        // Own number of this SIM (+7...), shown on the Status tab and in the "From / To" header of
        // transcripts. A number the SIM itself reports (quectel / dongle, AT+CNUM) wins; many SIMs
        // report none, then this one is used; neither: the line name.
        public string number { get; set; }
        // Where this line goes; no section: the common clients.telegram.chat_ids / clients.ntfy.topic.
        public LineClients clients { get; set; }
        // What to send for calls on this line (mode "full" only).
        // No section: everything on, the line's chats.
        public ChannelCalls calls { get; set; }
        // The SIP phone of this line (a line on the managed gateway only): the SIM rings it, and it calls out
        // through the SIM.
        public LinePhone phone { get; set; }

        // Before 2026-10-30 the modem and the port were both "line"; such configs are still read
        // (SettingsFile.Normalize).
        [YamlMember(Alias = "line")]
        public string legacyLine
        {
            get => null;
            set => LegacyLine = value;
        }

        [YamlIgnore]
        internal string LegacyLine { get; set; }

        // The modem or the port: what the gateway calls this SIM.
        [YamlIgnore]
        public string Device => !string.IsNullOrEmpty(modem) ? modem : port;
    }

    public class LinePhone
    {
        public string extension { get; set; }               // "11"; also the SIP user name
        public string secret { get; set; }                  // generated on save, shown once
        public string dial { get; set; } = "ru";            // ru | ru+intl: which numbers it may call
        public string encryption { get; set; } = "sdes";    // sdes (SRTP) | no
    }

    // gateways[].managed: the plain Asterisk ghostline writes the config of.
    public class ManagedPbx
    {
        // The shared directory for the generated files: as ghostline sees it, and as Asterisk sees it
        // (its pjsip.conf / extensions.conf / quectel.conf / dongle.conf #include from there).
        public string confDir { get; set; }
        public string asteriskDir { get; set; } = "/etc/asterisk/ghostline";
        public string recordingsPath { get; set; } = "/var/spool/asterisk/monitor";   // on the Asterisk side
        public PbxSip sip { get; set; } = new();
    }

    // Where the phones connect.
    public class PbxSip
    {
        public PbxTls tls { get; set; } = new();
        public int udpPort { get; set; }                    // 0: no plain-UDP transport
        public string externalAddress { get; set; }         // public name or IP for NAT
        public List<string> localNets { get; set; } = new();
    }

    // The top-level "pbx" section before 2026-10-30; it moves into its gateway (SettingsFile.Normalize).
    public class LegacyPbxSettings
    {
        public string mode { get; set; } = "freepbx";
        public string asterisk { get; set; }
        public string confDir { get; set; }
        public string asteriskDir { get; set; } = "/etc/asterisk/ghostline";
        public PbxTls tls { get; set; } = new();
        public int udpPort { get; set; }
        public string externalAddress { get; set; }
        public List<string> localNets { get; set; } = new();
        public string recordingsPath { get; set; } = "/var/spool/asterisk/monitor";
        public List<PbxModem> modems { get; set; } = new();
    }

    // A modem of an Asterisk gateway. Ports by path only (never discovery by IMEI/IMSI, which sends AT
    // commands to every port of the host).
    public class PbxModem
    {
        public string name { get; set; }        // device name: Quectel/<name>, Dongle/<name>; lines[].modem
        public string driver { get; set; }      // quectel | dongle
        // Managed Asterisk only:
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
        public List<Channel> lines { get; set; }
        public CallsSettings calls { get; set; }
        public GoipSettings goip { get; set; } = new();
        public MonitorSettings monitor { get; set; } = new();

        // Before 2026-10-30: "channels" (now "lines") and a top-level "pbx" section (now in its gateway).
        // Converted after parsing (SettingsFile.Normalize).
        [YamlMember(Alias = "channels")]
        public List<Channel> legacyChannels
        {
            get => null;
            set => LegacyChannelsValue = value;
        }

        [YamlIgnore]
        internal List<Channel> LegacyChannelsValue { get; private set; }

        [YamlMember(Alias = "pbx")]
        public LegacyPbxSettings legacyPbx
        {
            get => null;
            set => LegacyPbxValue = value;
        }

        [YamlIgnore]
        internal LegacyPbxSettings LegacyPbxValue { get; private set; }

        // What Normalize converted from the old format; null: the file is in the current format.
        [YamlIgnore]
        internal SettingsMigration Migration { get; set; }

        // The Asterisk ghostline writes the config of (gateways[].pbx: managed); there is at most one.
        [YamlIgnore]
        public Gateway ManagedGateway => gateways?.FirstOrDefault(g => g.Managed);

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
        // Also write SMS and calls to /var/log/ghostline/ghostline_DATE.log, text files for other systems.
        // The Journal tab reads the DB and does not need them.
        public bool file { get; set; }
        // The system log (journald, /var/log/ghostline/system-DATE.log, the System tab):
        // error | warning | info | debug. Empty: info.
        public string level { get; set; }
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
            lines = channels,
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
            Normalize(s);
            return s;
        }

        // The format before 2026-10-30, converted in memory:
        // - gateways of type quectel / dongle become type asterisk; several of them on one Asterisk
        //   (same ip and AMI port) become one gateway, and the lines follow it;
        // - a line's "line" becomes "modem" (asterisk; the modem is added to its gateway with the
        //   driver of the old gateway type) or "port" (yeastar, goip);
        // - "channels" becomes "lines";
        // - the top-level "pbx" section moves into the gateway it named (pbx.asterisk).
        internal static void Normalize(Settings s)
        {
            var m = new SettingsMigration();
            s.gateways ??= new List<Gateway>();
            if (s.LegacyChannelsValue != null)
            {
                m.ChannelsRenamed = true;
                s.lines ??= s.LegacyChannelsValue;
            }
            s.lines ??= new List<Channel>();

            var driverOf = s.gateways.Where(g => g.type is "quectel" or "dongle").ToDictionary(g => g.id ?? "", g => g.type);
            if (driverOf.Count > 0)
            {
                string pbxHost = s.LegacyPbxValue?.asterisk;
                var groups = s.gateways.Where(g => driverOf.ContainsKey(g.id ?? "") || g.IsAsterisk)
                    .GroupBy(g => ((g.ip ?? "").ToLowerInvariant(), g.amiPort)).ToList();
                foreach (var group in groups)
                {
                    var list = group.ToList();
                    var keep = list.FirstOrDefault(g => g.IsAsterisk) ?? list.FirstOrDefault(g => g.id == pbxHost) ?? list[0];
                    foreach (var g in list.Where(g => g != keep))
                    {
                        m.Merged[g.id] = keep.id;
                        keep.monitor |= g.monitor;
                    }
                    if (!keep.IsAsterisk)
                    {
                        keep.type = "asterisk";
                        m.Retyped.Add(keep.id);
                    }
                }
                s.gateways.RemoveAll(g => m.Merged.ContainsKey(g.id ?? ""));
            }

            foreach (var ch in s.lines)
            {
                string oldGateway = ch.gateway ?? "";
                if (m.Merged.TryGetValue(oldGateway, out var to))
                {
                    ch.gateway = to;
                    m.Regatewayed.Add(ch.name);
                }
                if (ch.LegacyLine == null) continue;
                var gw = s.gateways.FirstOrDefault(g => g.id == ch.gateway);
                if (gw?.IsAsterisk == true)
                {
                    ch.modem ??= ch.LegacyLine;
                    m.LineKeys[ch.name ?? ""] = "modem";
                    if (gw.Modem(ch.modem) == null && driverOf.TryGetValue(oldGateway, out var driver))
                        (gw.modems ??= new List<PbxModem>()).Add(new PbxModem { name = ch.modem, driver = driver });
                }
                else
                {
                    ch.port ??= ch.LegacyLine;
                    m.LineKeys[ch.name ?? ""] = "port";
                }
            }

            var p = s.LegacyPbxValue;
            if (p != null)
            {
                m.PbxRemoved = true;
                string host = p.asterisk != null && m.Merged.TryGetValue(p.asterisk, out var merged) ? merged : p.asterisk;
                var gw = s.gateways.FirstOrDefault(g => g.id == host);
                if (string.Equals(p.mode, "managed", StringComparison.OrdinalIgnoreCase) && gw?.IsAsterisk == true)
                {
                    gw.pbx = "managed";
                    gw.managed = new ManagedPbx
                    {
                        confDir = p.confDir,
                        asteriskDir = p.asteriskDir,
                        recordingsPath = p.recordingsPath,
                        sip = new PbxSip { tls = p.tls ?? new PbxTls(), udpPort = p.udpPort, externalAddress = p.externalAddress, localNets = p.localNets ?? new() },
                    };
                    foreach (var modem in p.modems ?? new())
                    {
                        gw.modems ??= new List<PbxModem>();
                        int at = gw.modems.FindIndex(x => x.name == modem.name);
                        if (at >= 0) gw.modems[at] = modem;
                        else gw.modems.Add(modem);
                    }
                    m.ManagedGateway = gw.id;
                }
            }

            s.Migration = m.Any ? m : null;
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

        // A file of the format before 2026-10-30 is rewritten once, in place (comments kept); the old one
        // stays next to it as .bak-v2. When the rewritten text does not read back the same, the settings
        // are written out in full instead (comments lost).
        public static void MigrateToV2(string yamlPath)
        {
            if (!File.Exists(yamlPath)) return;
            string yaml = File.ReadAllText(yamlPath);
            var s = Parse(yaml);
            if (s.Migration == null) return;

            string expected = Serialize(s);
            string text = ConfigYaml.Migrate(yaml, s);
            bool same;
            try
            {
                var back = Parse(text);
                same = back.Migration == null && Serialize(back) == expected;
            }
            catch { same = false; }
            if (!same)
                text = "# ghostline settings. Converted " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\n" + expected;

            File.Copy(yamlPath, yamlPath + ".bak-v2", overwrite: true);
            // In place, not via a temp file: the config keeps its mode (it holds secrets).
            File.WriteAllText(yamlPath, text);
            SysLog.Info("config", $"converted to the current format{(same ? "" : " (written out in full, comments lost)")}: " +
                                  $"{yamlPath}, the old one is {yamlPath}.bak-v2");
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
            SysLog.Info("config", $"migrated: {jsonPath} -> {yamlPath}");
        }
    }

    // What SettingsFile.Normalize converted, so the file can be rewritten the same way
    // (ConfigYaml.Migrate).
    internal class SettingsMigration
    {
        public bool ChannelsRenamed;
        public bool PbxRemoved;
        public string ManagedGateway;
        public readonly Dictionary<string, string> Merged = new();      // removed gateway id -> kept one
        public readonly List<string> Retyped = new();                   // gateways that became "asterisk"
        public readonly List<string> Regatewayed = new();               // lines moved to the kept gateway
        public readonly Dictionary<string, string> LineKeys = new();    // line -> "modem" / "port"

        public bool Any => ChannelsRenamed || PbxRemoved || Merged.Count > 0 || Retyped.Count > 0 || LineKeys.Count > 0;
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
            foreach (Channel ch in settings.lines)
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
            foreach (Channel ch in settings.lines)
            {
                if (ch.gateway == gatewayId && ch.Device == line)
                    return ch;
            }
            return null;
        }

        internal static Gateway getGatewayById(string id) =>
            settings.gateways?.FirstOrDefault(g => g.id == id);

        // The driver of the line's modem (asterisk gateways); null for yeastar / goip.
        internal static ModemFlavor LineFlavor(Settings s, Channel ch) =>
            Modems.For(s?.gateways?.FirstOrDefault(g => g.id == ch?.gateway)?.Modem(ch?.modem)?.driver);

        internal static Channel getChannelType(string str)
        {
            foreach (Channel ch in settings.lines)
            {
                if (str.ToUpper() == ch.name.ToUpper())
                    return ch;
            }
            return null;
        }

    }
}
