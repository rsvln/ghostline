namespace ghostline
{
    internal static class WebUi
    {
        public static void Start(string configPath)
        {
            Task.Run(() =>
            {
                try
                {
                    int port = Program.settings?.web?.port > 0 ? Program.settings.web.port : 8889;
                    var builder = WebApplication.CreateBuilder();
                    builder.WebHost.UseUrls("http://+:" + port);
                    builder.Logging.ClearProviders();
                    var app = builder.Build();

                    app.Use(async (context, next) =>
                    {
                        string? user = Program.settings?.web?.user;
                        string? pass = Program.settings?.web?.password;

                        if (string.IsNullOrEmpty(user) && string.IsNullOrEmpty(pass))
                        {
                            await next();
                            return;
                        }

                        string? authHeader = context.Request.Headers.Authorization;
                        if (authHeader != null && authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                        {
                            string decoded;
                            try
                            {
                                decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(authHeader.Substring("Basic ".Length).Trim()));
                            }
                            catch
                            {
                                decoded = "";
                            }
                            int sep = decoded.IndexOf(':');
                            if (sep >= 0 && decoded.Substring(0, sep) == user && decoded.Substring(sep + 1) == pass)
                            {
                                await next();
                                return;
                            }
                        }

                        context.Response.Headers.WWWAuthenticate = "Basic realm=\"ghostline\"";
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsync("Unauthorized");
                    });

                    // The page, at the address of every view (the script picks the view from the address).
                    foreach (var path in new[] { "/", "/calls", "/sms", "/log", "/system", "/status", "/config", "/config/yaml", "/about" })
                        app.MapGet(path, () => Results.Content(Localize(Asset("index.html")), "text/html; charset=utf-8"));
                    app.MapGet("/js/app.js", () => Results.Content(Localize(Asset("app.js")), "text/javascript; charset=utf-8"));
                    app.MapGet("/css/app.css", () => Results.Content(Asset("app.css"), "text/css; charset=utf-8"));
                    app.MapGet("/favicon.svg", () => Results.Content(Asset("favicon.svg"), "image/svg+xml"));

                    // YAML editor for the Config tab: a CodeMirror bundle served locally (no internet needed).
                    app.MapGet("/js/yaml-editor.js", () =>
                    {
                        string path = Path.Combine(AppContext.BaseDirectory, "web", "yaml-editor.js");
                        return File.Exists(path) ? Results.File(path, "text/javascript") : Results.NotFound();
                    });

                    // Version and README for the About tab.
                    app.MapGet("/api/about", () =>
                    {
                        string readmePath = Path.Combine(AppContext.BaseDirectory, "README.md");
                        string readme = File.Exists(readmePath)
                            ? Markdig.Markdown.ToHtml(File.ReadAllText(readmePath), Markdig.MarkdownExtensions.UseAdvancedExtensions(new Markdig.MarkdownPipelineBuilder()).Build())
                            : "";
                        return Results.Ok(new { version = VersionInfo.Version, build = VersionInfo.BuildDate, url = VersionInfo.ProjectUrl, readme });
                    });

                    app.MapGet("/api/channels", () =>
                    {
                        var list = (Program.settings?.lines ?? new List<Channel>())
                            .Select(c => new { c.name, gateway = c.gateway, type = Program.getGatewayById(c.gateway)?.type })
                            .ToList();
                        return Results.Ok(list);
                    });

                    app.MapGet("/api/status", () =>
                    {
                        var s = Program.settings;
                        bool full = s?.FullMode == true;
                        var components = (s?.gateways ?? new List<Gateway>())
                            .Select(g => Component(g.id, g.id, g.type, GatewayAddress(g), Modems.IsAmi(g.type)))
                            .ToList();
                        // Notification channels: only the ones switched on in the config.
                        if (Program.TelegramEnabled)
                            components.Add(Component("telegram", "Telegram", "telegram", null, true));
                        if (Program.NtfyEnabled)
                            components.Add(Component("ntfy", "ntfy", "ntfy", s.clients.ntfy.url, true));
                        if (full)
                        {
                            components.Add(Component("cdr", L10n.Web.T("web.status.cdr"), "cdr", Program.CdrSource(s.calls), true));
                            if (s.calls?.transcribe?.enabled == true)
                                components.Add(Component("transcribe", L10n.Web.T("web.status.transcribe"), "transcribe", s.calls.transcribe.url, true));
                        }
                        var sms = Store.GetStats();
                        var calls = full ? Store.CallStatsByChannel() : new();
                        var configured = (s?.lines ?? new List<Channel>()).Select(c => c.name).ToList();
                        // Configured lines first, then lines that are only in the history (renamed or removed).
                        var names = configured.Concat(sms.Select(x => x.Channel).Concat(calls.Select(x => x.channel))
                                                         .Where(n => !string.IsNullOrEmpty(n) && !configured.Contains(n)).Distinct());
                        var lines = names.Select(n =>
                        {
                            var ch = s?.lines?.FirstOrDefault(c => c.name == n);
                            var st = sms.FirstOrDefault(x => x.Channel == n);
                            var cs = calls.FirstOrDefault(x => x.channel == n);
                            return new
                            {
                                name = n, configured = ch != null, gateway = ch?.gateway, number = Program.LineNumber(ch),
                                smsIn = st?.In ?? 0, smsOut = st?.Out ?? 0,
                                callsIn = cs?.inAnswered ?? 0, callsMissed = cs?.inMissed ?? 0,
                                callsOut = (cs?.outAnswered ?? 0) + (cs?.outNoAnswer ?? 0), lastCall = cs?.last,
                                radio = ch == null ? null : Radio(ch)
                            };
                        }).ToList();
                        return Results.Ok(new
                        {
                            components,
                            lines,
                            queues = new
                            {
                                sms = Store.CountPendingOutgoingSms(),
                                telegram = Store.CountPendingTelegramMessages(),
                                calls = full ? CallsStatus() : null
                            },
                            version = VersionInfo.Version,
                            startedAt = Program.StartedAt
                        });
                    });

                    app.MapGet("/api/sms/list", (int? limit, string? direction, string? channel, string? text) =>
                    {
                        var rows = Store.Query(limit ?? 200, direction, channel, text);
                        return Results.Ok(rows);
                    });

                    app.MapGet("/api/sms/export", (int? limit, string? direction, string? channel, string? text) =>
                    {
                        var rows = Store.Query(limit ?? 100000, direction, channel, text);
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine("ts\tdirection\tgateway\tchannel\tpeer\tnote\tcontent");
                        foreach (var r in rows)
                            sb.AppendLine($"{r.Ts}\t{r.Direction}\t{r.Gateway}\t{r.Channel}\t{r.Peer}\t{r.Note}\t{r.Content.Replace("\n", "\\n").Replace("\r", "\\r")}");

                        var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
                        string fileName = "ghostline-sms-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt";
                        return Results.File(bytes, "text/plain; charset=utf-8", fileName);
                    });

                    app.MapPost("/api/sms/send", async (HttpRequest req) =>
                    {
                        using var reader = new StreamReader(req.Body);
                        var body = await reader.ReadToEndAsync();
                        var data = System.Text.Json.JsonSerializer.Deserialize<SendPayload>(body,
                            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (data == null || string.IsNullOrWhiteSpace(data.channel) || string.IsNullOrWhiteSpace(data.number) || string.IsNullOrWhiteSpace(data.text))
                            return Results.Ok(new { ok = false, error = "channel, number and text are required" });

                        var chan = Program.getChannelType(data.channel);
                        if (chan == null)
                            return Results.Ok(new { ok = false, error = "unknown channel: " + data.channel });

                        var (ok, error) = Program.QueueSmsSend(chan, data.number, data.text, "web-ui");
                        return Results.Ok(new { ok, error, queued = ok });
                    });

                    // --- Calls (mode "full") ---

                    app.MapGet("/api/mode", () => Results.Ok(new
                    {
                        mode = Program.settings?.FullMode == true ? "full" : "sms",
                        transcribe = Program.settings?.calls?.transcribe?.enabled == true,
                        pbx = Program.settings?.ManagedGateway?.id
                    }));

                    // --- PBX: Asterisk gateways, their modems, the phones of the managed one ---

                    // Live state for the Status tab. Read from the file, so a modem just added shows up.
                    app.MapGet("/api/status/pbx", async () =>
                    {
                        var s = Program.TryReadSettingsFile() ?? Program.settings;
                        var asterisks = (s?.gateways ?? new()).Where(g => g.IsAsterisk).ToList();
                        if (asterisks.Count == 0) return Results.Ok(new { gateways = Array.Empty<object>() });
                        var managed = s.ManagedGateway;

                        List<Program.AsteriskModem> known = new();
                        try { known = await Program.AsteriskModems(s); } catch { }
                        List<Program.PhoneContact> contacts = null;
                        string contactsError = null;
                        if (managed != null)
                        {
                            try { contacts = await Program.PhoneContacts(managed.id); }
                            catch (Exception ex) { contactsError = ex.Message; }
                        }
                        var (usb, usbAt) = Program.ReadUsbInventory(s);
                        var taken = (managed?.modems ?? new()).SelectMany(m => new[] { m.data, m.audio, m.alsadev })
                            .Where(x => !string.IsNullOrEmpty(x)).ToHashSet();
                        var cert = Program.PbxCertificate(s);

                        return Results.Ok(new
                        {
                            gateways = asterisks.Select(g => new
                            {
                                g.id, managed = g.Managed, address = GatewayAddress(g),
                                connected = HealthStatus.Find(g.id)?.Connected == true,
                                cert = g.Managed && cert != null ? new { cert.subject, notAfter = cert.notAfter.ToString("yyyy-MM-dd"), cert.daysLeft } : null,
                                modems = (g.modems ?? new()).Select(m =>
                                {
                                    var k = known.FirstOrDefault(x => x.gateway == g.id && x.device == m.name);
                                    var dev = g.Managed ? usb?.FirstOrDefault(u => u.ports.Any(p => p.path == m.data)) : null;
                                    return new
                                    {
                                        m.name, m.driver, found = k != null, state = k?.state, provider = k?.provider,
                                        model = dev == null ? k?.model : PbxConfig.Profile(dev.vid, dev.pid)?.Model ?? dev.product,
                                        usb = dev?.usb,
                                        line = s.lines?.FirstOrDefault(c => c.gateway == g.id && c.modem == m.name)?.name
                                    };
                                }),
                                // Devices the driver has that are not in the settings.
                                unknown = known.Where(x => x.gateway == g.id && g.Modem(x.device) == null)
                                    .Select(x => new { x.device, x.state, x.provider, x.model }),
                                // USB modems of the managed Asterisk's host that no modem uses yet.
                                usb = !g.Managed ? null : usb?.Where(u => !u.ports.Any(p => taken.Contains(p.path))).Select(u => new
                                {
                                    u.usb, u.vid, u.pid, u.product,
                                    model = PbxConfig.Profile(u.vid, u.pid)?.Model,
                                    access = u.ports.Any(p => p.access)
                                }),
                                usbAt = g.Managed ? usbAt : null,
                                errors = g.Managed ? PbxConfig.Validate(s) : new List<string>()
                            }),
                            phones = (s.lines ?? new()).Where(c => PbxConfig.OnManagedAsterisk(s, c) && !string.IsNullOrWhiteSpace(c.phone?.extension))
                                .Select(c => new
                                {
                                    line = c.name, c.phone.extension,
                                    contacts = contacts?.Where(x => x.extension == c.phone.extension).ToList()
                                }),
                            contactsError,
                            security = managed == null ? null : Program.RecentSecurityEvents.Take(50)
                        });
                    });

                    // USB modems of the managed Asterisk's host for "+ Modem" in the settings: known models get
                    // a suggested driver and ports; ports already used by a modem are marked.
                    app.MapGet("/api/pbx/usb", () =>
                    {
                        Settings s;
                        try { s = Program.ReadSettingsFile(); }
                        catch (Exception ex) { return Results.Ok(new { ok = false, error = SettingsFile.Describe(ex) }); }
                        var managed = s.ManagedGateway;
                        if (managed == null) return Results.Ok(new { ok = false, error = "no gateway has pbx: managed" });
                        var (usb, usbAt) = Program.ReadUsbInventory(s);
                        var taken = (managed.modems ?? new()).SelectMany(m => new[] { m.data, m.audio, m.alsadev })
                            .Where(x => !string.IsNullOrEmpty(x)).ToHashSet();
                        return Results.Ok(new
                        {
                            ok = true,
                            gateway = managed.id,
                            usbAt,
                            usb = usb?.Select(u =>
                            {
                                var prof = PbxConfig.Profile(u.vid, u.pid);
                                string Port(int iface) => iface < 0 ? null : u.ports.FirstOrDefault(p => p.@interface == iface)?.path;
                                return new
                                {
                                    u.usb, u.vid, u.pid, u.manufacturer, u.product,
                                    model = prof?.Model,
                                    ports = u.ports.Select(p => new { p.path, p.@interface, p.access, used = taken.Contains(p.path) }),
                                    sound = u.sound.Select(x => new { x.card, x.alsadev, x.access, used = taken.Contains(x.alsadev) }),
                                    configured = u.ports.Any(p => taken.Contains(p.path)),
                                    access = u.ports.Any(p => p.access),
                                    suggest = prof == null ? null : new
                                    {
                                        driver = prof.Driver, data = Port(prof.Data), audio = Port(prof.Audio),
                                        alsadev = prof.Uac ? u.sound.FirstOrDefault()?.alsadev : null, noqpcmv = prof.NoQpcmv
                                    }
                                };
                            }).ToList()
                        });
                    });

                    app.MapGet("/api/pbx/preview", () =>
                    {
                        try
                        {
                            var (errors, files) = Program.PreviewPbx();
                            // Secrets are not shown, not even here.
                            var masked = files.ToDictionary(kv => kv.Key,
                                kv => System.Text.RegularExpressions.Regex.Replace(kv.Value, @"(?m)^password=.*$", "password=********"));
                            return Results.Ok(new { ok = true, errors, files = masked });
                        }
                        catch (Exception ex) { return Results.Ok(new { ok = false, errors = new[] { SettingsFile.Describe(ex) } }); }
                    });

                    // A modem added or removed in the settings: saved and, on the managed Asterisk, applied (the
                    // driver reloads gracefully, after the calls on it end).
                    app.MapPost("/api/pbx/modem", async (HttpRequest req) =>
                    {
                        var data = await ReadJson<Program.PbxModemEdit>(req);
                        Program.PbxModemResult r;
                        try { r = Program.EditModem(data); }
                        catch (Exception ex) { r = new(false, ex.Message); }
                        if (!r.ok) return Results.Ok(new { ok = false, r.error });
                        var s = Program.TryReadSettingsFile();
                        if (s?.ManagedGateway?.id != data.gateway) return Results.Ok(new { ok = true });
                        var applied = await Program.ApplyPbx();
                        return Results.Ok(new { ok = true, applyErrors = applied.errors, applied.output });
                    });

                    app.MapPost("/api/pbx/secret", async (HttpRequest req) =>
                    {
                        var data = await ReadJson<SecretPayload>(req);
                        try
                        {
                            string secret = Program.NewPhoneSecret(data?.line);
                            if (secret == null) return Results.Ok(new { ok = false, error = "the line has no phone.extension" });
                            var applied = await Program.ApplyPbx();
                            return Results.Ok(new { ok = true, secret, applyErrors = applied.errors });
                        }
                        catch (Exception ex) { return Results.Ok(new { ok = false, error = ex.Message }); }
                    });

                    app.MapGet("/api/calls/list", (int? limit, string? channel, string? direction, bool? missed,
                                                   bool? rec, string? q, string? from, string? to) =>
                    {
                        if (Program.settings?.FullMode != true) return Results.Ok(Array.Empty<object>());
                        var rows = Store.QueryCalls(Math.Clamp(limit ?? 300, 1, 5000), channel, direction,
                                                    missed == true, rec == true, q, from, to);
                        return Results.Ok(rows);
                    });

                    // The recording is fetched from the PBX on every request (files are small: ~1 MB per minute);
                    // ASP.NET handles Range itself, so seeking in the player works.
                    app.MapGet("/api/calls/{id:long}/audio", async (long id, bool? download) =>
                    {
                        var call = Store.GetCall(id);
                        if (call?.RecPath == null) return Results.NotFound();
                        byte[] wav;
                        try { wav = await Recordings.BalancedForCall(call); }
                        catch (Exception ex) { return Results.Problem(L10n.Web.T("web.calls.pbx_unavailable", ex.Message), statusCode: 502); }
                        if (wav == null) return Results.NotFound();

                        string name = download == true ? CallFileName(call) : null;
                        return Results.File(wav, "audio/wav", name, enableRangeProcessing: true);
                    });

                    // Transcript as a text file: the call header and lines with timestamps.
                    app.MapGet("/api/calls/{id:long}/transcript", (long id) =>
                    {
                        var call = Store.GetCall(id);
                        if (call == null || call.TrState != "done") return Results.NotFound();
                        var l = L10n.Transcript;
                        var segs = Program.Segments(call);
                        string body = segs.Count > 0 ? Program.FormatDialog(segs, l, withTime: true, prefix: "web.calls")
                                                     : (call.TrText ?? "");
                        var hd = TranscriptPdf.MakeHeader(call, l);
                        string text = $"{hd.Title}\n{hd.Meta}\n{hd.FromLabel}: {hd.From}\n{hd.ToLabel}: {hd.To}\n\n{body}\n";
                        return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(text)).ToArray(),
                                            "text/plain; charset=utf-8", Program.CallFileBase(call) + ".txt");
                    });

                    // Transcript as PDF, looking like the tab: for e-mail and printing.
                    app.MapGet("/api/calls/{id:long}/transcript.pdf", (long id) =>
                    {
                        var call = Store.GetCall(id);
                        if (call == null || call.TrState != "done") return Results.NotFound();
                        return Results.File(TranscriptPdf.Build(call), "application/pdf", Program.CallFileBase(call) + ".pdf");
                    });

                    app.MapPost("/api/calls/{id:long}/retranscribe", (long id) =>
                        Results.Ok(new { ok = Store.RequeueTranscription(id) }));

                    // SMS and calls in time order, from the DB.
                    app.MapGet("/api/journal", (int? lines) =>
                        Results.Ok(new { entries = Program.Journal(Math.Clamp(lines ?? 200, 1, 5000)) }));

                    // The system log: entries of this level and more severe, newest first.
                    app.MapGet("/api/system-log", (string? level, string? area, int? lines) =>
                        Results.Ok(new
                        {
                            entries = SysLog.Read(level, area, Math.Clamp(lines ?? 500, 1, 5000))
                                .Select(e => new { ts = e.ts.ToString("yyyy-MM-dd HH:mm:ss.fff"), e.level, e.area, e.message }),
                            areas = SysLog.Areas()
                        }));

                    app.MapGet("/api/config", () =>
                    {
                        try
                        {
                            if (!File.Exists(configPath))
                                return Results.Ok(new { ok = false, error = "Config file not found: " + configPath });
                            return Results.Ok(new { ok = true, content = File.ReadAllText(configPath) });
                        }
                        catch (Exception ex)
                        {
                            return Results.Ok(new { ok = false, error = ex.Message });
                        }
                    });

                    app.MapPost("/api/config", async (HttpRequest req) =>
                    {
                        var data = await ReadJson<ConfigPayload>(req);
                        if (data?.content == null)
                            return Results.Ok(new { ok = false, error = "Empty content" });
                        return await SaveYaml(configPath, data.content, data.restart);
                    });

                    // Settings form (a tree of sections): fields from the current file.
                    app.MapGet("/api/settings", () =>
                    {
                        try
                        {
                            return Results.Ok(SettingsForm.Snapshot(SettingsFile.Parse(File.ReadAllText(configPath))));
                        }
                        catch (Exception ex)
                        {
                            return Results.Ok(new { ok = false, error = L10n.Web.T("web.config.form_unavailable", SettingsFile.Describe(ex)) });
                        }
                    });

                    // Changed form fields are written into the YAML in place; removed gateways and lines are cut out.
                    app.MapPost("/api/settings", async (HttpRequest req) =>
                    {
                        var data = await ReadJson<SettingsPayload>(req);
                        if (data?.fields == null)
                            return Results.Ok(new { ok = false, error = "Empty request" });
                        string yaml = ConfigYaml.MoveLegacyTelegram(File.Exists(configPath) ? File.ReadAllText(configPath) : "");
                        foreach (var path in data.remove ?? Array.Empty<string>())
                        {
                            if (RemovablePath.IsMatch(path ?? ""))
                                yaml = ConfigYaml.Remove(yaml, path);
                            else if (ClientSectionPath.IsMatch(path ?? ""))
                                yaml = ConfigYaml.RemoveSection(yaml, path);
                        }
                        // Client sections added in the form: created with their first keys, then filled by the fields.
                        foreach (var path in data.add ?? Array.Empty<string>())
                            if (ClientSectionPath.IsMatch(path ?? "") && SettingsForm.NewSection(path) is { } first)
                                yaml = ConfigYaml.AddSection(yaml, path, first);
                        yaml = ConfigYaml.Apply(yaml, data.fields);
                        return await SaveYaml(configPath, yaml, data.restart);
                    });

                    app.MapPost("/api/restart", () =>
                    {
                        RestartSoon();
                        return Results.Ok(new { ok = true });
                    });

                    app.Run();
                }
                catch (Exception ex)
                {
                    SysLog.Error("web", "web interface failed to start: " + ex.Message);
                }
            });
        }

        // One card of the Status tab and one dot in the header.
        // host:port ghostline connects to: AMI for asterisk / yeastar, HTTP for goip.
        private static string GatewayAddress(Gateway g)
        {
            int? port = Modems.IsAmi(g.type) ? g.amiPort : g.httpPort;
            return port is > 0 ? $"{g.ip}:{port}" : g.ip;
        }

        private static object Component(string id, string title, string kind, string detail, bool connectionBased)
        {
            var h = HealthStatus.Find(id);
            return new
            {
                id, title, kind, detail,
                state = HealthStatus.State(id, connectionBased),
                lastActivityAt = h?.LastActivityAt,
                lastErrorAt = h?.LastErrorAt,
                lastError = h?.LastError
            };
        }

        // Line monitoring: null when the gateway is not monitored, {data: false} while there is no fresh poll.
        private static object Radio(Channel ch)
        {
            if (!Program.RadioMonitored(Program.getGatewayById(ch.gateway))) return null;
            var r = Program.LineRadio(ch);
            if (r == null) return new { data = false };
            return new
            {
                data = true, up = r.Up, state = r.State, registration = r.Registration, provider = r.Provider,
                net = r.Net, dbm = r.Dbm, bars = r.Bars, rssi = r.RssiDbm, rsrp = r.Rsrp, rsrq = r.Rsrq, sinr = r.Sinr,
                lac = r.Lac, cell = r.Cell, model = r.Model, usbPort = r.UsbPort, dataPort = r.DataPort,
                updatedAt = r.UpdatedAt, downSince = r.DownSince
            };
        }

        private static object CallsStatus()
        {
            var (pending, done, error) = Store.TranscriptionStats();
            return new
            {
                total = Store.CountCalls(),
                transcribePending = pending,
                transcribeDone = done,
                transcribeError = error,
                tgPending = Store.CountPendingCallTg()
            };
        }

        private static string CallFileName(CallRecord c) => Program.CallFileBase(c) + ".wav";

        // Saves the config if it parses; the previous version goes to .bak. With a managed Asterisk, new
        // phones get their secret (returned once) and the Asterisk files are written and reloaded. With
        // restart, restarts.
        private static async Task<IResult> SaveYaml(string configPath, string content, bool restart)
        {
            Settings parsed;
            try
            {
                parsed = SettingsFile.Parse(content);
            }
            catch (Exception yex)
            {
                return Results.Ok(new { ok = false, error = L10n.Web.T("web.config.not_saved", SettingsFile.Describe(yex)) });
            }
            try
            {
                if (File.Exists(configPath))
                    File.Copy(configPath, configPath + ".bak", overwrite: true);
                File.WriteAllText(configPath, content);
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, error = ex.Message });
            }
            SysLog.Info("config", "settings saved from the web interface" + (restart ? ", restarting" : ""));
            Dictionary<string, string> secrets = new();
            Program.PbxApplyResult applied = null;
            if (parsed.ManagedGateway != null)
            {
                try
                {
                    secrets = Program.AddMissingPhoneSecrets();
                    applied = await Program.ApplyPbx();
                }
                catch (Exception ex) { applied = new(false, new() { ex.Message }, new(), ""); }
            }
            if (restart) RestartSoon();
            return Results.Ok(new
            {
                ok = true, restarting = restart, secrets,
                pbx = applied == null ? null : new { applied.ok, applied.errors, applied.changed, applied.output }
            });
        }

        // Exit: systemd (Restart=always) or Docker (restart: unless-stopped) brings the service
        // back with the new settings.
        private static void RestartSoon() =>
            _ = Task.Run(async () => { await Task.Delay(500); Environment.Exit(0); });

        private static readonly System.Text.RegularExpressions.Regex RemovablePath =
            new(@"^(gateways\[id=|lines\[name=)[A-Za-z0-9][A-Za-z0-9_.-]*\]$");

        private static readonly System.Text.RegularExpressions.Regex ClientSectionPath =
            new(@"^(lines\[name=[A-Za-z0-9][A-Za-z0-9_.-]*\]\.)?clients\.(telegram|ntfy)$");

        private static async Task<T> ReadJson<T>(HttpRequest req)
        {
            using var reader = new StreamReader(req.Body);
            return System.Text.Json.JsonSerializer.Deserialize<T>(await reader.ReadToEndAsync(),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        // A page file from web/ next to the app.
        private static string Asset(string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "web", name);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        // {{key}} in the page is a string of the web language; scripts get the web.* dictionary as I18N; %VERSION% etc. is the version.
        private static string Localize(string html)
        {
            html = System.Text.RegularExpressions.Regex.Replace(html, @"\{\{([\w.]+)\}\}",
                m => System.Net.WebUtility.HtmlEncode(L10n.Web.T(m.Groups[1].Value)));
            return html.Replace("/*I18N*/{}", System.Text.Json.JsonSerializer.Serialize(L10n.Web.Export("web.")))
                       .Replace("%VERSION%", VersionInfo.Version)
                       .Replace("%BUILD%", VersionInfo.BuildDate)
                       .Replace("%URL%", VersionInfo.ProjectUrl);
        }

        record ConfigPayload(string content, bool restart = false);
        record SettingsPayload(Dictionary<string, string> fields, string[] remove, bool restart = false, string[] add = null);
        record SendPayload(string channel, string number, string text);
        record SecretPayload(string line);
    }
}
