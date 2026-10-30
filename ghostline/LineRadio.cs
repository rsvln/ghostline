using System.Collections.Concurrent;
using System.Text;

namespace ghostline
{
    // Radio state of a SIM line as chan_quectel reports it: registration, operator, network type,
    // signal. Polled once a minute per modem gateway (AMI QuectelShowDevices / DongleShowDevices, which
    // only read the driver's state), kept in memory only.
    public class RadioState
    {
        public string State { get; set; }           // chan_quectel device state: Free, Ring, GSM not registered...
        public string Registration { get; set; }    // "Registered, home network"
        public string Provider { get; set; }
        public int Act { get; set; } = -1;          // 3GPP 27.007 access technology; -1: unknown (stock chan_quectel)
        public string Mode { get; set; }            // the driver's own mode text, from Huawei ^MODE (chan_dongle)
        public int Rssi { get; set; } = 99;         // 0..31, 99: unknown
        public string Lac { get; set; }
        public string Cell { get; set; }
        public string Number { get; set; }
        public string Model { get; set; }           // "EC200A", "E1550"
        public string DataPort { get; set; }        // data= of the device in quectel.conf / dongle.conf
        public DateTime UpdatedAt { get; set; }

        // The USB port of the modem: "7" from ".../pci-0000:00:14.0-usb-0:7:1.3-port0"; the device name
        // ("ttyUSB2") when the port is not given by path.
        public string UsbPort => DataPort == null ? null
            : System.Text.RegularExpressions.Regex.Match(DataPort, @"-usb(?:v2)?-\d+:([\d.]+):") is { Success: true } m
                ? m.Groups[1].Value
                : Path.GetFileName(DataPort);
        public DateTime? DownSince { get; set; }    // no network since then
        public bool Alerted { get; set; }           // "no network" already sent for this outage

        public bool Up => (Registration ?? "").StartsWith("Registered", StringComparison.OrdinalIgnoreCase)
                          && State is not ("Not connected" or "Not initialized" or "GSM not registered" or "Stopped");

        public string Net => Act switch
        {
            0 or 1 or 3 or 8 => "2G",
            2 or 4 or 5 or 6 => "3G",
            7 or 9 or 10 => "4G",
            11 or 12 or 13 => "5G",
            _ => NetOfMode(Mode)
        };

        // Huawei modules report the mode themselves (^MODE); the stock drivers keep it as text.
        internal static string NetOfMode(string mode) => (mode ?? "").ToUpperInvariant() switch
        {
            "GSM" or "GPRS" or "EDGE" or "GSM/GPRS" => "2G",
            "WCDMA" or "TD-SCDMA" or "HSDPA" or "HSUPA" or "HSDPA AND HSUPA" or "HSPA" or "HSPA+" => "3G",
            "LTE" => "4G",
            _ => null
        };

        // LTE serving cell quality (patched chan_quectel, Quectel and SIMCom modules); null: only RSSI is known.
        public int? Rsrp { get; set; }              // dBm
        public int? Rsrq { get; set; }              // dB
        public int? Sinr { get; set; }              // dB

        public int? RssiDbm => Rssi is >= 0 and <= 31 ? -113 + 2 * Rssi : null;

        // The level shown next to the bars: RSRP when known, RSSI otherwise.
        public int? Dbm => Rsrp ?? RssiDbm;

        // RSSI counts noise and neighbour cells too, so in LTE it shows full bars on a poor link. With RSRP
        // and RSRQ the bars are the worse of the two: a strong but noisy cell does not look good.
        public int Bars => Rsrp is { } rsrp
            ? Math.Min(rsrp switch { >= -85 => 4, >= -95 => 3, >= -105 => 2, >= -115 => 1, _ => 0 },
                       Rsrq switch { null => 4, >= -10 => 4, >= -13 => 3, >= -16 => 2, >= -19 => 1, _ => 0 })
            : Rssi switch { >= 20 and <= 31 => 4, >= 15 and < 20 => 3, >= 10 and < 15 => 2, >= 2 and < 10 => 1, _ => 0 };
    }

