using System.IO;
using System.Reflection;
using System.Text;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;
using SmtpServer;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Collections.Concurrent;

namespace ghostline
{
    // Entry point and the shared process state. The rest of the logic is split by topic:
    // Config.cs: config models and the channel-to-gateway lookup;
    // Contacts.cs: loading and matching contacts;
    // AmiTransport.cs: AMI connections, socket reading, event parsing;
    // InboundSms.cs: delivery of incoming SMS (yeastar/quectel via AMI, goip via SMTP);
    // OutboundSms.cs: queue and sending of outgoing SMS;
    // TelegramBridge.cs: notification queue and bot commands;
    // Store*.cs: SQLite for SMS, queues and calls;
    // Calls.cs, Recordings.cs, Transcriber.cs, CallTelegram.cs: calls (mode "full").
    internal partial class Program
    {
        public static Settings settings;
        public static ITelegramBotClient bot;
        public static readonly DateTime StartedAt = DateTime.Now;
        public static List<GoogleContactNormalized> contacts = new List<GoogleContactNormalized>();
        // One shared HttpClient is safe for all yeastar gateways at once: credentials go
        // in the query string of each request and are not stored in the client.
        private static readonly HttpClient _yeastarClient = new HttpClient();
        // goip credentials, however, live in the client headers (Basic auth), so every
        // physical goip box needs its own HttpClient. AMI (yeastar/quectel) is a
        // persistent socket, also one per physical gateway. The key is Gateway.id.
        private static readonly ConcurrentDictionary<string, HttpClient> _goipClients = new();
        private static readonly ConcurrentDictionary<string, TcpClient> _amiClients = new();
        // Whether a gateway's AMI is up, so the system log gets one line per change, not one per retry.
        private static readonly ConcurrentDictionary<string, bool> _amiUp = new();

        // Buffer of multipart SMS parts. Key: "<gateway id>|<channel/device>|<sender>".
        private static readonly ConcurrentDictionary<string, string[]> _multipartBuffers = new();

        static void Main(string[] args)
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            SysLog.Info("app", $"ghostline {version} starting");

            contacts = LoadContacts();

            //get config
            string fs;
            if (args.Length == 0)
                fs = "/etc/ghostline/ghostline.yaml";
            else
                fs = args[0];

            SysLog.Info("config", "config: " + fs);
            try
            {
                SettingsFile.MigrateFromJson(fs);
                SettingsFile.Migrate(fs);
                settings = SettingsFile.Parse(System.IO.File.ReadAllText(fs));
                SysLog.Configure(settings.logger?.level);
                SyncHostFiles(settings);
            }
            catch (Exception ex)
            {
                SysLog.Error("config", "bad settings file: " + SettingsFile.Describe(ex) + "; exit");
                return;
            }

            L10n.Load(settings.locale);

