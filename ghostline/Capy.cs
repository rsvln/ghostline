using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ghostline
{
    // Capy channel (a self-hosted messenger, docs/capy.md): ghostline is a bot there.
    //  - out: SMS, alerts and replies to commands go through the same persistent queues as Telegram, with
    //    the target "capy:<name>" (clients.capy.targets[].name) or "capy:<recipient>" (a reply into a chat);
    //  - calls: Capy posts its own call records (its SIP bridge is a phone of the line); a phone line's bot
    //    (a target with its own token) adds the transcript under the record, found by the PBX call id
    //    (X-Asterisk-CallId);
    //  - in: the bot's chats are polled (GET /chats, then /chats/{id}/events?after=seq), so ghostline needs
    //    no address reachable from the Capy server. A message written to the bot is an SMS to send
    //    (ParseSmsCommand) or a command (/status, /help).
    internal partial class Program
    {
        private const string CapyPrefix = "capy:";
        // The bot's owner, the recipient of a target without "to". Resolved at send time: /bot/me tells who
        // the owner is.
        internal const string CapyOwner = "owner";
        // The chat of a phone line's bot (a target with its own token and no "to"); resolved from /bot/me.
        private const string CapyBotChat = "bot-chat";
        private const string KvCapySeq = "capy.seq.";

        private static readonly HttpClient capyHttp = new() { Timeout = TimeSpan.FromMinutes(2) };

        // On with a server and a token: the main bot's, or a phone line bot's.
        internal static bool CapyEnabled =>
            !string.IsNullOrWhiteSpace(settings?.clients?.capy?.url)
            && (CapyMainBot || (settings.clients.capy.targets ?? new()).Any(t => !string.IsNullOrWhiteSpace(t.token)));

        internal static bool CapyMainBot => !string.IsNullOrWhiteSpace(settings?.clients?.capy?.token);

        private static string CapyTarget(string to) => CapyPrefix + to;
        private static bool IsCapyTarget(string target) => target != null && target.StartsWith(CapyPrefix);

        // Who the bot is, from /bot/me (the health loop); null until the first answer.
        private static Guid? _capyBotId;
        private static Guid? _capyOwnerId;
        private static string _capyOwnerName;

        // "@Alice" and "alice" are the same person; a chat id stays as it is; a chat's number (the one in its
        // link, capy.example.com/chat/13) becomes "#13".
        internal static string CapyRecipient(string to)
        {
            to = (to ?? "").Trim();
            if (to.Length == 0) return null;
            if (Guid.TryParse(to, out var id)) return id.ToString();
            if (to.TrimStart('#').All(char.IsAsciiDigit) && to.TrimStart('#').Length > 0) return "#" + long.Parse(to.TrimStart('#'));
            if (to.Equals(CapyOwner, StringComparison.OrdinalIgnoreCase)) return CapyOwner;
            return "@" + to.TrimStart('@').ToLowerInvariant();
        }

        private static IEnumerable<CapyChat> CapyChats =>
            CapyEnabled ? (settings.clients.capy.targets ?? new()).Where(t => !string.IsNullOrWhiteSpace(t.name)
                && (CapyMainBot || !string.IsNullOrWhiteSpace(t.token))) : Enumerable.Empty<CapyChat>();

        private static bool OwnBot(CapyChat t) => !string.IsNullOrWhiteSpace(t.token);

        // Names of the targets that get the line's SMS.
        internal static List<string> LineCapy(Channel ch) =>
            CapyChats.Where(t => Subscription.Covers(t.sms, ch)).Select(t => t.name.Trim()).Distinct().ToList();

        // Phone line bots that add the transcripts of the line's calls to Capy's call records.
        internal static List<string> LineCapyTranscripts(Channel ch) =>
            CapyChats.Where(t => OwnBot(t) && Subscription.Covers(t.transcripts, ch)).Select(t => t.name.Trim()).Distinct().ToList();

        internal static List<string> CapyAlertTargets => CapyChats.Where(t => t.alerts).Select(t => t.name.Trim()).Distinct().ToList();

        // A queued "capy:<key>": a target by its name (its bot's token, its recipient), or else a recipient
        // of the main bot (a reply into the chat a message came from, rows queued before 2026-11-27).
        private static (string token, string to) CapyRoute(string key)
        {
            var t = (settings.clients.capy.targets ?? new()).FirstOrDefault(x => x.name?.Trim() == key);
            if (t == null) return (settings.clients.capy.token, CapyRecipient(key) ?? CapyOwner);
            return OwnBot(t)
                ? (t.token, CapyRecipient(t.to) ?? CapyBotChat)
                : (settings.clients.capy.token, CapyRecipient(t.to) ?? CapyOwner);
        }

        // Lines a chat of the main bot serves: the targets that are that chat (its id or number; a direct chat
        // by the person in it: @username, or the owner), and the lines they get SMS of.
        internal static List<Channel> LinesOfCapyChat(Guid chatId, long number, bool direct, string username, bool isOwner)
        {
            string user = username == null ? null : CapyRecipient(username);
            var mine = CapyChats.Where(t => !OwnBot(t)).Where(t =>
            {
                string to = CapyRecipient(t.to) ?? CapyOwner;
                return to == chatId.ToString() || (number > 0 && to == "#" + number)
                    || (direct && ((user != null && to == user) || (isOwner && to == CapyOwner)));
            }).ToList();
            return AllLines.Where(l => mine.Any(t => Subscription.Covers(t.sms, l))).ToList();
        }

        private static void EnqueueCapyForLine(Channel line, string text)
        {
            foreach (string name in LineCapy(line))
                Store.EnqueueTelegramMessage(CapyTarget(name), text);
        }

        // ---- HTTP ----

        private static string CapyApi(string path) => settings.clients.capy.url.TrimEnd('/') + "/api/v1" + path;

        // token: a phone line bot's; null: the main bot's.
        private static async Task<JToken> CapyRequest(HttpMethod method, string path, object body = null, HttpContent content = null,
                                                      Action<HttpRequestMessage> setup = null, string token = null)
        {
            using var req = new HttpRequestMessage(method, CapyApi(path));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (token ?? settings.clients.capy.token ?? "").Trim());
            if (body != null)
                req.Content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            else if (content != null)
                req.Content = content;
            setup?.Invoke(req);
            using var resp = await capyHttp.SendAsync(req);
            string text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Capy HTTP {(int)resp.StatusCode}: {Trim(text.Trim(), 200)}", null, resp.StatusCode);
            return text.Length == 0 ? null : JToken.Parse(text);
        }

        // The same key for every retry of one queued item: the server then sends it once.
        internal static Guid CapyMsgId(string key) =>
            new(MD5.HashData(Encoding.UTF8.GetBytes("ghostline:" + key)));

        // Chat number -> chat id, from the chat list (filled by the poll loop, refreshed when a number is missing).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, Guid> _capyChatNumbers = new();

        private static void RememberCapyChats(JArray chats)
        {
            foreach (var c in chats)
                if ((long?)c["number"] is > 0 and var n)
                    _capyChatNumbers[n] = (Guid)c["id"];
        }

        // The chat of a phone line's bot, by its token (GET /bot/me gives chatId).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Guid> _capyBotChats = new();

        private static async Task<Guid> CapyBotChatOf(string token)
        {
            if (_capyBotChats.TryGetValue(token, out var id)) return id;
            var me = await CapyRequest(HttpMethod.Get, "/bot/me", token: token);
            id = (Guid?)me["chatId"] ?? throw new InvalidOperationException("this Capy bot is not a phone line's bot (no chatId)");
            _capyBotChats[token] = id;
            return id;
        }

        // A recipient for /bot/send: @username or a chat id; the owner by name, a chat number by its id.
        private static async Task<string> CapyTo(string to, string token = null)
        {
            if (to == CapyBotChat)
                return (await CapyBotChatOf(token)).ToString();
            if (to == CapyOwner)
            {
                if (_capyOwnerName == null) await CapyWhoAmI();
                return "@" + _capyOwnerName;
            }
            if (to.StartsWith('#') && long.TryParse(to.AsSpan(1), out long number))
            {
                if (!_capyChatNumbers.ContainsKey(number))
                    RememberCapyChats((JArray)await CapyRequest(HttpMethod.Get, "/chats"));
                return _capyChatNumbers.TryGetValue(number, out var id) ? id.ToString()
                    : throw new InvalidOperationException($"the bot is not in Capy chat {number}");
            }
            return to;
        }

        // (chat id, message id) of the sent message. target: what follows "capy:" in the queue.
        private static async Task<(Guid chatId, long messageId)> CapySend(string target, string text, string key, Guid? attachmentId = null)
        {
            var (token, to) = CapyRoute(target);
            var r = await CapyRequest(HttpMethod.Post, "/bot/send", new
            {
                to = await CapyTo(to, token),
                text = CapyText(text),
                attachmentIds = attachmentId == null ? null : new[] { attachmentId.Value },
                clientMsgId = CapyMsgId(key)
            }, token: token);
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

        // The transcript under Capy's own record of the call: PATCH with pbxCallId finds the record in the
        // bot's chat (the message id in the path is ignored then) and replaces its note. 404: Capy posts the
        // record when the call ends, and its SIP bridge may be slower than our CDR poll: tried again later.
        private static readonly int[] CapyAttachRetryMinutes = { 1, 2, 5, 10, 15 };

        private static async Task AttachTranscriptCapy(CallTgItem item, CallRecord call)
        {
            if (call.TrState != "done" || string.IsNullOrEmpty(call.PbxCallId))
            {
                if (string.IsNullOrEmpty(call.PbxCallId))
                    SysLog.Debug("capy", $"call {call.Id}: no PBX call id, the transcript is not added to Capy");
                Store.MarkCallTgFinal(item.Id);
                return;
            }
            var (token, _) = CapyRoute(item.ChatId.Substring(CapyPrefix.Length));
            try
            {
                Guid chatId = await CapyBotChatOf(token);
                await CapyRequest(new HttpMethod("PATCH"), $"/chats/{chatId}/messages/0",
                    new { text = CapyTranscriptNote(call), pbxCallId = call.PbxCallId }, token: token);
                HealthStatus.MarkActivity("capy");
                Store.MarkCallTgFinal(item.Id);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                if (item.Attempts >= CapyAttachRetryMinutes.Length)
                {
                    SysLog.Warn("capy", $"call {call.Id}: no Capy call record with id {call.PbxCallId}, the transcript is not added");
                    Store.MarkCallTgFinal(item.Id);
                    return;
                }
                Store.DeferCallTg(item.Id, "no call record yet", TimeSpan.FromMinutes(CapyAttachRetryMinutes[item.Attempts]));
            }
        }

        // The transcript under a cut (">>> title" ... "<<<"), within Capy's text limit.
        internal static string CapyTranscriptNote(CallRecord call)
        {
            string head = ">>> " + L10n.Tg.T("tg.call.transcript_cut") + "\n";
            const string tail = "\n<<<";
            string body = TranscriptText(call);
            int room = CapyTextLimit - head.Length - tail.Length;
            if (body.Length > room) body = body.Substring(0, room - 1) + "…";
            return head + body + tail;
        }

        private static async Task EditCallCapy(CallTgItem item, CallRecord call)
        {
            if (item.Kind == "transcript")
            {
                await AttachTranscriptCapy(item, call);
                return;
            }
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
                    // Every phone line bot's token is checked too: a revoked one would lose transcripts silently.
                    foreach (var t in CapyChats.Where(OwnBot).Select(t => t.token.Trim()).Distinct())
                        _capyBotChats[t] = (Guid?)(await CapyRequest(HttpMethod.Get, "/bot/me", token: t))["chatId"]
                            ?? throw new InvalidOperationException("a target's token is not a phone line's bot (no chatId)");
                    if (!CapyMainBot)
                    {
                        HealthStatus.MarkConnected("capy");
                        await Task.Delay(TimeSpan.FromMinutes(1));
                        continue;
                    }
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
                    RememberCapyChats(chats);
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
                                HandleCapyMessage(chatId, (long?)chat["number"] ?? 0, (string)chat["type"] == "Direct", name,
                                    sender == _capyOwnerId, (string)msg["text"] ?? "");
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
        private static void HandleCapyMessage(Guid chatId, long chatNumber, bool direct, string username, bool isOwner, string message)
        {
            string reply = CapyTarget(chatId.ToString());
            var lines = LinesOfCapyChat(chatId, chatNumber, direct, username, isOwner);
            var l = L10n.Tg;
            if (lines.Count == 0)
            {
                // Group chats are not answered: the bot may sit in a group for other reasons. The log says
                // what would make the chat a line's: a line that uses Capy at all, or the chat as a recipient.
                string who = direct ? $"@{username}" : $"group {chatNumber} ({chatId})";
                string fix = direct ? "not a target with lines in sms (clients.capy.targets)"
                    : $"not a target with lines in sms (add a target with to: \"#{chatNumber}\" to clients.capy.targets)";
                SysLog.Info("capy", $"message from {who}: {fix}; " + (direct ? "refused" : "ignored"));
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
