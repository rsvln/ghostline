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

                    app.MapGet("/", () => Results.Content(Localize(GetHtml()), "text/html; charset=utf-8"));

                    // Редактор YAML для вкладки Config — бандл CodeMirror, отдаётся локально (без интернета).
                    app.MapGet("/js/yaml-editor.js", () =>
                    {
                        string path = Path.Combine(AppContext.BaseDirectory, "web", "yaml-editor.js");
                        return File.Exists(path) ? Results.File(path, "text/javascript") : Results.NotFound();
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
                        // Telegram — не физический шлюз из конфига, но карточка/точка в UI нужна и для него.
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

                    // --- Звонки (mode "full") ---

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

                    // Запись берётся с АТС при каждом запросе (файлы небольшие: ~1 МБ на минуту),
                    // Range отдаёт сам ASP.NET — перемотка в плеере работает.
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

                    // Расшифровка текстовым файлом: шапка звонка и реплики с отметками времени.
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

                    // Расшифровка в PDF — тот же вид, что во вкладке: для почты и печати.
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
                        try
                        {
                            using var reader = new StreamReader(req.Body);
                            var body = await reader.ReadToEndAsync();
                            var data = System.Text.Json.JsonSerializer.Deserialize<ConfigPayload>(body,
                                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (data?.content == null)
                                return Results.Ok(new { ok = false, error = "Empty content" });

                            try
                            {
                                SettingsFile.Parse(data.content);
                            }
                            catch (Exception yex)
                            {
                                return Results.Ok(new { ok = false, error = L10n.Web.T("web.config.not_saved", SettingsFile.Describe(yex)) });
                            }

                            if (File.Exists(configPath))
                                File.Copy(configPath, configPath + ".bak", overwrite: true);
                            File.WriteAllText(configPath, data.content);

                            if (data.restart)
                            {
                                // Выходим — systemd (Restart=always) поднимет службу через 5 с уже с новыми настройками.
                                _ = Task.Run(async () => { await Task.Delay(500); Environment.Exit(0); });
                                return Results.Ok(new { ok = true, note = L10n.Web.T("web.config.restarting"), restarting = true });
                            }
                            return Results.Ok(new { ok = true, note = L10n.Web.T("web.config.saved") });
                        }
                        catch (Exception ex)
                        {
                            return Results.Ok(new { ok = false, error = ex.Message });
                        }
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

        // {{ключ}} в странице — строка веб-языка; скриптам — словарь web.* как I18N. Как в frte2tg.
        private static string Localize(string html)
        {
            html = System.Text.RegularExpressions.Regex.Replace(html, @"\{\{([\w.]+)\}\}",
                m => System.Net.WebUtility.HtmlEncode(L10n.Web.T(m.Groups[1].Value)));
            return html.Replace("/*I18N*/{}", System.Text.Json.JsonSerializer.Serialize(L10n.Web.Export("web.")));
        }

        record ConfigPayload(string content, bool restart = false);
        record SendPayload(string channel, string number, string text);

        private static string GetHtml() => """
            <!DOCTYPE html>
            <html lang="{{web.lang}}">
            <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>ghostline</title>
            <style>
              @import url('https://fonts.googleapis.com/css2?family=JetBrains+Mono:wght@400;600&family=IBM+Plex+Sans:wght@400;500&display=swap');

              :root {
                --bg: #0d1117;
                --bg2: #161b22;
                --bg3: #21262d;
                --border: #30363d;
                --text: #c9d1d9;
                --muted: #8b949e;
                --accent: #58a6ff;
                --green: #3fb950;
                --yellow: #d29922;
                --red: #f85149;
                --orange: #e3b341;
              }

              * { box-sizing: border-box; margin: 0; padding: 0; }

              body {
                background: var(--bg);
                color: var(--text);
                font-family: 'IBM Plex Sans', sans-serif;
                font-size: 14px;
                height: 100vh;
                display: flex;
                flex-direction: column;
                overflow: hidden;
              }

              header {
                background: var(--bg2);
                border-bottom: 1px solid var(--border);
                padding: 12px 20px;
                display: flex;
                align-items: center;
                gap: 16px;
                flex-shrink: 0;
              }

              header h1 {
                font-family: 'JetBrains Mono', monospace;
                font-size: 15px;
                color: var(--accent);
                letter-spacing: 0.05em;
              }

              header .dot {
                width: 8px; height: 8px;
                border-radius: 50%;
                background: var(--green);
                box-shadow: 0 0 6px var(--green);
                animation: pulse 2s infinite;
              }

              @keyframes pulse {
                0%, 100% { opacity: 1; }
                50% { opacity: 0.4; }
              }

              .header-status { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; }
              .hstat {
                display: flex; align-items: center; gap: 5px;
                font-size: 11px; color: var(--muted);
                font-family: 'JetBrains Mono', monospace;
              }
              .hdot {
                display: inline-block;
                width: 7px; height: 7px;
                border-radius: 50%;
                background: var(--muted);
                flex-shrink: 0;
              }
              .hdot.ok   { background: var(--green);  box-shadow: 0 0 5px var(--green); }
              .hdot.warn { background: var(--yellow); box-shadow: 0 0 5px var(--yellow); }
              .hdot.err  { background: var(--red);    box-shadow: 0 0 5px var(--red); }

              .tabs { display: flex; gap: 2px; margin-left: auto; }

              .tab {
                padding: 6px 16px;
                border-radius: 6px;
                border: 1px solid transparent;
                background: transparent;
                color: var(--muted);
                cursor: pointer;
                font-family: 'IBM Plex Sans', sans-serif;
                font-size: 13px;
                transition: all 0.15s;
              }

              .tab:hover { color: var(--text); background: var(--bg3); }
              .tab.active { background: var(--bg3); border-color: var(--border); color: var(--accent); }

              .panels { flex: 1; overflow: hidden; display: flex; flex-direction: column; }
              .panel { display: none; flex: 1; overflow: hidden; flex-direction: column; }
              .panel.active { display: flex; }

              .toolbar {
                padding: 10px 16px;
                background: var(--bg2);
                border-bottom: 1px solid var(--border);
                display: flex;
                align-items: center;
                gap: 12px;
                flex-shrink: 0;
                flex-wrap: wrap;
              }

              .toolbar label { color: var(--muted); font-size: 12px; }

              .autoscroll-toggle { margin-left: auto; display: flex; align-items: center; gap: 8px; }
              .autoscroll-toggle input { accent-color: var(--accent); }

              .toolbar select, .toolbar input[type=number], .toolbar input[type=text] {
                background: var(--bg3);
                border: 1px solid var(--border);
                color: var(--text);
                padding: 4px 8px;
                border-radius: 6px;
                font-size: 12px;
                font-family: 'IBM Plex Sans', sans-serif;
              }

              .btn {
                padding: 5px 14px;
                border-radius: 6px;
                border: 1px solid var(--border);
                background: var(--bg3);
                color: var(--text);
                cursor: pointer;
                font-size: 12px;
                font-family: 'IBM Plex Sans', sans-serif;
                transition: all 0.15s;
              }
              .btn:hover { border-color: var(--accent); color: var(--accent); }
              .btn.primary { background: var(--accent); border-color: var(--accent); color: #000; font-weight: 500; }
              .btn.primary:hover { opacity: 0.85; color: #000; }

              #sms-container, #log-container {
                  flex: 1;
                  overflow-y: auto;
                  overflow-x: auto;
                  padding: 12px 16px;
                  font-family: 'JetBrains Mono', monospace;
                  font-size: 12px;
                  line-height: 1.7;
              }

              #sms-container::-webkit-scrollbar, #log-container::-webkit-scrollbar { width: 6px; }
              #sms-container::-webkit-scrollbar-track, #log-container::-webkit-scrollbar-track { background: var(--bg); }
              #sms-container::-webkit-scrollbar-thumb, #log-container::-webkit-scrollbar-thumb { background: var(--border); border-radius: 3px; }

              .sms-line {
                  display: grid;
                  grid-template-columns: 150px 40px 30px 90px 130px 140px 1fr;
                  gap: 0 12px;
                  padding: 3px 0;
                  min-width: max-content;
                  border-bottom: 1px solid var(--bg3);
                }
              .sms-line:hover { background: var(--bg3); }
              .sms-line.hdr { color: var(--muted); font-weight: 600; border-bottom: 1px solid var(--border); }

              .dir-in { color: var(--green); }
              .dir-out { color: var(--accent); }
              .sms-ts { color: var(--muted); }
              .sms-peer { color: var(--yellow); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
              .sms-note { color: var(--muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
              .sms-content { color: var(--text); white-space: pre-wrap; }

              .log-line {
                  display: grid;
                  /* время с миллисекундами (HH:mm:ss.fff) — 12 знаков моноширинным шрифтом */
                  grid-template-columns: 90px 100px 24px 30px 90px 150px 150px 1fr;
                  gap: 0 12px;
                  padding: 1px 0;
                  min-width: max-content;
                }
              .log-line:hover { filter: brightness(1.3); }
              .log-ts { color: var(--muted); }
              .log-gw { color: var(--accent); }

              .config-toolbar {
                padding: 10px 16px;
                background: var(--bg2);
                border-bottom: 1px solid var(--border);
                display: flex;
                align-items: center;
                gap: 12px;
                flex-shrink: 0;
              }

              .config-hint { color: var(--muted); font-size: 12px; margin-left: auto; }

              #config-editor-host { flex: 1; min-height: 0; overflow: hidden; }

              #config-editor {
                flex: 1;
                background: var(--bg);
                color: var(--text);
                border: none;
                outline: none;
                padding: 16px;
                font-family: 'JetBrains Mono', monospace;
                font-size: 13px;
                line-height: 1.7;
                resize: none;
                tab-size: 2;
              }

              .toast {
                position: fixed;
                bottom: 24px;
                right: 24px;
                padding: 10px 20px;
                border-radius: 8px;
                font-size: 13px;
                opacity: 0;
                transform: translateY(8px);
                transition: all 0.2s;
                pointer-events: none;
                z-index: 100;
              }
              .toast.show { opacity: 1; transform: translateY(0); }
              .toast.ok { background: var(--green); color: #000; }
              .toast.err { background: var(--red); color: #fff; }

              .modal-overlay {
                display: none;
                position: fixed;
                inset: 0;
                background: rgba(0,0,0,0.6);
                align-items: center;
                justify-content: center;
                z-index: 50;
              }
              .modal-overlay.show { display: flex; }

              .modal {
                background: var(--bg2);
                border: 1px solid var(--border);
                border-radius: 10px;
                width: 420px;
                max-width: 90vw;
                padding: 20px;
              }
              .modal h2 { font-size: 15px; margin-bottom: 16px; color: var(--accent); }
              .modal-field { margin-bottom: 12px; display: flex; flex-direction: column; gap: 4px; }
              .modal-field label { font-size: 12px; color: var(--muted); }
              .modal-field select, .modal-field input, .modal-field textarea {
                background: var(--bg3);
                border: 1px solid var(--border);
                color: var(--text);
                padding: 8px 10px;
                border-radius: 6px;
                font-size: 13px;
                font-family: 'IBM Plex Sans', sans-serif;
              }
              .modal-field textarea { font-family: 'JetBrains Mono', monospace; min-height: 100px; resize: vertical; }
              .modal-actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 16px; }

              .status-scroll { flex: 1; overflow-y: auto; }
              .status-grid { display: flex; flex-wrap: wrap; gap: 12px; padding: 16px; }
              .status-card {
                background: var(--bg2);
                border: 1px solid var(--border);
                border-radius: 10px;
                padding: 14px 16px;
                min-width: 220px;
                flex: 1 1 220px;
              }
              .status-card h3 {
                font-size: 13px;
                margin-bottom: 8px;
                display: flex; align-items: center; gap: 8px;
                font-family: 'JetBrains Mono', monospace;
                color: var(--text);
              }
              .status-card .row { font-size: 12px; color: var(--muted); margin-top: 4px; }
              .status-card .row b { color: var(--text); font-weight: 500; }
              .status-card .row.err { color: var(--red); }

              .stat-table-wrap { padding: 0 16px 16px; overflow-x: auto; }
              .stat-table { width: 100%; border-collapse: collapse; font-size: 12px; font-family: 'JetBrains Mono', monospace; }
              .stat-table th, .stat-table td { text-align: left; padding: 6px 10px; border-bottom: 1px solid var(--bg3); }
              .stat-table th { color: var(--muted); font-weight: 600; }

              /* ---- Calls ---- */
              #calls-container { flex: 1; overflow-y: auto; padding: 8px 16px 24px; }
              .call { border-bottom: 1px solid var(--bg3); padding: 8px 4px; }
              .call:hover { background: var(--bg2); }
              .call-main {
                display: grid;
                grid-template-columns: 96px 22px 60px minmax(160px, 1fr) 56px auto;
                gap: 0 12px; align-items: center;
              }
              .call-ts { color: var(--muted); font-family: 'JetBrains Mono', monospace; font-size: 12px; }
              .call-dir { font-size: 15px; text-align: center; }
              .call-dir.in { color: var(--green); }
              .call-dir.out { color: var(--accent); }
              .call-dir.missed { color: var(--red); }
              .call-line { font-family: 'JetBrains Mono', monospace; font-size: 12px; color: var(--muted); }
              .call-who { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
              .call-who b { font-weight: 500; color: var(--text); }
              .call-who .num { color: var(--yellow); font-family: 'JetBrains Mono', monospace; font-size: 12px; margin-left: 6px; }
              .call-dur { font-family: 'JetBrains Mono', monospace; font-size: 12px; text-align: right; }
              .call-actions { display: flex; gap: 6px; justify-content: flex-end; }
              .icon-btn {
                min-width: 34px; height: 30px; padding: 0 8px;
                border-radius: 6px; border: 1px solid var(--border);
                background: var(--bg3); color: var(--text); cursor: pointer;
                font-size: 14px; text-decoration: none;
                display: inline-flex; align-items: center; justify-content: center;
              }
              .icon-btn:hover { border-color: var(--accent); }
              .icon-btn svg { width: 16px; height: 16px; stroke: currentColor; fill: none;
                              stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
              .icon-btn.off { opacity: 0.35; pointer-events: none; }
              .icon-btn.err { color: var(--red); }
              .icon-btn[data-tr="pending"] { color: var(--muted); }
              .call audio { width: 100%; margin-top: 8px; height: 36px; }
              .call-text {
                margin-top: 8px; padding: 10px 12px;
                background: var(--bg2); border: 1px solid var(--border); border-radius: 8px;
                white-space: pre-wrap; line-height: 1.55;
              }
              .call-text mark { background: var(--yellow); color: #000; border-radius: 2px; }
              /* Диалог — узкой колонкой, все реплики слева; стороны различаются подписью и цветом. */
              .call-text.dialog { white-space: normal; display: flex; flex-direction: column; gap: 4px; max-width: 860px; }
              .dl { display: flex; flex-direction: column; max-width: 80%; }
              .dl { align-self: flex-start; }
              .dl.mono { max-width: 100%; }
              .dl .hdr { font-size: 11px; color: var(--muted); margin-bottom: 2px; }
              .dl .hdr a { color: var(--muted); text-decoration: none; font-family: 'JetBrains Mono', monospace; cursor: pointer; }
              .dl .hdr a:hover { color: var(--accent); }
              .dl .bubble { padding: 6px 10px; border-radius: 10px; line-height: 1.45; white-space: pre-wrap; }
              .dl.me .bubble { background: #1f3a5f; border-bottom-left-radius: 3px; }
              .dl.me .hdr { color: var(--accent); }
              .dl.them .bubble { background: var(--bg3); border-bottom-left-radius: 3px; }
              .dl.mono .bubble { background: transparent; padding: 1px 0; }
              .call-text .meta { color: var(--muted); font-size: 12px; margin-top: 6px; }
              /* display:flex у диалога перебивает атрибут hidden — без этого свернуть нельзя. */
              .call-text[hidden] { display: none !important; }
              .call-main { cursor: pointer; }
              .icon-btn[data-tr="pending"] { cursor: default; }
              .tr-actions { display: flex; gap: 8px; }
              .tr-actions a.btn { text-decoration: none; }
              .calls-count { color: var(--muted); font-size: 12px; }
              .toolbar input.date { width: 96px; }
              .toolbar input.date.bad { border-color: var(--red); }
              .toolbar input[type=date] {
                background: var(--bg3); border: 1px solid var(--border); color: var(--text);
                padding: 3px 6px; border-radius: 6px; font-size: 12px; color-scheme: dark;
              }

              @media (max-width: 700px) {
                header { flex-wrap: wrap; padding: 10px 12px; gap: 8px; }
                .tabs { margin-left: 0; width: 100%; overflow-x: auto; }
                .toolbar { padding: 8px 12px; gap: 8px; }
                #calls-container { padding: 4px 8px 24px; }
                .call-main {
                  grid-template-columns: 22px 1fr auto;
                  grid-template-areas: "dir who dur" "dir meta actions";
                  gap: 2px 10px;
                }
                .call-dir { grid-area: dir; align-self: start; }
                .call-who { grid-area: who; }
                .call-dur { grid-area: dur; }
                .call-ts { grid-area: meta; }
                .call-line { display: none; }
                .call-actions { grid-area: actions; }
                .call-ts::after { content: attr(data-line); margin-left: 8px; }
              }
            </style>
            </head>
            <body>

            <header>
              <div class="dot"></div>
              <h1>ghostline</h1>
              <div class="header-status" id="header-status"></div>
              <div class="tabs">
                <button class="tab" id="tab-calls" style="display:none" onclick="switchTab('calls', this)">{{web.tab.calls}}</button>
                <button class="tab active" id="tab-sms" onclick="switchTab('sms', this)">{{web.tab.sms}}</button>
                <button class="tab" onclick="switchTab('log', this)">{{web.tab.log}}</button>
                <button class="tab" onclick="switchTab('status', this)">{{web.tab.status}}</button>
                <button class="tab" onclick="switchTab('config', this)">{{web.tab.config}}</button>
              </div>
            </header>

            <div class="panels">

              <div class="panel" id="panel-calls">
                <div class="toolbar">
                  <select id="calls-channel" onchange="loadCalls()"><option value="">{{web.calls.all_lines}}</option></select>
                  <select id="calls-direction" onchange="loadCalls()">
                    <option value="">{{web.all}}</option>
                    <option value="in">{{web.calls.dir.in}}</option>
                    <option value="out">{{web.calls.dir.out}}</option>
                  </select>
                  <label><input type="checkbox" id="calls-rec" checked onchange="loadCalls()"> {{web.calls.with_recording}}</label>
                  <label><input type="checkbox" id="calls-missed" onchange="if (this.checked) document.getElementById('calls-rec').checked = false; loadCalls()"> {{web.calls.missed_only}}</label>
                  <input type="text" id="calls-q" placeholder="{{web.calls.search}}" oninput="debouncedLoadCalls()" style="width:220px">
                  <!-- Даты — текстом в формате языка: у <input type="date"> формат навязывает браузер. -->
                  <label>{{web.calls.date_from}}</label>
                  <input type="text" id="calls-from" class="date" placeholder="{{web.calls.date_format}}" onchange="loadCalls()">
                  <label>{{web.calls.date_to}}</label>
                  <input type="text" id="calls-to" class="date" placeholder="{{web.calls.date_format}}" onchange="loadCalls()">
                  <button class="btn" onclick="loadCalls()">{{web.refresh}}</button>
                  <span class="calls-count" id="calls-count"></span>
                </div>
                <div id="calls-container"></div>
              </div>

              <div class="panel active" id="panel-sms">
                <div class="toolbar">
                  <label>{{web.sms.rows}}</label>
                  <input type="number" id="sms-limit" value="200" min="10" max="5000" style="width:70px">
                  <button class="btn" onclick="loadSms()">{{web.refresh}}</button>

                  <label>{{web.sms.dir}}</label>
                  <select id="sms-direction" onchange="loadSms()">
                    <option value="">{{web.all}}</option>
                    <option value="in">in</option>
                    <option value="out">out</option>
                  </select>

                  <label>{{web.sms.channel}}</label>
                  <select id="sms-channel" onchange="loadSms()">
                    <option value="">{{web.all}}</option>
                  </select>

                  <label>{{web.sms.text}}</label>
                  <input type="text" id="sms-text" placeholder="{{web.sms.search}}" oninput="debouncedLoadSms()" style="width:150px">

                  <button class="btn" onclick="exportSms()">{{web.sms.export}}</button>
                  <button class="btn primary" onclick="openSendModal()">{{web.sms.send}}</button>

                  <div class="autoscroll-toggle">
                    <label>Auto-refresh:</label>
                    <select id="sms-refresh-interval" onchange="setSmsRefresh()">
                      <option value="0">{{web.off}}</option>
                      <option value="5000">5s</option>
                      <option value="10000">10s</option>
                      <option value="30000">30s</option>
                    </select>
                  </div>
                </div>
                <div id="sms-container"></div>
              </div>

              <div class="panel" id="panel-log">
                <div class="toolbar">
                  <label>{{web.log.lines}}</label>
                  <input type="number" id="log-lines" value="200" min="10" max="2000" style="width:70px">
                  <button class="btn" onclick="loadLog()">{{web.refresh}}</button>

                  <div class="autoscroll-toggle">
                    <label>{{web.autorefresh}}</label>
                    <select id="log-refresh-interval" onchange="setLogRefresh()">
                      <option value="0">{{web.off}}</option>
                      <option value="5000" selected>5s</option>
                      <option value="10000">10s</option>
                      <option value="30000">30s</option>
                    </select>
                  </div>
                </div>
                <div id="log-container"></div>
              </div>

              <div class="panel" id="panel-status">
                <div class="toolbar">
                  <button class="btn" onclick="loadStatus()">{{web.refresh}}</button>
                </div>
                <div class="status-scroll">
                  <div class="status-grid" id="status-cards"></div>
                  <div class="stat-table-wrap">
                    <table class="stat-table">
                      <thead><tr><th>{{web.status.channel}}</th><th>{{web.status.in}}</th><th>{{web.status.out}}</th></tr></thead>
                      <tbody id="status-stats-body"></tbody>
                    </table>
                  </div>
                </div>
              </div>

              <div class="panel" id="panel-config">
                <div class="config-toolbar">
                  <button class="btn" onclick="saveConfig(false)">{{web.config.save}}</button>
                  <button class="btn primary" onclick="saveConfig(true)">{{web.config.save_restart}}</button>
                  <button class="btn" onclick="loadConfig()">{{web.config.reload}}</button>
                  <span class="config-hint">{{web.config.hint}}</span>
                </div>
                <div id="config-editor-host" style="display:none"></div>
                <textarea id="config-editor" spellcheck="false"></textarea>
              </div>

            </div>

            <div class="modal-overlay" id="send-modal">
              <div class="modal">
                <h2>{{web.sms.modal.title}}</h2>
                <div class="modal-field">
                  <label>{{web.sms.modal.channel}}</label>
                  <select id="send-channel"></select>
                </div>
                <div class="modal-field">
                  <label>{{web.sms.modal.number}}</label>
                  <input type="text" id="send-number" placeholder="+79000000000">
                </div>
                <div class="modal-field">
                  <label>{{web.sms.modal.message}}</label>
                  <textarea id="send-text" placeholder="{{web.sms.modal.message_placeholder}}"></textarea>
                </div>
                <div class="modal-actions">
                  <button class="btn" onclick="closeSendModal()">{{web.sms.modal.cancel}}</button>
                  <button class="btn primary" onclick="submitSend()">{{web.sms.modal.send}}</button>
                </div>
              </div>
            </div>

            <div class="toast" id="toast"></div>

            <script>
            // Строки веб-языка (locale.web) — подставляет сервер. t('ключ', арг0, арг1...).
            const I18N = /*I18N*/{};
            const t = (k, ...a) => (I18N[k] ?? k).replace(/\{(\d+)\}/g, (_, i) => a[i]);

            function switchTab(name, btn) {
              document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
              document.querySelectorAll('.panel').forEach(p => p.classList.remove('active'));
              (btn || document.getElementById('tab-' + name)).classList.add('active');
              document.getElementById('panel-' + name).classList.add('active');
              if (name === 'calls') loadCalls();
              if (name === 'config') loadConfig();
              if (name === 'log') loadLog();
              if (name === 'sms') loadSms();
              if (name === 'status') loadStatus();
            }

            function esc(s) {
              return String(s ?? '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
            }

            // ---- SMS tab ----

            let channelsCache = [];

            async function loadChannels() {
              const res = await fetch('/api/channels');
              channelsCache = await res.json();

              const filterSel = document.getElementById('sms-channel');
              const sendSel = document.getElementById('send-channel');
              filterSel.innerHTML = `<option value="">${esc(t('web.all'))}</option>` +
                channelsCache.map(c => `<option value="${esc(c.name)}">${esc(c.name)} (${esc(c.type)})</option>`).join('');
              sendSel.innerHTML = channelsCache.map(c => `<option value="${esc(c.name)}">${esc(c.name)} (${esc(c.type)})</option>`).join('');
            }

            let smsRefreshTimer = null;
            function setSmsRefresh() {
              clearInterval(smsRefreshTimer);
              const ms = parseInt(document.getElementById('sms-refresh-interval').value);
              if (ms > 0) smsRefreshTimer = setInterval(loadSms, ms);
            }

            async function loadSms() {
              const limit = document.getElementById('sms-limit').value;
              const direction = document.getElementById('sms-direction').value;
              const channel = document.getElementById('sms-channel').value;
              const text = document.getElementById('sms-text').value;

              const params = new URLSearchParams({ limit });
              if (direction) params.set('direction', direction);
              if (channel) params.set('channel', channel);
              if (text) params.set('text', text);

              const res = await fetch('/api/sms/list?' + params.toString());
              const rows = await res.json();

              const container = document.getElementById('sms-container');
              const header = `<div class="sms-line hdr">
                ${['time','dir','gw','channel','peer','note','content'].map(c => `<span>${esc(t('web.sms.col.' + c))}</span>`).join('')}
              </div>`;
              container.innerHTML = header + rows.map(formatSmsLine).join('');
            }

            function formatSmsLine(r) {
              return `<div class="sms-line">
                <span class="sms-ts">${esc(r.ts)}</span>
                <span class="dir-${esc(r.direction)}">${esc(r.direction)}</span>
                <span>${esc(r.gateway)}</span>
                <span>${esc(r.channel)}</span>
                <span class="sms-peer" title="${esc(r.peer)}">${esc(r.peer)}</span>
                <span class="sms-note" title="${esc(r.note)}">${esc(r.note)}</span>
                <span class="sms-content">${esc(r.content)}</span>
              </div>`;
            }

            let debounceTimer = null;
            function debouncedLoadSms() {
              clearTimeout(debounceTimer);
              debounceTimer = setTimeout(loadSms, 300);
            }

            function exportSms() {
              const limit = document.getElementById('sms-limit').value;
              const direction = document.getElementById('sms-direction').value;
              const channel = document.getElementById('sms-channel').value;
              const text = document.getElementById('sms-text').value;

              const params = new URLSearchParams();
              if (direction) params.set('direction', direction);
              if (channel) params.set('channel', channel);
              if (text) params.set('text', text);
              window.location.href = '/api/sms/export?' + params.toString();
            }

            // ---- Send SMS modal ----

            function openSendModal() {
              document.getElementById('send-number').value = '';
              document.getElementById('send-text').value = '';
              document.getElementById('send-modal').classList.add('show');
            }

            function closeSendModal() {
              document.getElementById('send-modal').classList.remove('show');
            }

            async function submitSend() {
              const channel = document.getElementById('send-channel').value;
              const number = document.getElementById('send-number').value.trim();
              const text = document.getElementById('send-text').value.trim();

              if (!channel || !number || !text) {
                showToast(t('web.sms.fill_all'), 'err');
                return;
              }

              const res = await fetch('/api/sms/send', {
                method: 'POST',
                headers: {'Content-Type': 'application/json'},
                body: JSON.stringify({ channel, number, text })
              });
              const data = await res.json();
              if (data.ok) {
                showToast(t('web.sms.queued'), 'ok');
                closeSendModal();
                loadSms();
              } else {
                showToast(t('web.error', data.error || 'unknown'), 'err');
              }
            }

            // ---- Log tab ----

            let logRefreshTimer = null;
            function setLogRefresh() {
              clearInterval(logRefreshTimer);
              const ms = parseInt(document.getElementById('log-refresh-interval').value);
              if (ms > 0) logRefreshTimer = setInterval(loadLog, ms);
            }

            async function loadLog() {
              const n = document.getElementById('log-lines').value;
              const res = await fetch('/api/log?lines=' + n);
              const data = await res.json();
              const container = document.getElementById('log-container');
              // Новое сверху — как на вкладках «Звонки» и SMS.
              container.innerHTML = data.lines.slice().reverse().map(formatLogLine).join('');
            }

            function formatLogLine(line) {
              const parts = line.split('\t');
              if (parts.length < 8) return `<div class="log-line"><span>${esc(line)}</span></div>`;
              const [date, time, gw, dir, smstime, from, dest, ...contentParts] = parts;
              const content = contentParts.join('\t');
              return `<div class="log-line">
                <span class="log-ts">${esc(date)}</span>
                <span class="log-ts">${esc(time)}</span>
                <span class="log-gw">${esc(gw)}</span>
                <span class="dir-${esc(dir)}">${esc(dir)}</span>
                <span>${esc(from)}</span>
                <span>${esc(dest)}</span>
                <span></span>
                <span>${esc(content)}</span>
              </div>`;
            }

            // ---- Config tab ----

            // Редактор: CodeMirror из /js/yaml-editor.js (тот же бандл, что у frte2tg),
            // если не загрузился — обычное текстовое поле.
            let configEditor = null;
            function getConfigEditor() {
              return configEditor ??= (async () => {
                const area = document.getElementById('config-editor');
                try {
                  const { createYamlEditor } = await import('/js/yaml-editor.js');
                  const host = document.getElementById('config-editor-host');
                  const editor = createYamlEditor(host, area.value);
                  area.style.display = 'none';
                  host.style.display = '';
                  return editor;
                } catch (e) {
                  console.warn('YAML editor is not available, using the plain text area', e);
                  return { getValue: () => area.value, setValue: v => { area.value = v; }, focus: () => area.focus() };
                }
              })();
            }

            async function loadConfig() {
              try {
                const res = await fetch('/api/config');
                if (!res.ok) { showToast(t('web.config.read_failed', res.status), 'err'); return; }
                const data = await res.json();
                if (!data.ok) { showToast(t('web.error', data.error || 'unknown'), 'err'); return; }
                (await getConfigEditor()).setValue(data.content);
              } catch (e) {
                showToast(t('web.network_error', e.message), 'err');
              }
            }

            async function saveConfig(restart) {
              const content = (await getConfigEditor()).getValue();
              try {
                const res = await fetch('/api/config', {
                  method: 'POST',
                  headers: {'Content-Type': 'application/json'},
                  body: JSON.stringify({ content, restart })
                });
                if (!res.ok) { showToast(t('web.config.save_failed', res.status), 'err'); return; }
                const data = await res.json();
                if (!data.ok) { showToast(data.error || t('web.error', ''), 'err'); return; }
                showToast(data.note, 'ok');
                if (data.restarting) await waitForRestart();
              } catch (e) {
                showToast(t('web.network_error', e.message), 'err');
              }
            }

            // После «Сохранить и перезапустить»: служба выходит, systemd поднимает её через ~5 с.
            async function waitForRestart() {
              await new Promise(r => setTimeout(r, 3000));
              for (let i = 0; i < 30; i++) {
                try {
                  const res = await fetch('/api/mode');
                  if (res.ok) { showToast(t('web.config.restarted'), 'ok'); loadStatus(); return; }
                } catch { }
                await new Promise(r => setTimeout(r, 1000));
              }
              showToast(t('web.config.restart_timeout'), 'err');
            }

            function showToast(msg, type) {
              const t = document.getElementById('toast');
              t.textContent = msg;
              t.className = 'toast ' + type + ' show';
              setTimeout(() => t.classList.remove('show'), 2500);
            }

            // ---- Status tab + header indicators ----
            // Каждый шлюз идентифицируется своим уникальным id — группировать/
            // подписывать типом поверх него не нужно, id и так самодостаточен.

            function statusClass(h, gwType) {
              if (!h) return 'warn';
              // "connected" отслеживается только для AMI-шлюзов (постоянный сокет).
              // goip и telegram — stateless/polling, для них ориентируемся на активность/ошибки.
              const isAmi = (gwType === 'yeastar' || gwType === 'quectel');
              if (isAmi && h.connected === false) return 'err';
              const lastErr = h.lastErrorAt ? new Date(h.lastErrorAt).getTime() : 0;
              const lastAct = h.lastActivityAt ? new Date(h.lastActivityAt).getTime() : 0;
              if (lastErr > lastAct) return 'err';
              if (!h.lastActivityAt) return 'warn';
              return 'ok';
            }

            function gwLabel(gw) {
              return gw.id;
            }

            async function loadStatus() {
              try {
                const res = await fetch('/api/status');
                if (!res.ok) return;
                const data = await res.json();
                renderHeaderStatus(data);
                renderStatusTab(data);
              } catch (e) {
                console.error(e);
              }
            }

            function renderHeaderStatus(data) {
              const el = document.getElementById('header-status');
              el.innerHTML = data.gateways.map(gw => {
                const h = data.health[gw.id];
                const cls = statusClass(h, gw.type);
                const title = t('web.status.last_activity') + ' ' + (h && h.lastActivityAt || t('web.never')) +
                              (h && h.lastError ? ('\n' + t('web.status.last_error', h.lastErrorAt || '') + ' ' + h.lastError) : '');
                return `<div class="hstat" title="${esc(title)}"><span class="hdot ${cls}"></span>${esc(gwLabel(gw))}</div>`;
              }).join('');
            }

            function renderStatusTab(data) {
              const cardsEl = document.getElementById('status-cards');
              cardsEl.innerHTML = data.gateways.map(gw => {
                const h = data.health[gw.id];
                const cls = statusClass(h, gw.type);
                let extra = (gw.type === 'telegram')
                  ? `<div class="row">${esc(t('web.status.queue_pending'))} <b>${data.tgQueuePending}</b></div>`
                  : (gw.ip ? `<div class="row">${esc(t('web.status.address'))} <b>${esc(gw.ip)}</b></div>` : '');
                return `<div class="status-card">
                  <h3><span class="hdot ${cls}"></span>${esc(gwLabel(gw))}</h3>
                  ${extra}
                  <div class="row">${esc(t('web.status.last_activity'))} <b>${esc(h && h.lastActivityAt || t('web.never'))}</b></div>
                  ${h && h.lastError ? `<div class="row err">${esc(t('web.status.last_error', h.lastErrorAt || ''))} ${esc(h.lastError)}</div>` : ''}
                </div>`;
              }).join('') + `<div class="status-card"><h3>${esc(t('web.status.sms_queue'))}</h3><div class="row">${esc(t('web.status.pending'))} <b>${data.smsQueuePending}</b></div></div>`
                + (data.calls ? `<div class="status-card"><h3>${esc(t('web.status.calls'))}</h3>
                    <div class="row">${esc(t('web.status.calls_total'))} <b>${data.calls.total}</b></div>
                    <div class="row">${esc(t('web.status.calls_tr', data.calls.transcribeDone, data.calls.transcribePending, data.calls.transcribeError))}</div>
                    <div class="row">${esc(t('web.status.calls_tg'))} <b>${data.calls.tgPending}</b></div></div>` : '');

              const statsBody = document.getElementById('status-stats-body');
              statsBody.innerHTML = data.stats.map(s =>
                `<tr><td>${esc(s.channel)}</td><td>${s.in}</td><td>${s.out}</td></tr>`
              ).join('') || `<tr><td colspan="3" style="color:var(--muted)">${esc(t('web.status.no_data'))}</td></tr>`;
            }

            // ---- Calls tab (mode "full") ----

            async function loadMode() {
              const res = await fetch('/api/mode');
              const m = await res.json();
              if (m.mode === 'full') {
                document.getElementById('tab-calls').style.display = '';
                switchTab('calls');
                // Пока есть звонки в очереди на расшифровку — обновляем, чтобы ⏳ сменился текстом.
                setInterval(() => {
                  if (document.getElementById('panel-calls').classList.contains('active') &&
                      document.querySelector('.call [data-tr="pending"]')) loadCalls(true);
                }, 15000);
              } else {
                loadSms();
              }
            }

            function fmtDur(s) { return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0'); }

            function fmtTs(ts) {
              // "2026-09-26 13:16:43" -> "26.09 13:16" (год — только если не текущий)
              const [d, t] = ts.split(' ');
              const [y, mo, da] = d.split('-');
              const year = (y != new Date().getFullYear()) ? '.' + y.slice(2) : '';
              return `${da}.${mo}${year} ${t.slice(0, 5)}`;
            }

            function highlight(text, q) {
              let html = esc(text);
              const words = (q || '').split(/\s+/).map(w => w.replace(/[^\p{L}\p{N}]/gu, '')).filter(w => w.length > 1);
              for (const w of words)
                html = html.replace(new RegExp('(' + w + ')', 'giu'), '<mark>$1</mark>');
              return html;
            }

            // Иконки кнопок — SVG одним контурным стилем, не символы шрифта: одинаково везде, без интернета.
            const svg = d => `<svg viewBox="0 0 24 24">${d}</svg>`;
            const ICON_PLAY = svg('<path d="M7 4.5v15l12.5-7.5z" fill="currentColor" stroke="none"/>');
            const ICON_DOWNLOAD = svg('<path d="M12 3v12"/><path d="M7 10l5 5 5-5"/><path d="M4 17v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-2"/>');
            // Расшифровка: документ со строками — готова; часы — в очереди; восклицательный знак — ошибка.
            const ICON_TEXT = svg('<path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5"/><path d="M9 13h6M9 17h6"/>');
            const ICON_PENDING = svg('<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>');
            const ICON_ERROR = svg('<circle cx="12" cy="12" r="9"/><path d="M12 7.5v5.5"/><path d="M12 16.5h.01"/>');

            let callsCache = {};

            async function loadCalls(keepOpen) {
              const q = document.getElementById('calls-q').value;
              const params = new URLSearchParams({ limit: 500 });
              const ch = document.getElementById('calls-channel').value;
              const dir = document.getElementById('calls-direction').value;
              const from = readDate('calls-from');
              const to = readDate('calls-to');
              if (from === false || to === false) return;
              if (ch) params.set('channel', ch);
              if (dir) params.set('direction', dir);
              if (document.getElementById('calls-missed').checked) params.set('missed', 'true');
              if (document.getElementById('calls-rec').checked) params.set('rec', 'true');
              if (q) params.set('q', q);
              if (from) params.set('from', from);
              if (to) params.set('to', to);

              if (keepOpen && document.querySelector('.call audio')) return;   // не сбиваем воспроизведение
              const open = keepOpen
                ? [...document.querySelectorAll('.call-text:not([hidden])')].map(e => e.closest('.call').dataset.id)
                : [];

              const res = await fetch('/api/calls/list?' + params.toString());
              const rows = await res.json();
              callsCache = Object.fromEntries(rows.map(r => [r.id, r]));
              document.getElementById('calls-count').textContent = t('web.calls.count', rows.length);
              document.getElementById('calls-container').innerHTML =
                rows.map(formatCall).join('') || `<div style="color:var(--muted);padding:16px">${esc(t('web.calls.nothing'))}</div>`;
              if (q) rows.filter(r => r.trState === 'done').forEach(r => toggleText(r.id, true));
              open.forEach(id => toggleText(id, true));
            }

            function formatCall(r) {
              const dirCls = r.missed ? 'missed' : r.direction;
              const dirIcon = r.missed ? '✕' : (r.direction === 'in' ? '↙' : '↗');
              const dirTitle = t(r.missed ? (r.direction === 'in' ? 'web.calls.kind.missed' : 'web.calls.kind.no_answer')
                                          : (r.direction === 'in' ? 'web.calls.kind.in' : 'web.calls.kind.out'));
              const who = r.peerName
                ? `<b>${esc(r.peerName)}</b><span class="num">${esc(r.peer)}</span>`
                : `<b class="num" style="margin:0">${esc(r.peer)}</b>`;
              const rec = r.hasRecording;
              // Расшифровка: готова — кнопка скачивает её текстом (открывается она кликом по строке);
              // в очереди — часы; не удалась — раскрывает строку с ошибкой и «Повторить».
              const trBtn = {
                done: `<a class="icon-btn" data-tr="done" title="${esc(t('web.calls.transcript_download'))}" href="/api/calls/${r.id}/transcript.pdf">${ICON_TEXT}</a>`,
                pending: `<span class="icon-btn" data-tr="pending" title="${esc(t('web.calls.tr_pending'))}">${ICON_PENDING}</span>`,
                error: `<button class="icon-btn err" data-tr="error" title="${esc(t('web.calls.tr_failed', r.trError || ''))}" onclick="toggleText(${r.id})">${ICON_ERROR}</button>`
              }[r.trState] || '';
              return `<div class="call" data-id="${r.id}">
                <div class="call-main" onclick="rowClick(event, ${r.id})">
                  <span class="call-ts" data-line="${esc(r.channel)}">${esc(fmtTs(r.ts))}</span>
                  <span class="call-dir ${dirCls}" title="${esc(dirTitle)}">${dirIcon}</span>
                  <span class="call-line">${esc(r.channel)}</span>
                  <span class="call-who" title="${esc(r.peerName || '')} ${esc(r.peer)}">${who}</span>
                  <span class="call-dur">${r.missed ? '' : fmtDur(r.billsec)}</span>
                  <span class="call-actions">
                    <button class="icon-btn ${rec ? '' : 'off'}" title="${esc(t('web.calls.play'))}" onclick="playCall(${r.id})">${ICON_PLAY}</button>
                    <a class="icon-btn ${rec ? '' : 'off'}" title="${esc(t('web.calls.download'))}" href="/api/calls/${r.id}/audio?download=true">${ICON_DOWNLOAD}</a>
                    ${trBtn}
                  </span>
                </div>
                <div class="call-player"></div>
                <div class="call-text" hidden></div>
              </div>`;
            }

            // Клик по строке звонка (не по кнопкам) — развернуть/свернуть расшифровку.
            function rowClick(e, id) {
              if (e.target.closest('.call-actions')) return;
              const r = callsCache[id];
              if (r && r.trState) toggleText(id);
            }

            function playCall(id, at) {
              const box = document.querySelector(`.call[data-id="${id}"] .call-player`);
              let a = box.querySelector('audio');
              if (!a) {
                document.querySelectorAll('.call audio').forEach(x => { x.pause(); x.remove(); });
                box.innerHTML = `<audio controls autoplay preload="auto" src="/api/calls/${id}/audio"></audio>`;
                a = box.querySelector('audio');
                a.addEventListener('ended', e => e.target.remove());
              }
              if (at != null) {
                const seek = () => { a.currentTime = at; a.play(); };
                if (a.readyState >= 1) seek(); else a.addEventListener('loadedmetadata', seek, { once: true });
              }
            }

            function fmtSec(x) { x = Math.floor(x); return Math.floor(x / 60) + ':' + String(x % 60).padStart(2, '0'); }

            // Дата из текстового поля → yyyy-mm-dd для API. Понимает дд.мм.гггг, дд.мм.гг, дд.мм
            // (текущий год) и yyyy-mm-dd. '' — поле пустое; false — не распознана (поле краснеет).
            function readDate(id) {
              const el = document.getElementById(id);
              const v = el.value.trim();
              el.classList.remove('bad');
              if (!v) return '';
              let m, y, mo, d;
              if ((m = v.match(/^(\d{4})-(\d{1,2})-(\d{1,2})$/))) [, y, mo, d] = m;
              else if ((m = v.match(/^(\d{1,2})[.\/](\d{1,2})(?:[.\/](\d{2}|\d{4}))?$/))) {
                [, d, mo, y] = m;
                y = !y ? String(new Date().getFullYear()) : (y.length === 2 ? '20' + y : y);
              }
              const dt = m && new Date(+y, +mo - 1, +d);
              if (!dt || dt.getMonth() !== +mo - 1 || dt.getDate() !== +d) {
                el.classList.add('bad');
                showToast(t('web.calls.date_invalid', v), 'err');
                return false;
              }
              return `${y}-${String(mo).padStart(2, '0')}-${String(d).padStart(2, '0')}`;
            }

            // Расшифровка как переписка: все реплики слева, мои — синим, собеседника — серым.
            // Старые моно-записи — просто реплики по времени, без стороны.
            function renderDialog(r, q) {
              let segs = [];
              try { segs = JSON.parse(r.trSegments || '[]'); } catch (e) {}
              if (!segs.length) return null;
              const them = r.peerName || t('web.calls.them');
              return segs.map(s => {
                const cls = s.who || 'mono';
                const who = s.who === 'me' ? esc(t('web.calls.me')) : (s.who === 'them' ? esc(them) : '');
                return `<div class="dl ${cls}">
                  <div class="hdr">${who ? who + ' · ' : ''}<a onclick="playCall(${r.id}, ${s.s})" title="${esc(t('web.calls.play_from'))}">${fmtSec(s.s)}</a></div>
                  <div class="bubble">${highlight(s.t, q)}</div>
                </div>`;
              }).join('');
            }

            function toggleText(id, forceOpen) {
              const r = callsCache[id];
              const el = document.querySelector(`.call[data-id="${id}"] .call-text`);
              if (!r || !el) return;
              if (!el.hidden && !forceOpen) { el.hidden = true; return; }
              const q = document.getElementById('calls-q').value;
              el.classList.remove('dialog');
              if (r.trState === 'done') {
                const dialog = renderDialog(r, q);
                if (dialog) { el.innerHTML = dialog; el.classList.add('dialog'); }
                else el.innerHTML = r.trText ? highlight(r.trText, q) : `<span style="color:var(--muted)">${esc(t('web.calls.no_speech'))}</span>`;
                // Перерасшифровать — например, после правки подсказки (calls.transcribe.prompt).
                el.insertAdjacentHTML('beforeend', `<div class="meta tr-actions">
                  <button class="btn" onclick="retranscribe(${id})">↻ ${esc(t('web.calls.retranscribe'))}</button>
                  <a class="btn" href="/api/calls/${id}/transcript">${esc(t('web.calls.download_txt'))}</a></div>`);
              }
              else if (r.trState === 'pending')
                el.innerHTML = `<span style="color:var(--muted)">⏳ ${esc(t('web.calls.tr_pending'))}</span>`;
              else
                el.innerHTML = `<span style="color:var(--red)">✖ ${esc(t('web.calls.tr_failed', r.trError || ''))}</span>
                  <div class="meta"><button class="btn" onclick="retranscribe(${id})">${esc(t('web.calls.retry'))}</button></div>`;
              el.hidden = false;
            }

            async function retranscribe(id) {
              const res = await fetch(`/api/calls/${id}/retranscribe`, { method: 'POST' });
              const d = await res.json();
              showToast(t(d.ok ? 'web.calls.requeued' : 'web.calls.no_recording'), d.ok ? 'ok' : 'err');
              loadCalls(true);
            }

            let callsDebounce = null;
            function debouncedLoadCalls() {
              clearTimeout(callsDebounce);
              callsDebounce = setTimeout(loadCalls, 350);
            }

            loadChannels().then(() => {
              document.getElementById('calls-channel').innerHTML = `<option value="">${esc(t('web.calls.all_lines'))}</option>` +
                channelsCache.map(c => `<option value="${esc(c.name)}">${esc(c.name)}</option>`).join('');
            });
            loadMode();
            loadStatus();
            setInterval(loadStatus, 15000);
            setLogRefresh();
            </script>
            </body>
            </html>
            """;
    }
}