            string dbPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(fs)), "ghostline.db");
            Store.Init(dbPath);
            ConfigPath = fs;
            WebUi.Start(fs);

            settings.gateways ??= new List<Gateway>();
            settings.lines ??= new List<Channel>();

            // gateways[].pbx: managed — ghostline writes that Asterisk's config; otherwise FreePBX owns it.
            var managed = settings.ManagedGateway;
            SysLog.Info("app", "pbx: " + (managed != null ? "managed (" + managed.id + ")" : "freepbx"));
            if (managed != null)
                Task.Run(() => PbxCertWatchLoop());
            if (settings.lines.Any(c => c.phone != null && c.gateway != managed?.id))
                SysLog.Warn("config", "lines[].phone is used only on a gateway with pbx: managed, ignored");

            // Inconsistent config: a channel refers to a gateway that is not in gateways[].
            // Nothing breaks loudly (the channel simply never fires), so warn explicitly
            // in the log instead of leaving the user guessing why a SIM line is silent.
            foreach (var ch in settings.lines)
            {
                var gw = settings.gateways.FirstOrDefault(g => g.id == ch.gateway);
                if (gw == null)
                    SysLog.Warn("config", $"line '{ch.name}' references unknown gateway '{ch.gateway}'");
                else if (gw.IsAsterisk && gw.Modem(ch.modem) == null)
                    SysLog.Warn("config", $"line '{ch.name}': gateway '{gw.id}' has no modem '{ch.modem}'");
            }

            foreach (var gw in settings.gateways)
            {
                HealthStatus.MarkConfigured(gw.id);

                if (gw.type == "goip")
                {
                    var client = new HttpClient();
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(gw.user + ":" + gw.password)));
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Connection", "Keep-Alive");
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Keep-Alive", "timeout=600");
                    _goipClients[gw.id] = client;
                }
                else if (Modems.IsAmi(gw.type))
                {
                    // Via Task.Run: an unavailable or invalid address of one gateway at startup
                    // must not block or crash the other gateways, the bot or the web UI.
                    var client = new TcpClient();
                    _amiClients[gw.id] = client;
                    Task.Run(() => ConnectAmiWithRetry(gw, client));
                    Task.Run(() => AmiKeepaliveLoop(gw.id));
                    if (RadioMonitored(gw))
                        Task.Run(() => RadioPollLoop(gw));
                }
                else
                {
                    SysLog.Warn("config", $"gateway '{gw.id}' has unknown type '{gw.type}'");
                }
            }

            // Notification channels, both optional: Telegram (telegram.token) and ntfy (ntfy.url).
            if (TelegramEnabled)
            {
                HealthStatus.MarkConfigured("telegram");
                bot = new TelegramBotClient(settings.clients.telegram.token);
                CancellationTokenSource cts = new();
                ReceiverOptions tgreceiverOptions = new()
                {
                    AllowedUpdates = Array.Empty<UpdateType>()
                };
                bot.StartReceiving(updateHandler: TgHandleUpdateAsync, errorHandler: TgHandlePollingErrorAsync, receiverOptions: tgreceiverOptions, cancellationToken: cts.Token);
                Task.Run(() => TelegramHealthLoop());
                Task.Run(() => SetBotCommands());
            }
            if (NtfyEnabled)
            {
                HealthStatus.MarkConfigured("ntfy");
                Task.Run(() => NtfyListenLoop());
            }
            SysLog.Info("app", $"clients: telegram={(TelegramEnabled ? "on" : "off")}, ntfy={(NtfyEnabled ? "on" : "off")}");
            Task.Run(() => TelegramOutboxWorker());
            Task.Run(() => SmsOutboxWorker());

            // Mode "full": calls, i.e. the journal from the PBX CDR, recordings, transcription, Telegram.
            // In mode "sms" none of this starts, and neither the PBX nor the transcription service is contacted.
            SysLog.Info("app", "mode: " + (settings.FullMode ? "full" : "sms"));
            if (settings.FullMode)
            {
                if (settings.calls?.CdrConfigured != true || !settings.calls.RecordingsConfigured)
                {
                    SysLog.Error("config", "mode \"full\" needs system.calls.cdr_db or cdr_sqlite, and recordings_url " +
                                           "or recordings_dir; calls are disabled");
                }
                else
                {
                    Task.Run(() => CallsPollerLoop());
                    Task.Run(() => CallTelegramWorker());
                    if (TranscribeEnabled)
                    {
                        Task.Run(() => TranscribeWorker(requested: false));
                        Task.Run(() => TranscribeWorker(requested: true));
                        Task.Run(() => TranscribeHealthLoop());
                    }
                }
            }

            //smtp server start (for goip)
            if (settings.goip.smtp.enabled)
            {
                var options = new SmtpServerOptionsBuilder().ServerName("goipsmtpserver").Port(settings.goip.smtp.port).Build();
                var serviceProvider = new SmtpServer.ComponentModel.ServiceProvider();
                serviceProvider.Add(new SampleMessageStore());
                var smtpServer = new SmtpServer.SmtpServer(options, serviceProvider);
                Task.Run(() => smtpServer.StartAsync(CancellationToken.None));
            }

            Thread.Sleep(Timeout.Infinite);

        }
    }
}