    internal partial class Program
    {
        private const string RadioActionId = "gl-radio";
        private const int RadioPollSeconds = 60;

        // Key: "<gateway id>|<device>" (lines[].modem).
        private static readonly ConcurrentDictionary<string, RadioState> _radio = new();
        // Gateways whose AMI user may not run the show-devices action: polling stops until a restart.
        private static readonly ConcurrentDictionary<string, bool> _radioDenied = new();

        internal static bool RadioMonitored(Gateway gw) => gw != null && gw.IsAsterisk && gw.monitor;

        // Data of the line, or null: not monitored, the gateway is down, or nothing came for 3 polls.
        internal static RadioState LineRadio(Channel ch)
        {
            var gw = getGatewayById(ch?.gateway);
            if (!RadioMonitored(gw) || HealthStatus.Find(gw.id)?.Connected != true) return null;
            return _radio.TryGetValue(gw.id + "|" + ch.Device, out var r)
                   && DateTime.Now - r.UpdatedAt < TimeSpan.FromSeconds(RadioPollSeconds * 3) ? r : null;
        }

        // Own number of the line: what the SIM reports (AT+CNUM), else channels[].number from the config.
        internal static string LineNumber(Channel ch) =>
            ch == null ? null : LineRadio(ch)?.Number ?? (string.IsNullOrWhiteSpace(ch.number) ? null : ch.number);

        // Polls the drivers of the gateway's modems. They are read from the file every time: a modem added
        // in the settings is polled without a restart.
        private static async Task RadioPollLoop(Gateway gw)
        {
            await Task.Delay(10000);   // after the AMI login
            while (!_radioDenied.ContainsKey(gw.id))
            {
                try
                {
                    var modems = (TryReadSettingsFile() ?? settings).gateways?.FirstOrDefault(g => g.id == gw.id)?.modems ?? gw.modems ?? new();
                    foreach (var flavor in modems.Select(m => Modems.For(m.driver)).Where(f => f != null).Distinct())
                        if (_amiClients.TryGetValue(gw.id, out var client) && client.Connected)
                            lock (client)
                                client.GetStream().Write(Encoding.UTF8.GetBytes($"Action: {flavor.ShowDevicesAction}\r\nActionID: {RadioActionId}\r\n\r\n"));
                }
                catch { }
                await Task.Delay(RadioPollSeconds * 1000);
            }
        }

        // AMI packets of the poll: the device entries, or an error reply (no permission).
        private static bool HandleRadioEvent(Dictionary<string, string> evt, Gateway gw)
        {
            if (evt.GetValueOrDefault("ActionID") != RadioActionId) return false;
            if (evt.GetValueOrDefault("Response") == "Error")
            {
                if (_radioDenied.TryAdd(gw.id, true))
                    Console.WriteLine($"AMI ({gw.id}): ShowDevices refused ({evt.GetValueOrDefault("Message", "")}); " +
                                      "line monitoring is off until a restart. The AMI user needs 'reporting' in 'write'.");
                return true;
            }
            if (!Modems.All.Any(f => evt.GetValueOrDefault("Event") == f.DeviceEntryEvent)) return true;

            string device = evt.GetValueOrDefault("Device", "");
            var ch = getChannelByLine(gw.id, device);
            if (ch == null) return true;

            var r = _radio.GetOrAdd(gw.id + "|" + device, _ => new RadioState());
            r.State = evt.GetValueOrDefault("State");
            r.Registration = evt.GetValueOrDefault("GSMRegistrationStatus");
            r.Provider = evt.GetValueOrDefault("ProviderName") is { } p && p != "NONE" ? ProviderName(p) : null;
            r.Act = ParseIntOr(evt, "AccessTechnology", -1);
            // The submode is the more exact one ("HSDPA" within "WCDMA"), when the driver knows it.
            r.Mode = RadioState.NetOfMode(evt.GetValueOrDefault("Submode")) != null ? evt["Submode"] : evt.GetValueOrDefault("Mode");
            // "RSSI: 29, -55 dBm"
            r.Rssi = int.TryParse(evt.GetValueOrDefault("RSSI", "").Split(',')[0].Trim(), out var rssi) ? rssi : 99;
            r.Rsrp = ParseIntOr(evt, "RSRP", int.MinValue) is var rsrp && rsrp != int.MinValue ? rsrp : null;
            r.Rsrq = ParseIntOr(evt, "RSRQ", int.MinValue) is var rsrq && rsrq != int.MinValue ? rsrq : null;
            r.Sinr = ParseIntOr(evt, "SINR", int.MinValue) is var sinr && sinr != int.MinValue ? sinr : null;
            r.Lac = evt.GetValueOrDefault("LocationAreaCode");
            r.Cell = evt.GetValueOrDefault("CellID");
            r.Number = evt.GetValueOrDefault("SubscriberNumber") is { } n && n != "Unknown" && n != "" ? n : null;
            r.Model = evt.GetValueOrDefault("Model") is { } m && m != "" && m != "Unknown" ? m : null;
            r.DataPort = evt.GetValueOrDefault("DataSetting") is { } dp && dp != "" ? dp : null;
            r.UpdatedAt = DateTime.Now;
            CheckRadioAlert(ch, r);
            return true;
        }

