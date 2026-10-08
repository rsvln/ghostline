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
                    foreach (var path in new[] { "/", "/calls", "/sms", "/log", "/status", "/config", "/config/yaml", "/about" })
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
                        var list = (Program.settings?.channels ?? new List<Channel>())
                            .Select(c => new { c.name, gateway = c.gateway, type = Program.getGatewayById(c.gateway)?.type })
                            .ToList();
                        return Results.Ok(list);
                    });

                    app.MapGet("/api/status", () =>
                    {
                        var gateways = (Program.settings?.gateways ?? new List<Gateway>())
                            .Select(g => new { g.id, g.type, g.ip })
                            .ToList();
                        // Telegram is not a physical gateway from the config, but the UI needs a card and a dot for it too.
                        gateways.Add(new { id = "telegram", type = "telegram", ip = "" });
                        return Results.Ok(new
                        {
                            gateways,
                            health = HealthStatus.Snapshot(),
                            stats = Store.GetStats(),
                            tgQueuePending = Store.CountPendingTelegramMessages(),
                            smsQueuePending = Store.CountPendingOutgoingSms(),
                            calls = Program.settings?.FullMode == true ? CallsStatus() : null
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
                        transcribe = Program.settings?.calls?.transcribe?.enabled == true
                    }));

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

                    app.MapGet("/api/log", (int? lines) =>
                    {
                        int n = lines ?? 200;
                        string logDir = "/var/log/ghostline/";
                        if (!Directory.Exists(logDir))
                            return Results.Ok(new { lines = Array.Empty<string>() });

                        var logFiles = Directory.GetFiles(logDir, "ghostline_*.log")
                                                .OrderByDescending(f => f)
                                                .ToArray();

                        if (logFiles.Length == 0)
                            return Results.Ok(new { lines = Array.Empty<string>() });

                        var result = new List<string>();
                        foreach (var file in logFiles)
                        {
                            if (result.Count >= n) break;
                            var fileLines = File.ReadAllLines(file);
                            result.InsertRange(0, fileLines);
                        }

                        var tail = result.Skip(Math.Max(0, result.Count - n)).ToArray();
                        return Results.Ok(new { lines = tail });
                    });

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
                        return SaveYaml(configPath, data.content, data.restart);
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
                        string yaml = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
                        foreach (var path in data.remove ?? Array.Empty<string>())
                            if (RemovablePath.IsMatch(path ?? ""))
                                yaml = ConfigYaml.Remove(yaml, path);
                        yaml = ConfigYaml.Apply(yaml, data.fields);
                        return SaveYaml(configPath, yaml, data.restart);
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
                    Console.WriteLine("WebUi failed to start: " + ex.Message);
                }
            });
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

        // Saves the config if it parses; the previous version goes to .bak. With restart, restarts.
        private static IResult SaveYaml(string configPath, string content, bool restart)
        {
            try
            {
                SettingsFile.Parse(content);
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
            Console.WriteLine("Settings saved from the web UI" + (restart ? ", restarting" : ""));
            if (restart) RestartSoon();
            return Results.Ok(new { ok = true, restarting = restart });
        }

        // Exit: systemd (Restart=always) or Docker (restart: unless-stopped) brings the service
        // back with the new settings.
        private static void RestartSoon() =>
            _ = Task.Run(async () => { await Task.Delay(500); Environment.Exit(0); });

        private static readonly System.Text.RegularExpressions.Regex RemovablePath =
            new(@"^(gateways\[id=|channels\[name=)[A-Za-z0-9][A-Za-z0-9_.-]*\]$");

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
        record SettingsPayload(Dictionary<string, string> fields, string[] remove, bool restart = false);
        record SendPayload(string channel, string number, string text);
    }
}
