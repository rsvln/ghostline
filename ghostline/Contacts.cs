using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace ghostline
{
    public class GoogleContact
    {
        public string FirstName { get; set; }
        public string MiddleName { get; set; }
        public string LastName { get; set; }
        public string Photo { get; set; }
        public string EmailAddress { get; set; }
        public string PhoneNumberLabel1 { get; set; } = string.Empty;
        public string PhoneNumberValue1 { get; set; } = string.Empty;
        public string PhoneNumberLabel2 { get; set; } = string.Empty;
        public string PhoneNumberValue2 { get; set; } = string.Empty;
        public string PhoneNumberLabel3 { get; set; } = string.Empty;
        public string PhoneNumberValue3 { get; set; } = string.Empty;
        public string PhoneNumberLabel4 { get; set; } = string.Empty;
        public string PhoneNumberValue4 { get; set; } = string.Empty;

    }

    public class GoogleContactNormalized
    {
        public string FirstName { get; set; }
        public string MiddleName { get; set; }
        public string LastName { get; set; }
        public string Photo { get; set; }
        public string EmailAddress { get; set; }
        public string PhoneNumberLabel { get; set; } = string.Empty;
        public string PhoneNumberValue { get; set; } = string.Empty;
    }

    public sealed class GoogleContactMap : ClassMap<GoogleContact>
    {
        public GoogleContactMap()
        {
            Map(m => m.FirstName).Name("First Name");
            Map(m => m.MiddleName).Name("Middle Name");
            Map(m => m.LastName).Name("Last Name");
            Map(m => m.Photo).Name("Photo");
            Map(m => m.EmailAddress).Name("E-mail 1 - Value");
            Map(m => m.PhoneNumberLabel1).Name("Phone 1 - Label");
            Map(m => m.PhoneNumberValue1).Name("Phone 1 - Value");
            Map(m => m.PhoneNumberLabel2).Name("Phone 2 - Label");
            Map(m => m.PhoneNumberValue2).Name("Phone 2 - Value");
            Map(m => m.PhoneNumberLabel3).Name("Phone 3 - Label");
            Map(m => m.PhoneNumberValue3).Name("Phone 3 - Value");
            Map(m => m.PhoneNumberLabel4).Name("Phone 4 - Label");
            Map(m => m.PhoneNumberValue4).Name("Phone 4 - Value");

        }
    }

    internal partial class Program
    {
        private static List<GoogleContactNormalized> LoadContacts()
        {
            var result = new List<GoogleContactNormalized>();
            try
            {
                using var reader = new StreamReader("/etc/ghostline/contacts.csv");
                using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
                csv.Context.RegisterClassMap<GoogleContactMap>();
                foreach (var r in csv.GetRecords<GoogleContact>())
                {
                    foreach (var (label, value) in new[]
                    {
                        (r.PhoneNumberLabel1, r.PhoneNumberValue1),
                        (r.PhoneNumberLabel2, r.PhoneNumberValue2),
                        (r.PhoneNumberLabel3, r.PhoneNumberValue3),
                        (r.PhoneNumberLabel4, r.PhoneNumberValue4),
                    })
                    {
                        if (!string.IsNullOrEmpty(value))
                            result.Add(new GoogleContactNormalized
                            {
                                FirstName = r.FirstName,
                                LastName = r.LastName,
                                MiddleName = r.MiddleName,
                                EmailAddress = r.EmailAddress,
                                Photo = r.Photo,
                                PhoneNumberLabel = label,
                                // Тем же ToE164, что и входящие/исходящие номера —
                                // иначе матчинг сравнивал бы две разные нотации.
                                PhoneNumberValue = ToE164(value)
                            });
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("Contacts load failed: " + ex.Message); }
            return result;
        }

        // Снимает форматирование, оставляя только содержательные символы номера.
        private static string NormalizePhone(string raw)
        {
            return raw.Replace("+", "").Replace(" ", "").Replace("-", "")
                      .Replace("(", "").Replace(")", "").Replace(".", "");
        }

        // Приводит "адрес" SMS к единому каноническому виду — им пользуются и БД, и
        // UI, и Telegram, и матчинг контактов. Различает три класса:
        //   * буквенный sender ID ("VTB", "Greenatom") — не трогаем вообще;
        //   * короткий сервисный код ("900", "0867") — только цифры, без "+";
        //   * телефон — E.164 ("+79001234567", "+37366600941").
        // Формат набора под конкретный шлюз выводится из результата отдельно,
        // см. FormatForGateway в OutboundSms.cs.
        private static string ToE164(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;

            string trimmed = raw.Trim();
            bool hadPlus = trimmed.StartsWith("+");
            string digits = NormalizePhone(trimmed);

            // Буквенный sender ID (банки/сервисы шлют не с номера, а с имени).
            if (digits.Length == 0 || !digits.All(char.IsDigit))
                return trimmed;

            // Короткий сервисный номер. Проверка идёт до всего остального и игнорирует
            // "+": в E.164 не бывает номера из <= 6 цифр, значит "+900" — это не
            // международный номер, а короткий код с ошибочно приклеенным плюсом.
            if (digits.Length <= 6)
                return digits;

            // Явный международный формат — доверяем ему как есть.
            if (hadPlus)
                return "+" + digits;

            // "810" — российский выход на международную линию, дальше код страны и
            // номер. Требуем длину >= 12: "810" + код страны + номер короче не бывает.
            if (digits.StartsWith("810") && digits.Length >= 12)
                return "+" + digits.Substring(3);

            // Домашний российский номер с "8" вместо "+7".
            if (digits.Length == 11 && digits.StartsWith("8"))
                return "+7" + digits.Substring(1);

            // Уже "7XXXXXXXXXX", просто без "+".
            if (digits.Length == 11 && digits.StartsWith("7"))
                return "+" + digits;

            // Голый 10-значный номер без префикса — считаем российским домашним.
            if (digits.Length == 10)
                return "+7" + digits;

            // 11+ цифр с чужим кодом страны, записанные без "+".
            if (digits.Length >= 11)
                return "+" + digits;

            // 7-9 цифр: местный номер без кода города либо длинный сервисный.
            // Приписать код страны наугад = соврать, поэтому оставляем как есть.
            return digits;
        }

        private static bool IsPhoneNumber(string canonical) =>
            !string.IsNullOrEmpty(canonical) && canonical.StartsWith("+");

        private static GoogleContactNormalized? FindContact(string rawNumber)
        {
            string canonical = ToE164(rawNumber);
            if (string.IsNullOrWhiteSpace(canonical)) return null;

            // Контакты канонизированы тем же ToE164 при загрузке, так что сравниваем
            // одну нотацию с другой такой же. Точное совпадение — единственный
            // допустимый вариант для коротких кодов и буквенных ID: старый
            // подстрочный матчинг находил на "900" первого встречного абонента,
            // в чьём номере эти цифры просто где-то встречались.
            var exact = contacts.FirstOrDefault(c =>
                string.Equals(c.PhoneNumberValue, canonical, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            // Подстраховка для телефонов, записанных в контактах в форме, которая не
            // сошлась к той же канонической строке: сверяем последние 10 цифр.
            if (IsPhoneNumber(canonical))
            {
                string? tail = Tail10(canonical);
                if (tail != null)
                    return contacts.FirstOrDefault(c =>
                        IsPhoneNumber(c.PhoneNumberValue) && Tail10(c.PhoneNumberValue) == tail);
            }

            return null;
        }

        private static string? Tail10(string canonical)
        {
            string digits = NormalizePhone(canonical);
            return digits.Length >= 10 ? digits.Substring(digits.Length - 10) : null;
        }
    }
}
