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
                                // Through the same ToE164 as incoming and outgoing numbers;
                                // otherwise matching would compare two different notations.
                                PhoneNumberValue = ToE164(value)
                            });
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("Contacts load failed: " + ex.Message); }
            return result;
        }

        // Strips formatting, keeping only the meaningful characters of the number.
        private static string NormalizePhone(string raw)
        {
            return raw.Replace("+", "").Replace(" ", "").Replace("-", "")
                      .Replace("(", "").Replace(")", "").Replace(".", "");
        }

        // Brings an SMS "address" to one canonical form, used by the DB, the UI,
        // Telegram and contact matching. Three classes:
        //   * alphanumeric sender ID ("VTB", "Greenatom"): left untouched;
        //   * short service code ("900", "0867"): digits only, no "+";
        //   * phone number: E.164 ("+79001234567", "+37366600941").
        // The dial format for a particular gateway is derived from the result separately,
        // see FormatForGateway in OutboundSms.cs.
        private static string ToE164(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;

            string trimmed = raw.Trim();
            bool hadPlus = trimmed.StartsWith("+");
            string digits = NormalizePhone(trimmed);

            // Alphanumeric sender ID (banks and services send from a name, not a number).
            if (digits.Length == 0 || !digits.All(char.IsDigit))
                return trimmed;

            // Short service number. Checked before everything else and ignores
            // "+": E.164 has no numbers of <= 6 digits, so "+900" is not an
            // international number but a short code with a plus added by mistake.
            if (digits.Length <= 6)
                return digits;

            // Explicit international format: trusted as is.
            if (hadPlus)
                return "+" + digits;

            // "810" is the Russian international prefix, then the country code and the
            // number. Length >= 12 is required: "810" + country code + number is never shorter.
            if (digits.StartsWith("810") && digits.Length >= 12)
                return "+" + digits.Substring(3);

            // Domestic Russian number with "8" instead of "+7".
            if (digits.Length == 11 && digits.StartsWith("8"))
                return "+7" + digits.Substring(1);

            // Already "7XXXXXXXXXX", just without "+".
            if (digits.Length == 11 && digits.StartsWith("7"))
                return "+" + digits;

            // A bare 10-digit number without a prefix: treated as Russian domestic.
            if (digits.Length == 10)
                return "+7" + digits;

            // 11+ digits with a foreign country code, written without "+".
            if (digits.Length >= 11)
                return "+" + digits;

            // 7-9 digits: a local number without the area code or a long service number.
            // Adding a country code by guessing would be lying, so it stays as is.
            return digits;
        }

        private static bool IsPhoneNumber(string canonical) =>
            !string.IsNullOrEmpty(canonical) && canonical.StartsWith("+");

        private static GoogleContactNormalized? FindContact(string rawNumber)
        {
            string canonical = ToE164(rawNumber);
            if (string.IsNullOrWhiteSpace(canonical)) return null;

            // Contacts were made canonical with the same ToE164 when loaded, so one
            // notation is compared with the same notation. An exact match is the only
            // acceptable option for short codes and alphanumeric ids: the old substring
            // matching found the first random subscriber for "900" whose number simply
            // contained these digits somewhere.
            var exact = contacts.FirstOrDefault(c =>
                string.Equals(c.PhoneNumberValue, canonical, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            // Fallback for phone numbers stored in the contacts in a form that did not
            // reduce to the same canonical string: compare the last 10 digits.
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
