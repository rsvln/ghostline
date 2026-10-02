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
    // Точка входа и общее состояние процесса. Остальная логика разбита по темам:
    // Config.cs — модели конфига и разрешение канал→шлюз;
    // Contacts.cs — загрузка/сопоставление контактов;
    // AmiTransport.cs — AMI-подключения, чтение сокета, разбор событий;
    // InboundSms.cs — доставка входящих SMS (yeastar/quectel через AMI, goip через SMTP);
    // OutboundSms.cs — очередь и отправка исходящих SMS;
    // TelegramBridge.cs — очередь уведомлений и обработка команд бота;
    // Store*.cs — SQLite: SMS, очереди, звонки;
    // Calls.cs, Recordings.cs, Transcriber.cs, CallTelegram.cs — звонки (mode "full").
    internal partial class Program
    {
        public static Settings settings;
        public static ITelegramBotClient bot;
        public static List<GoogleContactNormalized> contacts = new List<GoogleContactNormalized>();
        // Общий HttpClient безопасен для всех yeastar-шлюзов сразу: креды едут
        // в query string конкретного запроса, а не хранятся в самом клиенте.
        private static readonly HttpClient _yeastarClient = new HttpClient();
        // А вот goip-креды сидят в заголовках клиента (Basic auth) — на каждый
        // физический goip-бокс нужен свой HttpClient. AMI (yeastar/quectel) —
        // персистентный сокет, тоже один на физический шлюз. Ключ — Gateway.id.
        private static readonly ConcurrentDictionary<string, HttpClient> _goipClients = new();
        private static readonly ConcurrentDictionary<string, TcpClient> _amiClients = new();

        // Буфер частей многочастных SMS. Ключ: "<id шлюза>|<канал/устройство>|<отправитель>".
        private static readonly ConcurrentDictionary<string, string[]> _multipartBuffers = new();

        static void Main(string[] args)
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            Console.WriteLine($"ghostline {version}");

            contacts = LoadContacts();

            //get config
            string fs;
            if (args.Length == 0)
                fs = "/etc/ghostline/ghostline.yaml";
            else
                fs = args[0];

            Console.WriteLine(fs);
            try
            {
                SettingsFile.MigrateFromJson(fs);
                settings = SettingsFile.Parse(System.IO.File.ReadAllText(fs));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Bad settings file: " + SettingsFile.Describe(ex) + ". Exit");
                return;
            }

            L10n.Load(settings.locale);

            string dbPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(fs)), "ghostline.db");
            Store.Init(dbPath);
            WebUi.Start(fs);

            settings.gateways ??= new List<Gateway>();
            settings.channels ??= new List<Channel>();

            // Конфиг несогласован — канал ссылается на шлюз, которого нет в gateways[].
            // Это молча ничего не сломает (канал просто никогда не сработает), поэтому
            // явно предупреждаем в лог, а не оставляем гадать, почему SIM-линия не отвечает.
            foreach (var ch in settings.channels)
            {
                if (settings.gateways.All(g => g.id != ch.gateway))
                    Console.WriteLine($"Config warning: channel '{ch.name}' references unknown gateway '{ch.gateway}'");
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
                else if (gw.type == "yeastar" || gw.type == "quectel")
                {
                    // Через Task.Run — недоступность/невалидный адрес одного шлюза при
                    // старте не должна блокировать или ронять остальные шлюзы, бота и веб-UI.
                    var client = new TcpClient();
                    _amiClients[gw.id] = client;
                    Task.Run(() => ConnectAmiWithRetry(gw, client));
                    Task.Run(() => AmiKeepaliveLoop(gw.id));
                }
                else
                {
                    Console.WriteLine($"Config warning: gateway '{gw.id}' has unknown type '{gw.type}'");
                }
            }

            //tg start
            HealthStatus.MarkConfigured("telegram");
            bot = new TelegramBotClient(settings.telegram.token);
            CancellationTokenSource cts = new();
            ReceiverOptions tgreceiverOptions = new()
            {
                AllowedUpdates = Array.Empty<UpdateType>()
            };
            bot.StartReceiving(updateHandler: TgHandleUpdateAsync, errorHandler: TgHandlePollingErrorAsync, receiverOptions: tgreceiverOptions, cancellationToken: cts.Token);
            Task.Run(async () =>
            {
                try
                {
                    await bot.GetMe();
                    HealthStatus.MarkConnected("telegram");
                }
                catch (Exception ex)
                {
                    HealthStatus.MarkError("telegram", ex.Message);
                }
            });
            Task.Run(() => TelegramOutboxWorker());
            Task.Run(() => SmsOutboxWorker());

            // Режим "full": звонки — журнал из CDR АТС, записи, расшифровка, Telegram.
            // В режиме "sms" ничего из этого не запускается и к АТС и сервису расшифровки не обращается.
            Console.WriteLine("mode: " + (settings.FullMode ? "full" : "sms"));
            if (settings.FullMode)
            {
                if (settings.calls?.cdrDb == null || string.IsNullOrEmpty(settings.calls.recordingsUrl))
                {
                    Console.WriteLine("Config error: mode \"full\" needs calls.cdrDb and calls.recordingsUrl — calls are disabled");
                }
                else
                {
                    Task.Run(() => CallsPollerLoop());
                    Task.Run(() => CallTelegramWorker());
                    if (TranscribeEnabled)
                    {
                        Task.Run(() => TranscribeWorker(requested: false));
                        Task.Run(() => TranscribeWorker(requested: true));
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
