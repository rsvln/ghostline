using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace ghostline
{
    // pbx.mode: managed. Applying the generated files (PbxConfig), the phones' registrations, the
    // SIP security events and the TLS certificate. Settings are read from the file on every call, so
    // the PBX tab works with what was just saved, without a restart.
    internal partial class Program
    {
        internal static string ConfigPath { get; set; }

        internal static Settings ReadSettingsFile() => SettingsFile.Parse(File.ReadAllText(ConfigPath));

        // ---- AMI commands with a reply -----------------------------------------------------------------

        private static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _amiPending = new();
        private static int _amiActionSeq;

        // Runs a CLI command over the gateway's AMI connection and returns its output.
        internal static async Task<string> AmiCommand(string gatewayId, string command, int timeoutMs = 15000)
        {
            if (!_amiClients.TryGetValue(gatewayId, out var client) || !client.Connected)
                throw new InvalidOperationException($"AMI of gateway '{gatewayId}' is not connected");
            string id = "gl-cmd-" + Interlocked.Increment(ref _amiActionSeq);
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _amiPending[id] = tcs;
            try
            {
                lock (client)
                    client.GetStream().Write(Encoding.UTF8.GetBytes($"Action: Command\r\nActionID: {id}\r\nCommand: {command}\r\n\r\n"));
                var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
                if (done != tcs.Task) throw new TimeoutException($"no reply to '{command}'");
                return await tcs.Task;
            }
            finally
            {
                _amiPending.TryRemove(id, out _);
            }
        }

        // A reply to AmiCommand: "Output:" lines are kept in order (ParseAmiEvent keeps only the last one).
        private static bool HandleAmiReply(string block)
        {
            var m = Regex.Match(block, @"^ActionID: (gl-cmd-\d+)\r?$", RegexOptions.Multiline);
            if (!m.Success || !_amiPending.TryGetValue(m.Groups[1].Value, out var tcs)) return false;
            var output = new StringBuilder();
            foreach (var line in block.Replace("\r", "").Split('\n'))
            {
                if (line.StartsWith("Output: ")) output.Append(line, 8, line.Length - 8).Append('\n');
                else if (line.StartsWith("Message: ") && line != "Message: Command output follows")
                    output.Append(line, 9, line.Length - 9).Append('\n');
            }
            tcs.TrySetResult(output.ToString().TrimEnd());
            return true;
        }

        // ---- Apply ---------------------------------------------------------------------------------------

        internal record PbxApplyResult(bool ok, List<string> errors, List<string> changed, string output);

        private static readonly SemaphoreSlim _pbxApplyLock = new(1, 1);

        // Validates, writes the files that changed (the old ones go to .bak) and reloads Asterisk.
        internal static async Task<PbxApplyResult> ApplyPbx()
        {
            await _pbxApplyLock.WaitAsync();
            try
            {
                var s = ReadSettingsFile();
                var errors = PbxConfig.Validate(s);
                if (errors.Count > 0) return new(false, errors, new(), "");
                if (!Directory.Exists(s.pbx.confDir))
                    return new(false, new() { $"pbx.conf_dir {s.pbx.confDir} does not exist (is the shared directory mounted?)" }, new(), "");

                var changed = new List<string>();
                foreach (var (name, text) in PbxConfig.Generate(s))
                {
                    string path = Path.Combine(s.pbx.confDir, name);
                    if (File.Exists(path) && File.ReadAllText(path) == text) continue;
                    if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
                    File.WriteAllText(path + ".tmp", text);
                    File.Move(path + ".tmp", path, overwrite: true);
                    changed.Add(name);
                }

                var output = new StringBuilder();
                if (changed.Count > 0)
                {
                    Console.WriteLine("PBX: wrote " + string.Join(", ", changed) + ", reloading");
                    var cmds = new List<string>();
                    if (changed.Contains(PbxConfig.PjsipFile)) cmds.Add("module reload res_pjsip.so");
                    // Before the modem reload: a modem's new context must exist when it comes up.
                    if (changed.Contains(PbxConfig.DialplanFile)) cmds.Add("dialplan reload");
                    // "gracefully": the driver waits until its calls end, then re-reads the devices.
                    if (changed.Contains(PbxConfig.QuectelFile)) cmds.Add("quectel reload gracefully");
                    if (changed.Contains(PbxConfig.DongleFile)) cmds.Add("dongle reload gracefully");
                    foreach (var cmd in cmds)
                        output.Append("> ").Append(cmd).Append('\n').Append(await AmiCommand(s.pbx.asterisk, cmd)).Append('\n');
                }
                return new(true, new(), changed, output.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return new(false, new() { ex.Message }, new(), "");
            }
            finally
            {
                _pbxApplyLock.Release();
            }
        }

        // The generated files as they would be written now, for the preview on the PBX tab.
        internal static (List<string> errors, SortedDictionary<string, string> files) PreviewPbx()
        {
            var s = ReadSettingsFile();
            var errors = PbxConfig.Validate(s);
            // A missing secret is shown as an error but does not stop the preview.
            bool onlySecrets = errors.All(e => e.Contains("no phone secret"));
            return (errors, errors.Count == 0 || onlySecrets ? PbxConfig.Generate(s) : new());
        }

        // A new secret for the line's phone, written into the config. Returns it once; it is not logged.
        internal static string NewPhoneSecret(string lineName)
        {
            var s = ReadSettingsFile();
            var ch = s.channels?.FirstOrDefault(c => c.name == lineName);
            if (ch?.phone == null || string.IsNullOrWhiteSpace(ch.phone.extension)) return null;
            string secret = PbxConfig.NewSecret();
            string yaml = ConfigYaml.Set(File.ReadAllText(ConfigPath), $"channels[name={lineName}].phone.secret", secret);
            SettingsFile.Parse(yaml);
            File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true);
            File.WriteAllText(ConfigPath, yaml);
            Console.WriteLine($"PBX: new phone secret for line {lineName} (apply to use it)");
            return secret;
        }

        // ---- Editing on the PBX tab ----------------------------------------------------------------------

        internal record PbxLineEdit(string line, string extension, string dial, string encryption);
        internal record PbxSipEdit(int? tlsPort, int? udpPort, string externalAddress, string localNets, string cert, string key);
        internal record PbxEdit(PbxSipEdit sip, List<PbxLineEdit> lines, bool dryRun);
        internal record PbxSaveResult(bool ok, List<string> errors, Dictionary<string, string> secrets, SortedDictionary<string, string> files);

        private static readonly object _pbxSaveLock = new();

        // Writes the PBX tab's changes into the config (comments kept, the old file goes to .bak). An empty
        // extension removes the line's phone; a new phone gets a secret, returned once. With dryRun nothing
        // is written and the files are returned as they would be generated.
        internal static PbxSaveResult SavePbx(PbxEdit e)
        {
            lock (_pbxSaveLock)
            {
                string yaml = File.ReadAllText(ConfigPath);
                var before = SettingsFile.Parse(yaml);
                var errors = new List<string>();
                var secrets = new Dictionary<string, string>();

                if (e?.sip is { } sip)
                {
                    if (sip.tlsPort != null) yaml = ConfigYaml.Set(yaml, "pbx.tls.port", sip.tlsPort.ToString());
                    if (sip.udpPort != null) yaml = ConfigYaml.Set(yaml, "pbx.udp_port", sip.udpPort.ToString());
                    if (sip.externalAddress != null) yaml = ConfigYaml.Set(yaml, "pbx.external_address", sip.externalAddress.Trim());
                    if (sip.localNets != null) yaml = ConfigYaml.Set(yaml, "pbx.local_nets", sip.localNets.Trim());
                    if (sip.cert != null) yaml = ConfigYaml.Set(yaml, "pbx.tls.cert", sip.cert.Trim());
                    if (sip.key != null) yaml = ConfigYaml.Set(yaml, "pbx.tls.key", sip.key.Trim());
                }

                foreach (var l in e?.lines ?? new())
                {
                    var ch = before.channels?.FirstOrDefault(c => c.name == l.line);
                    if (ch == null || !ConfigYaml.NameOk.IsMatch(l.line ?? ""))
                    {
                        errors.Add($"no line '{l.line}'");
                        continue;
                    }
                    string at = $"channels[name={l.line}].phone";
                    string ext = l.extension?.Trim() ?? "";
                    if (ext == "")
                    {
                        if (ch.phone != null) yaml = ConfigYaml.RemoveSection(yaml, at);
                        continue;
                    }
                    yaml = ConfigYaml.Set(yaml, at + ".extension", ext);
                    if (l.dial != null) yaml = ConfigYaml.Set(yaml, at + ".dial", l.dial);
                    if (l.encryption != null) yaml = ConfigYaml.Set(yaml, at + ".encryption", l.encryption);
                    if (string.IsNullOrEmpty(ch.phone?.secret))
                    {
                        secrets[l.line] = PbxConfig.NewSecret();
                        yaml = ConfigYaml.Set(yaml, at + ".secret", secrets[l.line]);
                    }
                }
                if (errors.Count > 0) return new(false, errors, new(), null);

                Settings s;
                try { s = SettingsFile.Parse(yaml); }
                catch (Exception ex) { return new(false, new() { SettingsFile.Describe(ex) }, new(), null); }
                errors = PbxConfig.Validate(s);
                if (errors.Count > 0) return new(false, errors, new(), null);
                if (e.dryRun) return new(true, errors, new(), PbxConfig.Generate(s));

                File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true);
                // In place, not via a temp file: the config keeps its mode (it holds secrets).
                File.WriteAllText(ConfigPath, yaml);
                Console.WriteLine("PBX: settings saved from the PBX tab"
                    + (secrets.Count > 0 ? " (new phone secret for " + string.Join(", ", secrets.Keys) + ")" : ""));
                return new(true, errors, secrets, null);
            }
        }

        // ---- Lines added and removed on the PBX tab ---------------------------------------------------------

        internal record PbxLineAdd(string name, string gateway, string device, string extension, string dial, string encryption);
        internal record PbxLineResult(bool ok, string error, string secret);

        private static readonly Regex DeviceName = new(@"^[A-Za-z0-9_-]{1,32}$");

        // A new line on a modem gateway of the managed Asterisk, optionally with its phone. The modem itself
        // is described in quectel.conf / dongle.conf by hand. The caller restarts the service: the lines
        // are read at start.
        internal static PbxLineResult AddLine(PbxLineAdd a)
        {
            lock (_pbxSaveLock)
            {
                string yaml = File.ReadAllText(ConfigPath);
                var s = SettingsFile.Parse(yaml);
                string name = a?.name?.Trim() ?? "", device = a?.device?.Trim() ?? "", ext = a?.extension?.Trim() ?? "";
                if (!ConfigYaml.NameOk.IsMatch(name)) return new(false, "the line name: letters, digits, _ . - only", null);
                if (s.channels?.Any(c => c.name == name) == true) return new(false, $"line {name} already exists", null);
                var gw = s.gateways?.FirstOrDefault(g => g.id == a.gateway);
                if (gw == null || Modems.For(gw.type) == null) return new(false, "the gateway must be a quectel or dongle gateway", null);
                if (!PbxConfig.OnManagedAsterisk(s, new Channel { gateway = gw.id }))
                    return new(false, $"gateway {gw.id} is not on the managed Asterisk (pbx.asterisk)", null);
                if (!DeviceName.IsMatch(device)) return new(false, "the device: its name in quectel.conf / dongle.conf, like gsm3", null);
                if (s.channels?.Any(c => c.line == device && s.gateways.FirstOrDefault(g => g.id == c.gateway)?.ip == gw.ip) == true)
                    return new(false, $"device {device} already has a line", null);

                string at = $"channels[name={name}]";
                yaml = ConfigYaml.Set(yaml, at + ".gateway", gw.id);
                yaml = ConfigYaml.Set(yaml, at + ".pattern", device);
                yaml = ConfigYaml.Set(yaml, at + ".line", device);
                string secret = null;
                if (ext != "")
                {
                    secret = PbxConfig.NewSecret();
                    yaml = ConfigYaml.Set(yaml, at + ".phone.extension", ext);
                    yaml = ConfigYaml.Set(yaml, at + ".phone.dial", string.IsNullOrEmpty(a.dial) ? "ru" : a.dial);
                    yaml = ConfigYaml.Set(yaml, at + ".phone.encryption", string.IsNullOrEmpty(a.encryption) ? "sdes" : a.encryption);
                    yaml = ConfigYaml.Set(yaml, at + ".phone.secret", secret);
                }

                Settings after;
                try { after = SettingsFile.Parse(yaml); }
                catch (Exception ex) { return new(false, SettingsFile.Describe(ex), null); }
                if (after.channels?.Any(c => c.name == name && c.line == device) != true)
                    return new(false, "could not add the line to the config", null);
                var errors = PbxConfig.Validate(after);
                if (errors.Count > 0) return new(false, string.Join("; ", errors), null);

                File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true);
                File.WriteAllText(ConfigPath, yaml);
                Console.WriteLine($"PBX: line {name} ({gw.id}/{device}) added from the PBX tab");
                return new(true, null, secret);
            }
        }

        // Removes a line of the managed Asterisk (its phone with it). The caller applies and restarts.
        internal static PbxLineResult RemoveLine(string name)
        {
            lock (_pbxSaveLock)
            {
                string yaml = File.ReadAllText(ConfigPath);
                var s = SettingsFile.Parse(yaml);
                var ch = s.channels?.FirstOrDefault(c => c.name == name);
                if (ch == null || !ConfigYaml.NameOk.IsMatch(name)) return new(false, $"no line '{name}'", null);
                if (!PbxConfig.OnManagedAsterisk(s, ch)) return new(false, $"line {name} is on another PBX", null);
                yaml = ConfigYaml.Remove(yaml, $"channels[name={name}]");
                try
                {
                    if (SettingsFile.Parse(yaml).channels?.Any(c => c.name == name) == true)
                        return new(false, "could not remove the line from the config", null);
                }
                catch (Exception ex) { return new(false, SettingsFile.Describe(ex), null); }

                File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true);
                File.WriteAllText(ConfigPath, yaml);
                Console.WriteLine($"PBX: line {name} removed from the PBX tab");
                return new(true, null, null);
            }
        }

        // ---- USB modems on the Asterisk host (usb.json from scripts/gl-usb-inventory.py) ----------------

        internal record UsbPort(string path, string tty, int @interface, bool access);
        internal record UsbSound(string card, string alsadev, bool access);
        internal record UsbDevice(string usb, string vid, string pid, string manufacturer, string product,
                                  List<UsbPort> ports, List<UsbSound> sound);
        internal record UsbInventory(List<UsbDevice> devices);

        internal static (List<UsbDevice> devices, DateTime? at) ReadUsbInventory(Settings s)
        {
            string path = Path.Combine(s.pbx?.confDir ?? "", "usb.json");
            if (string.IsNullOrEmpty(s.pbx?.confDir) || !File.Exists(path)) return (null, null);
            try
            {
                var inv = System.Text.Json.JsonSerializer.Deserialize<UsbInventory>(File.ReadAllText(path));
                return (inv?.devices ?? new(), File.GetLastWriteTime(path));
            }
            catch { return (null, null); }
        }

        internal record PbxModemEdit(string action, string name, string driver, string data, string audio, string alsadev, bool noqpcmv);
        internal record PbxModemResult(bool ok, string error, bool restart);

        // Adds or removes a modem in pbx.modems. Only ports the inventory lists and Asterisk may open are
        // accepted, and no port of another modem. A modem needs a gateway of its driver on the managed
        // Asterisk (ghostline handles that driver's SMS and monitoring through it); a missing one is added
        // with the AMI login of pbx.asterisk, and then the service has to restart.
        internal static PbxModemResult EditModem(PbxModemEdit e)
        {
            lock (_pbxSaveLock)
            {
                string yaml = File.ReadAllText(ConfigPath);
                var s = SettingsFile.Parse(yaml);
                string name = e?.name?.Trim() ?? "";
                var modems = s.pbx.modems ?? new();
                bool restart = false;

                if (e?.action == "remove")
                {
                    if (!modems.Any(m => m.name == name)) return new(false, $"no modem '{name}'", false);
                    var user = s.channels?.FirstOrDefault(c => c.line == name && PbxConfig.OnManagedAsterisk(s, c));
                    if (user != null) return new(false, $"line {user.name} uses modem {name}: remove the line first", false);
                    yaml = ConfigYaml.Remove(yaml, $"pbx.modems[name={name}]");
                }
                else if (e?.action == "add")
                {
                    if (!DeviceName.IsMatch(name)) return new(false, "the modem name: letters, digits, _ and - only, like gsm3", false);
                    if (modems.Any(m => m.name == name)) return new(false, $"modem {name} already exists", false);
                    if (!PbxConfig.Drivers.Contains(e.driver)) return new(false, "driver must be quectel or dongle", false);
                    var (devices, _) = ReadUsbInventory(s);
                    if (devices == null) return new(false, "no USB list from the Asterisk host (usb.json in conf_dir)", false);
                    var ports = devices.SelectMany(d => d.ports).ToList();
                    var taken = modems.SelectMany(m => new[] { m.data, m.audio, m.alsadev }).Where(x => !string.IsNullOrEmpty(x)).ToHashSet();
                    foreach (var port in new[] { e.data, e.audio }.Where(x => !string.IsNullOrEmpty(x)))
                    {
                        var p = ports.FirstOrDefault(x => x.path == port);
                        if (p == null) return new(false, $"{port} is not on the Asterisk host", false);
                        if (!p.access) return new(false, $"Asterisk may not open {port} (not passed to its container)", false);
                        if (taken.Contains(port)) return new(false, $"{port} is used by another modem", false);
                    }
                    if (!string.IsNullOrEmpty(e.alsadev))
                    {
                        var snd = devices.SelectMany(d => d.sound).FirstOrDefault(x => x.alsadev == e.alsadev);
                        if (snd == null || !snd.access) return new(false, $"Asterisk may not open the sound card {e.alsadev}", false);
                        if (taken.Contains(e.alsadev)) return new(false, $"{e.alsadev} is used by another modem", false);
                    }

                    // A new list item: its first key is the selector, the rest follow.
                    string at = $"pbx.modems[name={name}]";
                    yaml = ConfigYaml.Set(yaml, at + ".driver", e.driver);
                    yaml = ConfigYaml.Set(yaml, at + ".data", e.data ?? "");
                    if (!string.IsNullOrEmpty(e.audio)) yaml = ConfigYaml.Set(yaml, at + ".audio", e.audio);
                    if (!string.IsNullOrEmpty(e.alsadev)) yaml = ConfigYaml.Set(yaml, at + ".alsadev", e.alsadev);
                    if (e.noqpcmv) yaml = ConfigYaml.Set(yaml, at + ".noqpcmv", "true");

                    var host = s.gateways?.FirstOrDefault(g => g.id == s.pbx.asterisk);
                    if (host != null && !(s.gateways.Any(g => g.type == e.driver && g.ip == host.ip)))
                    {
                        string gid = $"{host.id}-{e.driver}";
                        yaml = ConfigYaml.Set(yaml, $"gateways[id={gid}].type", e.driver);
                        yaml = ConfigYaml.Set(yaml, $"gateways[id={gid}].ip", host.ip);
                        yaml = ConfigYaml.Set(yaml, $"gateways[id={gid}].ami_port", host.amiPort.ToString());
                        yaml = ConfigYaml.Set(yaml, $"gateways[id={gid}].user", host.user);
                        yaml = ConfigYaml.Set(yaml, $"gateways[id={gid}].password", host.password);
                        restart = true;
                    }
                }
                else return new(false, "unknown action", false);

                Settings after;
                try { after = SettingsFile.Parse(yaml); }
                catch (Exception ex) { return new(false, SettingsFile.Describe(ex), false); }
                bool present = after.pbx.modems?.Any(m => m.name == name) == true;
                if (present != (e.action == "add")) return new(false, "could not change pbx.modems in the config", false);
                var errors = PbxConfig.Validate(after);
                if (errors.Count > 0) return new(false, string.Join("; ", errors), false);

                File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true);
                File.WriteAllText(ConfigPath, yaml);
                Console.WriteLine($"PBX: modem {name} {(e.action == "add" ? "added" : "removed")} from the PBX tab");
                return new(true, null, restart);
            }
        }

        // ---- Modems the managed Asterisk knows ------------------------------------------------------------

        internal record AsteriskModem(string gateway, string device, string state, string provider, string model);

        // "quectel show devices" / "dongle show devices" of every modem gateway on the managed Asterisk.
        // Only the driver's state is read; nothing is sent to the modems. IMEI, IMSI and the number are
        // in that table too and are left out.
        internal static async Task<List<AsteriskModem>> AsteriskModems(Settings s)
        {
            var list = new List<AsteriskModem>();
            var seen = new HashSet<string>();
            foreach (var gw in s.gateways ?? new())
            {
                if (Modems.For(gw.type) == null || !PbxConfig.OnManagedAsterisk(s, new Channel { gateway = gw.id })) continue;
                if (!seen.Add(gw.type)) continue;   // one driver, one table
                string text;
                try { text = await AmiCommand(gw.id, gw.type + " show devices"); }
                catch { continue; }
                list.AddRange(ParseDeviceTable(text).Select(r => new AsteriskModem(gw.id, r.id, r.state, r.provider, r.model)));
            }
            return list;
        }

        // A fixed-width table: the columns are cut at the header's positions (the provider has spaces).
        internal static List<(string id, string state, string provider, string model)> ParseDeviceTable(string text)
        {
            var result = new List<(string, string, string, string)>();
            var lines = text.Replace("\r", "").Split('\n');
            int h = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^ID\s+Group\s+State"));
            if (h < 0) return result;
            string head = lines[h];
            int Col(string name) => head.IndexOf(name, StringComparison.Ordinal);
            int cState = Col("State"), cRssi = Col("RSSI"), cProv = Col("Provider"), cModel = Col("Model"), cFw = Col("Firmware");
            string Cut(string l, int from, int to) =>
                from < 0 || from >= l.Length ? "" : l.Substring(from, Math.Max(0, Math.Min(to < 0 ? l.Length : to, l.Length) - from)).Trim();
            foreach (var l in lines.Skip(h + 1))
            {
                var m = Regex.Match(l, @"^([A-Za-z0-9_-]+)\s");
                if (!m.Success || m.Groups[1].Value == "ID") continue;
                result.Add((m.Groups[1].Value, Cut(l, cState, cRssi), Cut(l, cProv, cModel), Cut(l, cModel, cFw)));
            }
            return result;
        }

        // ---- Registrations -------------------------------------------------------------------------------

        internal record PhoneContact(string extension, string uri, string status, double? rttMs);

        // "  Contact:  11/sip:11@203.0.113.5:46334;transport=TLS 3a2dbee345 Avail       270.036"
        private static readonly Regex ContactLine = new(@"^\s*Contact:\s+([^/\s]+)/(\S+)\s+\S+\s+(\S+)\s+([\d.]+|nan)?", RegexOptions.Multiline);

        internal static async Task<List<PhoneContact>> PhoneContacts(string gatewayId)
        {
            string text = await AmiCommand(gatewayId, "pjsip show contacts");
            var list = new List<PhoneContact>();
            foreach (Match m in ContactLine.Matches(text))
            {
                if (m.Groups[1].Value.StartsWith("<")) continue;   // the header line
                double? rtt = double.TryParse(m.Groups[4].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : null;
                list.Add(new(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, rtt));
            }
            return list;
        }

        // ---- SIP security events -------------------------------------------------------------------------

        internal record SecurityEvent(DateTime at, string kind, string address, string account);

        private static readonly ConcurrentQueue<SecurityEvent> _securityEvents = new();
        private static readonly ConcurrentDictionary<string, DateTime> _securityAlerted = new();
        private const int SecurityAlertCount = 5;          // failures from one address...
        private static readonly TimeSpan SecurityWindow = TimeSpan.FromMinutes(10);   // ...within this time

        internal static IEnumerable<SecurityEvent> RecentSecurityEvents => _securityEvents.Reverse();

        // AMI security class (the AMI user needs "security" in "read"): failed registrations and calls.
        private static bool HandleSecurityEvent(Dictionary<string, string> evt, Gateway gw)
        {
            string kind = evt.GetValueOrDefault("Event");
            if (kind is not ("InvalidPassword" or "ChallengeResponseFailed" or "InvalidAccountID" or "FailedACL")) return false;
            if (settings.pbx?.Managed != true || gw.id != settings.pbx.asterisk) return true;

            // RemoteAddress: "IPV4/TLS/203.0.113.5/46334"
            var parts = evt.GetValueOrDefault("RemoteAddress", "").Split('/');
            string address = parts.Length >= 3 ? parts[2] : evt.GetValueOrDefault("RemoteAddress", "?");
            var e = new SecurityEvent(DateTime.Now, kind, address, evt.GetValueOrDefault("AccountID", ""));
            _securityEvents.Enqueue(e);
            while (_securityEvents.Count > 300) _securityEvents.TryDequeue(out _);

            int recent = _securityEvents.Count(x => x.address == address && DateTime.Now - x.at < SecurityWindow);
            if (recent >= SecurityAlertCount
                && (!_securityAlerted.TryGetValue(address, out var last) || DateTime.Now - last > TimeSpan.FromHours(24)))
            {
                _securityAlerted[address] = DateTime.Now;
                Console.WriteLine($"PBX: {recent} failed SIP attempts from {address} in {SecurityWindow.TotalMinutes} min");
                EnqueueAdminAlert(L10n.Tg.T("tg.pbx.attack.title"), L10n.Tg.T("tg.pbx.attack.text", address, recent, e.account));
            }
            return true;
        }

        // To the common Telegram chats and the common ntfy topic (not a line's): PBX-wide events.
        private static void EnqueueAdminAlert(string title, string text)
        {
            if (TelegramEnabled)
                foreach (var chat in settings.clients.telegram.chatIds ?? new())
                    Store.EnqueueTelegramMessage(chat, title + "\n" + text);
            if (NtfyEnabled && !string.IsNullOrWhiteSpace(settings.clients.ntfy.topic))
                EnqueueNtfyTopic(settings.clients.ntfy.topic, title, text);
        }

        // ---- TLS certificate -----------------------------------------------------------------------------

        internal record CertInfo(string subject, DateTime notAfter, int daysLeft);

        // Only a certificate inside conf_dir can be read here; null otherwise.
        internal static CertInfo PbxCertificate(Settings s)
        {
            string cert = s.pbx?.tls?.cert;
            if (string.IsNullOrWhiteSpace(cert) || cert.StartsWith("/") || string.IsNullOrWhiteSpace(s.pbx.confDir)) return null;
            string path = Path.Combine(s.pbx.confDir, cert);
            if (!File.Exists(path)) return null;
            try
            {
                var x = X509Certificate2.CreateFromPemFile(path);
                return new(x.Subject, x.NotAfter, (int)Math.Floor((x.NotAfter - DateTime.Now).TotalDays));
            }
            catch
            {
                try
                {
                    var x = X509CertificateLoader.LoadCertificateFromFile(path);
                    return new(x.Subject, x.NotAfter, (int)Math.Floor((x.NotAfter - DateTime.Now).TotalDays));
                }
                catch { return null; }
            }
        }

        // Once a day: a warning when the certificate expires within 14 days.
        private static async Task PbxCertWatchLoop()
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            while (true)
            {
                try
                {
                    if (PbxCertificate(ReadSettingsFile()) is { daysLeft: <= 14 } c)
                    {
                        Console.WriteLine($"PBX: the TLS certificate expires in {c.daysLeft} days ({c.notAfter:yyyy-MM-dd})");
                        EnqueueAdminAlert(L10n.Tg.T("tg.pbx.cert.title"), L10n.Tg.T("tg.pbx.cert.text", c.daysLeft, c.notAfter.ToString("yyyy-MM-dd")));
                    }
                }
                catch { }
                await Task.Delay(TimeSpan.FromDays(1));
            }
        }
    }
}
