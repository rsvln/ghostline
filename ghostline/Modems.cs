namespace ghostline
{
    // GSM modems driven by an Asterisk channel driver over AMI. chan_quectel is a fork of chan_dongle
    // and keeps its design, so the two differ only in names: the AMI events and actions, the CLI
    // word and the channel technology.
    internal sealed record ModemFlavor(
        string Driver,      // gateways[].modems[].driver
        string Letter,      // gateway letter in the SMS history and the log ("q", "d")
        string Prefix,      // AMI names: <Prefix>NewSMS, <Prefix>ShowDevices, <Prefix>DeviceEntry
        string Cli,         // "<cli> sms <device> <number> <text>"
        string Tech)        // channel technology in the CDR: "<Tech>/<device>-<id>"
    {
        public string NewSmsEvent => Prefix + "NewSMS";
        public string ShowDevicesAction => Prefix + "ShowDevices";
        public string DeviceEntryEvent => Prefix + "DeviceEntry";
    }

    internal static class Modems
    {
        public static readonly ModemFlavor Quectel = new("quectel", "q", "Quectel", "quectel", "Quectel");
        public static readonly ModemFlavor Dongle = new("dongle", "d", "Dongle", "dongle", "Dongle");
        internal static readonly ModemFlavor[] All = { Quectel, Dongle };

        // null: not a modem driver.
        public static ModemFlavor For(string driver) => All.FirstOrDefault(f => f.Driver == driver);

        // Gateways with a permanent AMI connection: Asterisk and Yeastar.
        public static bool IsAmi(string type) => type is "asterisk" or "yeastar";

        // "Quectel/gsm1-0100000002" → "gsm1", "Dongle/hw1-0100000001" → "hw1"; null for any other channel.
        public static string Device(string channel)
        {
            if (string.IsNullOrEmpty(channel)) return null;
            foreach (var f in All)
            {
                string prefix = f.Tech + "/";
                if (!channel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string rest = channel.Substring(prefix.Length);
                int dash = rest.IndexOf('-');
                return dash > 0 ? rest.Substring(0, dash) : rest;
            }
            return null;
        }
    }
}
