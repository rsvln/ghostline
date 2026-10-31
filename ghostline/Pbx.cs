using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace ghostline
{
    // gateways[].pbx: managed. Applying the generated files (PbxConfig), the modems, the phones'
    // registrations, the SIP security events and the TLS certificate. Settings are read from the file on
    // every call, so what was just saved works without a restart.
    internal partial class Program
    {
        internal static string ConfigPath { get; set; }

        internal static Settings ReadSettingsFile() => SettingsFile.Parse(File.ReadAllText(ConfigPath));

        internal static Settings TryReadSettingsFile()
        {
            try { return ReadSettingsFile(); }
            catch { return null; }
        }

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
            var r = await ApplyPbxLocked();
            if (!r.ok) SysLog.Error("pbx", "Asterisk files not applied: " + string.Join("; ", r.errors));
            return r;
        }

        private static async Task<PbxApplyResult> ApplyPbxLocked()
        {
            await _pbxApplyLock.WaitAsync();
            try
            {
                var s = ReadSettingsFile();
                var errors = PbxConfig.Validate(s);
                if (errors.Count > 0) return new(false, errors, new(), "");
                var gw = s.ManagedGateway;
                string confDir = gw.managed.confDir;
                if (!Directory.Exists(confDir))
                    return new(false, new() { $"managed.conf_dir {confDir} does not exist (is the shared directory mounted?)" }, new(), "");

                var changed = new List<string>();
                foreach (var (name, text) in PbxConfig.Generate(s))
                {
                    string path = Path.Combine(confDir, name);
                    if (File.Exists(path) && File.ReadAllText(path) == text) continue;
                    if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
                    File.WriteAllText(path + ".tmp", text);
                    File.Move(path + ".tmp", path, overwrite: true);
                    changed.Add(name);
                }

                var output = new StringBuilder();
                if (changed.Count > 0)
                {
                    SysLog.Info("pbx", "wrote " + string.Join(", ", changed) + ", reloading");
                    var cmds = new List<string>();
                    if (changed.Contains(PbxConfig.PjsipFile)) cmds.Add("module reload res_pjsip.so");
                    // Before the modem reload: a modem's new context must exist when it comes up.
                    if (changed.Contains(PbxConfig.DialplanFile)) cmds.Add("dialplan reload");
                    // "gracefully": the driver waits until its calls end, then re-reads the devices.
                    if (changed.Contains(PbxConfig.QuectelFile)) cmds.Add("quectel reload gracefully");
                    if (changed.Contains(PbxConfig.DongleFile)) cmds.Add("dongle reload gracefully");
                    foreach (var cmd in cmds)
                        output.Append("> ").Append(cmd).Append('\n').Append(await AmiCommand(gw.id, cmd)).Append('\n');
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

        // The generated files as they would be written now, for the preview.
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
            var ch = s.lines?.FirstOrDefault(c => c.name == lineName);
            if (ch?.phone == null || string.IsNullOrWhiteSpace(ch.phone.extension)) return null;
            string secret = PbxConfig.NewSecret();
            string yaml = ConfigYaml.Set(File.ReadAllText(ConfigPath), $"lines[name={lineName}].phone.secret", secret);
            SettingsFile.Parse(yaml);
            File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true);
            File.WriteAllText(ConfigPath, yaml);
            SysLog.Info("pbx", $"new phone secret for line {lineName}");
            return secret;
        }

        // Phones of the managed Asterisk that have an extension but no secret yet get one, written into the
        // config. Returns line -> secret, to be shown once; nothing is logged but the line names.
        internal static Dictionary<string, string> AddMissingPhoneSecrets()
        {
            var secrets = new Dictionary<string, string>();
            lock (_pbxSaveLock)
            {
                string yaml = File.ReadAllText(ConfigPath);
                var s = SettingsFile.Parse(yaml);
                foreach (var ch in (s.lines ?? new()).Where(c => PbxConfig.OnManagedAsterisk(s, c)
                             && !string.IsNullOrWhiteSpace(c.phone?.extension) && string.IsNullOrEmpty(c.phone.secret)))
                {
                    secrets[ch.name] = PbxConfig.NewSecret();
                    yaml = ConfigYaml.Set(yaml, $"lines[name={ch.name}].phone.secret", secrets[ch.name]);
                }
                if (secrets.Count == 0) return secrets;
                SettingsFile.Parse(yaml);
                // In place, not via a temp file: the config keeps its mode (it holds secrets).
                File.WriteAllText(ConfigPath, yaml);
                SysLog.Info("pbx", "new phone secret for " + string.Join(", ", secrets.Keys));
            }
            return secrets;
        }

        private static readonly object _pbxSaveLock = new();
        private static readonly Regex DeviceName = new(@"^[A-Za-z0-9_-]{1,32}$");

        // ---- USB modems on the Asterisk host (usb.json from scripts/gl-usb-inventory.py) ----------------

        internal record UsbPort(string path, string tty, int @interface, bool access);
        internal record UsbSound(string card, string alsadev, bool access);
        internal record UsbDevice(string usb, string vid, string pid, string manufacturer, string product,
                                  List<UsbPort> ports, List<UsbSound> sound);
        internal record UsbInventory(List<UsbDevice> devices);

        internal static (List<UsbDevice> devices, DateTime? at) ReadUsbInventory(Settings s)
        {
            string confDir = s.ManagedGateway?.managed?.confDir;
            if (string.IsNullOrEmpty(confDir)) return (null, null);
            string path = Path.Combine(confDir, "usb.json");
            if (!File.Exists(path)) return (null, null);
            try
            {
                var inv = System.Text.Json.JsonSerializer.Deserialize<UsbInventory>(File.ReadAllText(path));
                return (inv?.devices ?? new(), File.GetLastWriteTime(path));
            }
            catch { return (null, null); }
        }

        internal record PbxModemEdit(string action, string gateway, string name, string driver, string data, string audio, string alsadev, bool noqpcmv);
        internal record PbxModemResult(bool ok, string error);

        // Adds or removes a modem of an Asterisk gateway. On the managed Asterisk only ports the inventory
        // lists and Asterisk may open are accepted, and no port of another modem; under FreePBX a modem is
        // only its name and driver (FreePBX owns the driver config). A modem a line uses stays.
        internal static PbxModemResult EditModem(PbxModemEdit e)
        {
            lock (_pbxSaveLock)
            {
                string yaml = File.ReadAllText(ConfigPath);
                var s = SettingsFile.Parse(yaml);
                var gw = s.gateways?.FirstOrDefault(g => g.id == e?.gateway);
                if (gw == null || !gw.IsAsterisk || !ConfigYaml.NameOk.IsMatch(gw.id)) return new(false, $"no Asterisk gateway '{e?.gateway}'");
                string name = e.name?.Trim() ?? "";
                var modems = gw.modems ?? new();
                string at = $"gateways[id={gw.id}].modems[name={name}]";

                if (e.action == "remove")
                {
                    if (!modems.Any(m => m.name == name)) return new(false, $"no modem '{name}'");
                    var user = s.lines?.FirstOrDefault(c => c.gateway == gw.id && c.modem == name);
                    if (user != null) return new(false, $"line {user.name} uses modem {name}: remove the line or move it to another modem first");
                    yaml = ConfigYaml.Remove(yaml, at);
                }
                else if (e.action == "add")
                {
                    if (!DeviceName.IsMatch(name)) return new(false, "the modem name: letters, digits, _ and - only, like gsm3");
                    if (modems.Any(m => m.name == name)) return new(false, $"modem {name} already exists");
                    if (!PbxConfig.Drivers.Contains(e.driver)) return new(false, "driver must be quectel or dongle");
                    if (gw.Managed)
                    {
                        var (devices, _) = ReadUsbInventory(s);
                        if (devices == null) return new(false, "no USB list from the Asterisk host (usb.json in managed.conf_dir)");
                        var ports = devices.SelectMany(d => d.ports).ToList();
                        var taken = modems.SelectMany(m => new[] { m.data, m.audio, m.alsadev }).Where(x => !string.IsNullOrEmpty(x)).ToHashSet();
                        foreach (var port in new[] { e.data, e.audio }.Where(x => !string.IsNullOrEmpty(x)))
                        {
                            var p = ports.FirstOrDefault(x => x.path == port);
                            if (p == null) return new(false, $"{port} is not on the Asterisk host");
                            if (!p.access) return new(false, $"Asterisk may not open {port} (not passed to its container)");
                            if (taken.Contains(port)) return new(false, $"{port} is used by another modem");
                        }
                        if (!string.IsNullOrEmpty(e.alsadev))
                        {
                            var snd = devices.SelectMany(d => d.sound).FirstOrDefault(x => x.alsadev == e.alsadev);
                            if (snd == null || !snd.access) return new(false, $"Asterisk may not open the sound card {e.alsadev}");
                            if (taken.Contains(e.alsadev)) return new(false, $"{e.alsadev} is used by another modem");
                        }
                    }

                    // A new list item: its first key is the selector, the rest follow.
                    yaml = ConfigYaml.Set(yaml, at + ".driver", e.driver);
                    if (gw.Managed)
                    {
                        yaml = ConfigYaml.Set(yaml, at + ".data", e.data ?? "");
                        if (!string.IsNullOrEmpty(e.audio)) yaml = ConfigYaml.Set(yaml, at + ".audio", e.audio);
                        if (!string.IsNullOrEmpty(e.alsadev)) yaml = ConfigYaml.Set(yaml, at + ".alsadev", e.alsadev);
                        if (e.noqpcmv) yaml = ConfigYaml.Set(yaml, at + ".noqpcmv", "true");
                    }
                }
                else return new(false, "unknown action");

                Settings after;
                try { after = SettingsFile.Parse(yaml); }
                catch (Exception ex) { return new(false, SettingsFile.Describe(ex)); }
                bool present = after.gateways.First(g => g.id == gw.id).Modem(name) != null;
                if (present != (e.action == "add")) return new(false, "could not change the modems in the config");
                if (gw.Managed)
                {
                    var errors = PbxConfig.Validate(after);
                    if (errors.Count > 0) return new(false, string.Join("; ", errors));
                }

                File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true);
                File.WriteAllText(ConfigPath, yaml);
                SysLog.Info("pbx", $"modem {gw.id}/{name} {(e.action == "add" ? "added" : "removed")}");
                return new(true, null);
            }
        }

        // ---- Modems the managed Asterisk knows ------------------------------------------------------------

        internal record AsteriskModem(string gateway, string device, string state, string provider, string model);

        // "quectel show devices" / "dongle show devices" of every Asterisk gateway, for the drivers its modems
        // use. Only the driver's state is read; nothing is sent to the modems. IMEI, IMSI and the number
        // are in that table too and are left out.
        internal static async Task<List<AsteriskModem>> AsteriskModems(Settings s)
        {
            var list = new List<AsteriskModem>();
            foreach (var gw in (s.gateways ?? new()).Where(g => g.IsAsterisk))
                foreach (var driver in (gw.modems ?? new()).Select(m => m.driver).Where(d => Modems.For(d) != null).Distinct())
                {
                    string text;
                    try { text = await AmiCommand(gw.id, driver + " show devices", 5000); }
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
            if (settings.ManagedGateway?.id != gw.id) return true;

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
                SysLog.Warn("pbx", $"{recent} failed SIP attempts from {address} in {SecurityWindow.TotalMinutes} min");
                _ = Task.Run(() => AttackAlert(address));
            }
            return true;
        }

        // The message waits a little, so it can say whether fail2ban (it polls the same log) banned the address.
        private static async Task AttackAlert(string address)
        {
            await Task.Delay(TimeSpan.FromSeconds(45));
            try
            {
                var events = _securityEvents.Where(x => x.address == address && DateTime.Now - x.at < SecurityWindow).ToList();
                var l = L10n.Tg;
                var text = new List<string> { l.T("tg.pbx.attack.count", address, events.Count, (int)SecurityWindow.TotalMinutes) };

                // Accounts of the managed Asterisk: a wrong password on one of them is a real attempt,
                // an account that does not exist is a scanner guessing.
                var ext = PbxConfig.Lines(settings).ToDictionary(x => x.Phone.extension ?? "", x => x.Name);
                var hit = events.Where(x => ext.ContainsKey(x.account)).Select(x => x.account).Distinct().ToList();
                var other = events.Select(x => x.account).Where(a => !ext.ContainsKey(a) && !string.IsNullOrEmpty(a) && !a.StartsWith("<"))
                    .Distinct().ToList();
                foreach (var a in hit)
                    text.Add(l.T("tg.pbx.attack.own", a, ext[a]));
                if (other.Count > 0)
                    text.Add(l.T("tg.pbx.attack.scan", other.Count, string.Join(", ", other.Take(3)) + (other.Count > 3 ? ", …" : "")));

                var f2b = ReadFail2ban(settings);
                var ban = f2b?.banned?.FirstOrDefault(b => b.ip == address);
                text.Add(ban != null
                    ? l.T("tg.pbx.attack.banned", ban.until == null ? l.T("tg.pbx.attack.forever") : ban.until.Value.ToLocalTime().ToString("dd.MM HH:mm"),
                          ban.count ?? 1)
                    : l.T(f2b == null ? "tg.pbx.attack.no_f2b" : "tg.pbx.attack.not_banned"));
                EnqueueAdminAlert(l.T("tg.pbx.attack.title"), string.Join("\n", text));
            }
            catch (Exception ex) { SysLog.Error("pbx", "attack alert: " + ex.Message); }
        }

        // ---- fail2ban on the Asterisk host (fail2ban.json from scripts/gl-fail2ban.py) -------------------

        internal record Fail2banBan(string ip, DateTime? since, DateTime? until, int? count);
        internal record Fail2banStatus(DateTime at, List<string> errors, int? failed_now, int? total_failed, int? total_banned,
                                       int? max_retry, int? find_seconds, int? ban_seconds, bool? ban_growth, List<Fail2banBan> banned);

        // On start: the settings for the host helpers (gl-fail2ban, gl-acme) reach the host without
        // waiting for a save (Asterisk is not involved, so this needs no AMI and touches no other file).
        internal static void SyncHostFiles(Settings s)
        {
            var gw = s.ManagedGateway;
            if (gw == null || !Directory.Exists(gw.managed?.confDir)) return;
            var errors = PbxConfig.Validate(s);
            foreach (var (name, topic) in new[] { (PbxConfig.Fail2banFile, "fail2ban"), (PbxConfig.AcmeFile, "acme") })
            {
                try
                {
                    if (errors.Any(e => e.Contains(topic))) continue;
                    string path = Path.Combine(gw.managed.confDir, name);
                    string text = PbxConfig.Generate(s)[name];
                    if (File.Exists(path) && File.ReadAllText(path) == text) continue;
                    File.WriteAllText(path + ".tmp", text);
                    File.Move(path + ".tmp", path, overwrite: true);
                    SysLog.Info("pbx", "wrote " + name);
                }
                catch (Exception ex) { SysLog.Warn("pbx", name + " not written: " + ex.Message); }
            }
        }

        // null: no report from the host (gl-fail2ban not installed, or the shared directory is not there).
        internal static Fail2banStatus ReadFail2ban(Settings s)
        {
            string confDir = s?.ManagedGateway?.managed?.confDir;
            if (string.IsNullOrEmpty(confDir)) return null;
            try
            {
                string path = Path.Combine(confDir, "fail2ban.json");
                return File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<Fail2banStatus>(File.ReadAllText(path)) : null;
            }
            catch { return null; }
        }

        // Unban: a request file the host handles within seconds (a path unit) or at the next timer run.
        internal static string RequestUnban(string ip)
        {
            if (!System.Net.IPAddress.TryParse(ip ?? "", out var addr)) return "not an IP address";
            string confDir = settings?.ManagedGateway?.managed?.confDir;
            if (string.IsNullOrEmpty(confDir) || !Directory.Exists(confDir)) return "no managed.conf_dir";
            string path = Path.Combine(confDir, "fail2ban_request.json");
            lock (_securityAlerted)
            {
                var list = new List<string>();
                try
                {
                    if (File.Exists(path))
                        list = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("unban")
                            .EnumerateArray().Select(x => x.GetString()).ToList();
                }
                catch { }
                if (!list.Contains(addr.ToString())) list.Add(addr.ToString());
                File.WriteAllText(path + ".tmp", System.Text.Json.JsonSerializer.Serialize(new { unban = list }));
                File.Move(path + ".tmp", path, overwrite: true);
            }
            SysLog.Info("pbx", $"fail2ban: unban {addr} requested");
            return null;
        }

        // ---- Let's Encrypt on the Asterisk host (acme.json from scripts/gl-acme.py) -----------------------

        internal record AcmeStatus(DateTime at, bool enabled, string domain, DateTime? last_try, DateTime? last_ok,
                                   DateTime? next_renew, string error, DateTime? error_at);

        // null: no report from the host (gl-acme not installed, or the shared directory is not there).
        internal static AcmeStatus ReadAcme(Settings s)
        {
            string confDir = s?.ManagedGateway?.managed?.confDir;
            if (string.IsNullOrEmpty(confDir)) return null;
            try
            {
                string path = Path.Combine(confDir, "acme.json");
                return File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<AcmeStatus>(File.ReadAllText(path)) : null;
            }
            catch { return null; }
        }

        // Renew now: like unban, a request file for the host.
        internal static string RequestRenew()
        {
            var s = ReadSettingsFile();
            if (s.ManagedGateway?.managed?.sip?.tls?.acme?.enabled != true) return "sip.tls.acme is off";
            string confDir = s.ManagedGateway.managed.confDir;
            if (string.IsNullOrEmpty(confDir) || !Directory.Exists(confDir)) return "no managed.conf_dir";
            string path = Path.Combine(confDir, "acme_request.json");
            File.WriteAllText(path + ".tmp", "{\"renew\": true}\n");
            File.Move(path + ".tmp", path, overwrite: true);
            SysLog.Info("pbx", "acme: renewal requested");
            return null;
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

        internal record CertInfo(string subject, string issuer, DateTime notAfter, int daysLeft);

        // "self-signed", else the issuer's organization (Let's Encrypt), else its name.
        static string CertIssuer(X509Certificate2 x) =>
            x.Issuer == x.Subject ? "self-signed"
            : System.Text.RegularExpressions.Regex.Match(x.Issuer, @"(?:^|, )O=([^,]+)") is { Success: true } m ? m.Groups[1].Value
            : x.GetNameInfo(X509NameType.SimpleName, true);

        // Only a certificate inside conf_dir can be read here; null otherwise.
        internal static CertInfo PbxCertificate(Settings s)
        {
            var p = s.ManagedGateway?.managed;
            string cert = p?.sip?.tls?.cert;
            if (string.IsNullOrWhiteSpace(cert) || cert.StartsWith("/") || string.IsNullOrWhiteSpace(p.confDir)) return null;
            string path = Path.Combine(p.confDir, cert);
            if (!File.Exists(path)) return null;
            try
            {
                var x = X509Certificate2.CreateFromPemFile(path);
                return new(x.Subject, CertIssuer(x), x.NotAfter, (int)Math.Floor((x.NotAfter - DateTime.Now).TotalDays));
            }
            catch
            {
                try
                {
                    var x = X509CertificateLoader.LoadCertificateFromFile(path);
                    return new(x.Subject, CertIssuer(x), x.NotAfter, (int)Math.Floor((x.NotAfter - DateTime.Now).TotalDays));
                }
                catch { return null; }
            }
        }

        // Once a day: a warning when the certificate expires within 14 days. Every 5 minutes: a message
        // when gl-acme reports a new failure (once per failure).
        private static async Task PbxCertWatchLoop()
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            DateTime lastExpiryCheck = DateTime.MinValue;
            DateTime? alertedError = ReadAcme(settings)?.error_at;
            while (true)
            {
                try
                {
                    var s = ReadSettingsFile();
                    if (DateTime.Now - lastExpiryCheck > TimeSpan.FromDays(1))
                    {
                        lastExpiryCheck = DateTime.Now;
                        if (PbxCertificate(s) is { daysLeft: <= 14 } c)
                        {
                            SysLog.Warn("pbx", $"the TLS certificate expires in {c.daysLeft} days ({c.notAfter:yyyy-MM-dd})");
                            EnqueueAdminAlert(L10n.Tg.T("tg.pbx.cert.title"), L10n.Tg.T("tg.pbx.cert.text", c.daysLeft, c.notAfter.ToString("yyyy-MM-dd")));
                        }
                    }
                    if (ReadAcme(s) is { error.Length: > 0, error_at: { } at } a && at != alertedError)
                    {
                        alertedError = at;
                        SysLog.Warn("pbx", $"acme: {a.domain}: {a.error}");
                        EnqueueAdminAlert(L10n.Tg.T("tg.pbx.acme.title"), L10n.Tg.T("tg.pbx.acme.failed", a.domain, a.error));
                    }
                }
                catch { }
                await Task.Delay(TimeSpan.FromMinutes(5));
            }
        }
    }
}
