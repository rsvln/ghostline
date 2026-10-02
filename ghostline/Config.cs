using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ghostline
{
    // Физический шлюз. Может быть несколько шлюзов одного типа (например,
    // два GoIP-бокса на разных IP) — каждый со своим id, адресом и кредами.
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
        // Id шлюза (Gateway.id), которому принадлежит эта SIM-линия.
        public string gateway { get; set; }
        public string name { get; set; }
        public string pattern { get; set; }
        // Для yeastar/goip — номер линии/порта в виде строки.
        // Для quectel — имя устройства из quectel.conf ("gsm1", "gsm2", ...).
        public string line { get; set; }
        // Номер SIM этой линии (+7…) — в шапке расшифровки «Звонил / Кому» для внешних
        // получателей; не задан — подставляется название линии.
        public string number { get; set; }
        // Что слать в Telegram по звонкам этой линии (только mode "full").
        // Секции нет — всё включено, чаты общие.
        public ChannelCalls calls { get; set; }
    }

    public class ChannelCalls
    {
        public bool voice { get; set; } = true;       // голосовое с записью разговора
        public bool transcript { get; set; } = true;  // расшифровка, дописывается в подпись позже
        public bool missed { get; set; } = true;      // уведомление о пропущенном входящем
        public List<string> chatIds { get; set; }     // пусто — общие telegram.chat_ids
    }

    public class CallsSettings
    {
        public CdrDbSettings cdrDb { get; set; }
        // Каталог записей АТС по HTTP (Apache alias на /var/spool/asterisk/monitor).
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
        // OpenAI-совместимый /v1/audio/transcriptions (speaches / faster-whisper).
        public string url { get; set; }
        public string model { get; set; }
        public string language { get; set; } = "ru";
        // Подсказка Whisper (initial_prompt): имена и термины, которые встречаются в разговорах —
        // на именах собственных и терминах без неё больше всего ошибок. К ней автоматически
        // добавляется имя собеседника из контактов.
        public string prompt { get; set; }
    }

    // Разделы настроек — как у frte2tg: telegram, web, logger, ...
    public class Settings
    {
        // "sms" — только SMS, как было; "full" — плюс звонки: журнал, записи,
        // расшифровка, Telegram.
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

    // Языки — файлы locales/<язык>.json рядом с приложением (en, ru). Как в frte2tg.
    public class LocaleSettings
    {
        public string @default { get; set; } = "en";
        public string web { get; set; }        // пусто — default
        public string telegram { get; set; }   // пусто — default
        public string transcript { get; set; } // скачиваемая расшифровка (PDF, .txt); пусто — default
    }

    public class TelegramSettings
    {
        public string token { get; set; }
        // Куда идут SMS (и звонки, если у линии нет своих). Только из этих чатов бот
        // принимает команды на отправку SMS.
        public List<string> chatIds { get; set; } = new();
    }

    // Веб-интерфейс; пустые user и password — без авторизации.
    public class WebSettings
    {
        public int port { get; set; } = 8889;
        public string user { get; set; }
        public string password { get; set; }
    }

    public class LoggerSettings
    {
        // Дублировать SMS в /var/log/ghostline/ghostline_ДАТА.log.
        public bool file { get; set; }
    }

    public class GoipSettings
    {
        // SMTP-приёмник: GoIP-шлюзы шлют входящие SMS письмами.
        public SmtpSettings smtp { get; set; } = new();
    }

    public class SmtpSettings
    {
        public bool enabled { get; set; }
        public int port { get; set; } = 25;
    }

    // Формат ghostline.json (yetgsms, до 2026-09-27): всё в корне, camelCase.
    // Нужен только для миграции.
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

    // Файл настроек — YAML с разделами (telegram, web, logger, calls...), ключи в snake_case.
    // Незнакомый ключ — ошибка (опечатка не должна молча выключать опцию).
    // До 2026-09-27 настройки были в JSON (ghostline.json) — при первом запуске
    // конвертируются в YAML, старый файл переименовывается в .json.migrated.
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

        // Ошибка YAML → «строка N, столбец M: что не так» — для веб-редактора и лога.
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

        // ghostline.yaml нет, а ghostline.json рядом есть — конвертируем.
        public static void MigrateFromJson(string yamlPath)
        {
            string jsonPath = Path.ChangeExtension(yamlPath, ".json");
            if (File.Exists(yamlPath) || !File.Exists(jsonPath)) return;

            var s = Newtonsoft.Json.JsonConvert.DeserializeObject<LegacyJsonSettings>(File.ReadAllText(jsonPath)).ToSettings();
            File.WriteAllText(yamlPath, "# ghostline — настройки. Сконвертировано из " + Path.GetFileName(jsonPath) +
                                        " " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\n" + Serialize(s));
            File.Move(jsonPath, jsonPath + ".migrated");
            Console.WriteLine($"Config migrated: {jsonPath} -> {yamlPath}");
        }
    }

    internal partial class Program
    {
        // gatewayId == null — искать по всем каналам (нужно для SMTPReadData/goip,
        // где pattern уже сам по себе уникален по серийнику устройства).
        // gatewayId задан — искать только среди каналов этого шлюза (нужно для
        // AMI-событий: два одинаковых по виду события с разных физических боксов
        // не должны матчиться на канал чужого шлюза).
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

        private static string getAllChannelNames()
        {
            string str = "";
            foreach (Channel ch in settings.channels)
            {
                str = str + ch.name + "\n";
            }
            return str.Trim();
        }
    }
}
