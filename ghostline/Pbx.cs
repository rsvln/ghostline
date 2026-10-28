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
                    foreach (var cmd in new[] { "module reload res_pjsip.so", "dialplan reload" })
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
