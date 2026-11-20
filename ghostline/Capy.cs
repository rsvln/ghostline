using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ghostline
{
    // Capy channel (a self-hosted messenger, docs/capy.md): ghostline is a bot there.
    //  - out: SMS, calls (a voice note, the transcript added later by editing the message), line and PBX
    //    alerts and replies to commands go through the same persistent queues as Telegram, with the target
    //    "capy:<recipient>" (@username, a chat id, or the bot's owner);
    //  - in: the bot's chats are polled (GET /chats, then /chats/{id}/events?after=seq), so ghostline needs
    //    no address reachable from the Capy server. A message written to the bot is an SMS to send
    //    (ParseSmsCommand) or a command (/status, /help).
    internal partial class Program
    {
        private const string CapyPrefix = "capy:";
        // The bot's owner, the recipient when neither clients.capy.to nor the line's to is set. Resolved at
        // send time: /bot/me tells who the owner is.
        internal const string CapyOwner = "owner";
        private const string KvCapySeq = "capy.seq.";

        private static readonly HttpClient capyHttp = new() { Timeout = TimeSpan.FromMinutes(2) };

        internal static bool CapyEnabled =>
            !string.IsNullOrWhiteSpace(settings?.clients?.capy?.url) && !string.IsNullOrWhiteSpace(settings?.clients?.capy?.token);

        private static string CapyTarget(string to) => CapyPrefix + to;
        private static bool IsCapyTarget(string target) => target != null && target.StartsWith(CapyPrefix);

        // Who the bot is, from /bot/me (the health loop); null until the first answer.
        private static Guid? _capyBotId;
        private static Guid? _capyOwnerId;
        private static string _capyOwnerName;

        // "@Alice" and "alice" are the same person; a chat id stays as it is.
        internal static string CapyRecipient(string to)
        {
            to = (to ?? "").Trim();
            if (to.Length == 0) return null;
            if (Guid.TryParse(to, out var id)) return id.ToString();
            if (to.Equals(CapyOwner, StringComparison.OrdinalIgnoreCase)) return CapyOwner;
            return "@" + to.TrimStart('@').ToLowerInvariant();
        }

        private static List<string> CapyList(List<string> to) =>
            (to ?? new()).Select(CapyRecipient).Where(x => x != null).Distinct().ToList();

        // The common recipients: clients.capy.to, or the bot's owner.
        internal static List<string> CapyCommonTo() =>
            CapyList(settings.clients.capy.to) is { Count: > 0 } list ? list : new List<string> { CapyOwner };

        // Recipients of a line: its own to, or the common ones. Empty: the line does not go to Capy.
        internal static List<string> LineCapy(Channel ch) =>
            !CapyEnabled || !LineUses(ch, "capy") ? new List<string>()
            : CapyList(ch.clients?.capy?.to) is { Count: > 0 } own ? own
            : CapyCommonTo();

        // Lines a chat serves: a group by its id; a direct chat by the person in it (@username, or the owner).
        internal static List<Channel> LinesOfCapyChat(Guid chatId, bool direct, string username, bool isOwner)
        {
            string user = username == null ? null : CapyRecipient(username);
            return AllLines.Where(c =>
            {
                var to = LineCapy(c);
                return to.Contains(chatId.ToString())
                    || (direct && ((user != null && to.Contains(user)) || (isOwner && to.Contains(CapyOwner))));
            }).ToList();
        }

        private static void EnqueueCapyForLine(Channel line, string text)
        {
            foreach (string to in LineCapy(line))
                Store.EnqueueTelegramMessage(CapyTarget(to), text);
        }

        // ---- HTTP ----

        private static string CapyApi(string path) => settings.clients.capy.url.TrimEnd('/') + "/api/v1" + path;

        private static async Task<JToken> CapyRequest(HttpMethod method, string path, object body = null, HttpContent content = null,
                                                      Action<HttpRequestMessage> setup = null)
        {
            using var req = new HttpRequestMessage(method, CapyApi(path));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.clients.capy.token.Trim());
            if (body != null)
                req.Content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            else if (content != null)
                req.Content = content;
            setup?.Invoke(req);
            using var resp = await capyHttp.SendAsync(req);
            string text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Capy HTTP {(int)resp.StatusCode}: {Trim(text.Trim(), 200)}");
            return text.Length == 0 ? null : JToken.Parse(text);
        }

        // The same key for every retry of one queued item: the server then sends it once.
        internal static Guid CapyMsgId(string key) =>
            new(MD5.HashData(Encoding.UTF8.GetBytes("ghostline:" + key)));

        // A recipient for /bot/send: @username or a chat id; the owner by name.
        private static async Task<string> CapyTo(string to)
        {
            if (to != CapyOwner) return to;
            if (_capyOwnerName == null) await CapyWhoAmI();
            return "@" + _capyOwnerName;
        }

        // (chat id, message id) of the sent message.
        private static async Task<(Guid chatId, long messageId)> CapySend(string to, string text, string key, Guid? attachmentId = null)
        {
            var r = await CapyRequest(HttpMethod.Post, "/bot/send", new
            {
                to = await CapyTo(to),
                text = CapyText(text),
                attachmentIds = attachmentId == null ? null : new[] { attachmentId.Value },
                clientMsgId = CapyMsgId(key)
            });
            HealthStatus.MarkActivity("capy");
            return ((Guid)r["chatId"], (long)r["messageId"]);
        }

        private static async Task CapyEdit(Guid chatId, long messageId, string text)
        {
            await CapyRequest(HttpMethod.Patch, $"/chats/{chatId}/messages/{messageId}", new { text = CapyText(text) });
            HealthStatus.MarkActivity("capy");
        }

        // A file in one chunk (a call recording is far below the 32 MiB a chunk may have). kind "Voice" makes
        // it a voice note with a player and a waveform; otherwise the server tells the kind itself.
        private static async Task<Guid> CapyUpload(byte[] data, string fileName, string contentType, string kind = null,
                                                   int? durationMs = null, string waveform = null)
        {
            var up = await CapyRequest(HttpMethod.Post, "/uploads", new
            {
                fileName, contentType, size = data.LongLength, kind, durationMs, waveform
            });
            Guid id = (Guid)up["id"];
            await CapyRequest(new HttpMethod("PATCH"), $"/uploads/{id}", content: new ByteArrayContent(data),
                setup: req =>
                {
                    req.Headers.Add("Upload-Offset", "0");
                    req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
                });
            var att = await CapyRequest(HttpMethod.Post, $"/uploads/{id}/complete");
            return (Guid)att["id"];
        }

        // Capy's text limit is 16000 characters; nothing ghostline sends comes close but a long transcript.
        private const int CapyTextLimit = 15000;

        private static string CapyText(string text)
        {
            text ??= "";
            return text.Length <= CapyTextLimit ? text : text.Substring(0, CapyTextLimit) + "\n…";
        }

        // Telegram HTML (help and status) to Capy's markup: **bold**, `code`, ```blocks```, bare links.
        internal static string HtmlToCapy(string html)
        {
            string s = Regex.Replace(html, @"<a href=""([^""]*)"">(.*?)</a>", m => m.Groups[2].Value == m.Groups[1].Value
                ? m.Groups[1].Value : $"{m.Groups[2].Value} {m.Groups[1].Value}");
            s = Regex.Replace(s, @"</?b>", "**");
            s = Regex.Replace(s, @"<pre>(.*?)</pre>", "```\n$1\n```", RegexOptions.Singleline);
            s = Regex.Replace(s, @"</?code>", "`");
            s = Regex.Replace(s, @"<[^>]+>", "");
            return System.Net.WebUtility.HtmlDecode(s);
        }

        // ---- the queue ----

        // A queued tg_queue row whose target is Capy: plain text, as Telegram gets it.
        private static Task CapySendQueued(long id, string target, string text) =>
            CapySend(target.Substring(CapyPrefix.Length), text, "q" + id);

        // ---- calls ----

        private static async Task SendCallCapy(CallTgItem item, CallRecord call)
        {
            string to = item.ChatId.Substring(CapyPrefix.Length);
            bool trFinished = call.TrState is "done" or "error";
            string head = CallHeader(call);
            string text = item.WantTranscript ? head + TranscriptPart(call) : head;
            Guid? attachment = null;
            if (item.Kind == "voice")
            {
                byte[] wav = await Recordings.BalancedForCall(call);
                if (wav != null && wav.Length > Store.MinRecordingBytes)
                    attachment = await CapyRecording(call, wav);
            }
            var (chatId, messageId) = await CapySend(to, text, "call" + item.Id, attachment);
            // The chat id goes where ntfy keeps its attachment URL: the edit needs both.
            Store.MarkCallTgSent(item.Id, messageId, final: !item.WantTranscript || trFinished, ext: chatId.ToString());
        }

        // A voice note (ogg/opus) with a waveform; MP3 or the WAV itself if ffmpeg fails.
        private static async Task<Guid> CapyRecording(CallRecord call, byte[] wav)
        {
            string name = CallFileBase(call);
            int durationMs = call.Billsec * 1000;
            byte[] ogg = await Recordings.ToOggOpus(wav);
            if (ogg != null)
                return await CapyUpload(ogg, name + ".ogg", "audio/ogg", "Voice", durationMs, Recordings.Waveform(wav));
            byte[] mp3 = await Recordings.ToMp3(wav);
            return mp3 != null
                ? await CapyUpload(mp3, name + ".mp3", "audio/mpeg", durationMs: durationMs)
                : await CapyUpload(wav, name + ".wav", "audio/wav", durationMs: durationMs);
        }

        private static async Task EditCallCapy(CallTgItem item, CallRecord call)
        {
            if (!Guid.TryParse(item.Ext, out var chatId) || item.MessageId == 0)
            {
                Store.MarkCallTgFinal(item.Id);
                return;
            }
            await CapyEdit(chatId, item.MessageId, CallHeader(call) + TranscriptPart(call));
            Store.MarkCallTgFinal(item.Id);
        }

        // ---- who the bot is; health ----

        private static async Task CapyWhoAmI()
        {
            var me = await CapyRequest(HttpMethod.Get, "/bot/me");
            _capyBotId = (Guid)me["id"];
            var owner = (Guid)me["ownerId"];
            if (owner != _capyOwnerId || _capyOwnerName == null)
            {
                var user = await CapyRequest(HttpMethod.Get, $"/users/{owner}");
                _capyOwnerName = ((string)user["username"])?.ToLowerInvariant();
                _capyOwnerId = owner;
            }
        }

        // /bot/me once a minute, like Telegram's getMe: Capy is "connected" while that works.
        private static async Task CapyHealthLoop()
        {
            bool commandsSet = false;
            while (true)
            {
                try
                {
                    await CapyWhoAmI();
                    HealthStatus.MarkConnected("capy");
                    if (!commandsSet)
                    {
                        await CapyRequest(HttpMethod.Put, "/bot/commands", new[]
                        {
                            new { command = "status", description = L10n.Tg.T("tg.cmd.status") },
                            new { command = "help", description = L10n.Tg.T("tg.cmd.help") },
                        });
                        commandsSet = true;
                    }
                }
                catch (Exception ex)
                {
                    HealthStatus.MarkDisconnected("capy");
                    HealthStatus.MarkError("capy", DescribeException(ex));
                }
                await Task.Delay(TimeSpan.FromMinutes(1));
            }
        }

        // ---- incoming: messages written to the bot ----

        // Every few seconds: the bot's chats, and the new events of those with a newer seq. The cursor per
        // chat is kept in the DB, so messages written while ghostline was down are handled after the start.
        // A chat seen for the first time starts at its current seq: its history is not replayed.
        private static async Task CapyPollLoop()
        {
            var users = new Dictionary<Guid, string>();
            while (true)
            {
                try
                {
                    if (_capyBotId == null) await CapyWhoAmI();
                    var chats = (JArray)await CapyRequest(HttpMethod.Get, "/chats");
                    foreach (var chat in chats)
                    {
                        Guid chatId = (Guid)chat["id"];
                        long last = (long)chat["lastSeq"];
                        string key = KvCapySeq + chatId;
                        if (!long.TryParse(Store.GetKv(key), out long seen))
                        {
                            Store.SetKv(key, last.ToString());
                            continue;
                        }
                        while (seen < last)
                        {
                            var page = await CapyRequest(HttpMethod.Get, $"/chats/{chatId}/events?after={seen}&limit=100");
                            var events = (JArray)page["events"];
                            foreach (var e in events)
                            {
                                seen = Math.Max(seen, (long)e["seq"]);
                                if ((string)e["type"] != "MessageNew") continue;
                                var msg = e["payload"]?["message"];
                                if (msg == null || (Guid?)msg["senderId"] is not { } sender || sender == _capyBotId) continue;
                                if (!users.TryGetValue(sender, out var name))
                                    users[sender] = name = (string)(await CapyRequest(HttpMethod.Get, $"/users/{sender}"))["username"];
                                HandleCapyMessage(chatId, (string)chat["type"] == "Direct", name, sender == _capyOwnerId,
                                    (string)msg["text"] ?? "");
                            }
                            Store.SetKv(key, seen.ToString());
                            if (events.Count == 0 || page["hasMore"]?.Value<bool>() != true) break;
                        }
                    }
                    HealthStatus.MarkConnected("capy");
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }
                catch (Exception ex)
                {
                    HealthStatus.MarkDisconnected("capy");
                    HealthStatus.MarkError("capy", DescribeException(ex));
                    SysLog.Warn("capy", "poll failed: " + DescribeException(ex) + ", retrying in 10 s");
                    await Task.Delay(TimeSpan.FromSeconds(10));
                }
            }
        }

        // A message to the bot: a command, or an SMS to send ("number / text" in a chat of one line,
        // "line / number / text" in a chat of several). Replies go into the same chat.
        private static void HandleCapyMessage(Guid chatId, bool direct, string username, bool isOwner, string message)
        {
            string reply = CapyTarget(chatId.ToString());
            var lines = LinesOfCapyChat(chatId, direct, username, isOwner);
            var l = L10n.Tg;
            if (lines.Count == 0)
            {
                // Group chats are not answered: the bot may sit in a group for other reasons.
                if (direct) Store.EnqueueTelegramMessage(reply, l.T("tg.sms.go_away"));
                return;
            }
            HealthStatus.MarkActivity("capy");
            string text = message.Replace("\r", "").Trim();
            SysLog.Debug("capy", $"message from @{username}");

            if (text.StartsWith('/'))
            {
                string cmd = text.Split(' ', '\n')[0].ToLowerInvariant();
                Store.EnqueueTelegramMessage(reply, HtmlToCapy(cmd switch
                {
                    "/help" or "/start" => HelpText(lines),
                    "/status" => StatusText(),
                    _ => l.T("tg.cmd.unknown", Html(cmd))
                }));
                return;
            }

            var (line, number, body) = ParseSmsCommand(lines, text);
            if (line == null)
            {
                Store.EnqueueTelegramMessage(reply, HtmlToCapy(WrongFormatText(lines)));
                return;
            }
            var (ok, error) = QueueSmsSend(line, number, body, "capy:@" + username);
            if (!ok)
                Store.EnqueueTelegramMessage(reply, l.T("tg.sms.send_failed", error));
        }
    }
}