        // One message when the line has had no network for monitor.alert_minutes, one when it is back.
        private static void CheckRadioAlert(Channel ch, RadioState r)
        {
            int minutes = settings.monitor?.alertMinutes ?? 0;
            var l = L10n.Tg;
            if (r.Up)
            {
                if (r.Alerted)
                {
                    int down = (int)Math.Round((DateTime.Now - r.DownSince.Value).TotalMinutes);
                    Console.WriteLine($"line {ch.name}: network is back after {down} min");
                    EnqueueTelegramForLine(ch, l.T("tg.radio.back", ch.name, down));
                    EnqueueNtfy(ch, l.T("tg.radio.back.title", ch.name), l.T("tg.radio.back.text", down));
                }
                r.DownSince = null;
                r.Alerted = false;
                return;
            }

            r.DownSince ??= DateTime.Now;
            if (r.Alerted || minutes <= 0 || DateTime.Now - r.DownSince.Value < TimeSpan.FromMinutes(minutes)) return;
            r.Alerted = true;
            string since = r.DownSince.Value.ToString("HH:mm");
            // The driver state says more when the device itself is down ("Not connected").
            string state = (r.State is null or "Free" ? r.Registration : r.State) ?? "";
            Console.WriteLine($"line {ch.name}: no network since {since} ({r.State}, {r.Registration})");
            EnqueueTelegramForLine(ch, l.T("tg.radio.lost", ch.name, since, state));
            EnqueueNtfy(ch, l.T("tg.radio.lost.title", ch.name), l.T("tg.radio.lost.text", since, state));
        }

        // "MTS · 4G · ▂▄▆█ -55 dBm" for /status; "no network 5 min" when down.
        internal static string RadioSummary(RadioState r)
        {
            var l = L10n.Tg;
            if (!r.Up)
                return l.T("tg.radio.down", r.DownSince == null ? 0 : (int)(DateTime.Now - r.DownSince.Value).TotalMinutes);
            var parts = new List<string>();
            if (r.Provider != null) parts.Add(r.Provider);
            if (r.Net != null) parts.Add(r.Net);
            if (r.Rsrp != null) parts.Add($"{SignalBars(r.Bars)} RSRP {r.Rsrp} dBm, RSRQ {r.Rsrq} dB, SINR {r.Sinr} dB");
            else if (r.Dbm != null) parts.Add(SignalBars(r.Bars) + " " + r.Dbm + " dBm");
            return string.Join(" · ", parts);
        }

        // Some SIMs give the name twice, short and long ("beeline Beeline"): keep one of a repeated word.
        private static string ProviderName(string name)
        {
            var words = new List<string>();
            foreach (var w in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (words.Count > 0 && string.Equals(words[^1], w, StringComparison.OrdinalIgnoreCase))
                    words[^1] = w;
                else
                    words.Add(w);
            return string.Join(" ", words);
        }

        private static string SignalBars(int bars) =>
            string.Concat("▂▄▆█".Select((c, i) => i < bars ? c : '·'));
    }
}
