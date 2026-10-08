using Newtonsoft.Json;

namespace ghostline
{
    // Строки одного языка из locales/<язык>.json рядом с приложением. Ключа нет в выбранном
    // языке — берётся из en.json, нет нигде — возвращается сам ключ. Как в frte2tg.
    public class Strings
    {
        readonly Dictionary<string, string> strings;
        readonly Dictionary<string, string> fallback;

        public string Locale { get; }

        public Strings(string locale, Dictionary<string, string> strings, Dictionary<string, string> fallback)
        {
            Locale = locale;
            this.strings = strings;
            this.fallback = fallback;
        }

        public string T(string key, params object[] args)
        {
            string s = strings.TryGetValue(key, out var v) ? v : fallback.TryGetValue(key, out var f) ? f : key;
            return args.Length == 0 ? s : string.Format(s, args);
        }

        public bool Has(string key) => strings.ContainsKey(key) || fallback.ContainsKey(key);

        // Строки с заданными префиксами (с учётом en) — для скриптов веб-страницы.
        public Dictionary<string, string> Export(params string[] prefixes)
        {
            var result = new Dictionary<string, string>();
            foreach (var kv in fallback.Concat(strings))
                if (prefixes.Any(p => kv.Key.StartsWith(p)))
                    result[kv.Key] = kv.Value;
            return result;
        }
    }

    // Языки приложения: веб-интерфейс, Telegram и скачиваемая расшифровка
    // (locale.web, locale.telegram, locale.transcript; пусто — locale.default).
    public static class L10n
    {
        public const string DefaultLocale = "en";

        static readonly Strings Empty = new(DefaultLocale, new(), new());
        public static Strings Web { get; private set; } = Empty;
        public static Strings Tg { get; private set; } = Empty;
        public static Strings Transcript { get; private set; } = Empty;

        public static string LocalesDir => Path.Combine(AppContext.BaseDirectory, "locales");

        public static void Load(LocaleSettings s)
        {
            string Or(string v) => string.IsNullOrWhiteSpace(v) ? s?.@default : v;
            var fallback = ReadFile(DefaultLocale) ?? new Dictionary<string, string>();
            Web = Make(Or(s?.web), fallback);
            Tg = Make(Or(s?.telegram), fallback);
            Transcript = Make(Or(s?.transcript), fallback);
            Console.WriteLine($"locale: web={Web.Locale}, telegram={Tg.Locale}, transcript={Transcript.Locale}");
        }

        static Strings Make(string locale, Dictionary<string, string> fallback)
        {
            locale = string.IsNullOrWhiteSpace(locale) ? DefaultLocale : locale.Trim().ToLowerInvariant();
            var selected = locale == DefaultLocale ? fallback : ReadFile(locale);
            if (selected == null)
            {
                Console.WriteLine($"Locale '{locale}' not found in {LocalesDir}, using {DefaultLocale}");
                return new Strings(DefaultLocale, fallback, fallback);
            }
            return new Strings(locale, selected, fallback);
        }

        static Dictionary<string, string> ReadFile(string locale)
        {
            string path = Path.Combine(LocalesDir, locale + ".json");
            if (!File.Exists(path))
                return null;
            try
            {
                return JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to read locale file {path}: {ex.Message}");
                return null;
            }
        }
    }
}
