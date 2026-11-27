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
    //  - in: the chats of the main bot and of every target's own bot with lines in sms are polled (GET /chats,
    //    then /chats/{id}/events?after=seq), so ghostline needs no address reachable from the Capy server. A
    //    message written to such a bot is an SMS to send (ParseSmsCommand) or a command (/status, /help).
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

        // A bot ghostline acts as: the main one (clients.capy.token) or a target's own. What /bot/me tells
        // about it (who it is, its owner, a phone line bot's chat) and the chat numbers it knows.
        private sealed class CapyBot
        {
            public Guid? Id, OwnerId, ChatId;
            public string OwnerName;
            public bool CommandsSet;
            public readonly System.Collections.Concurrent.ConcurrentDictionary<long, Guid> Numbers = new();
            // Health: the last check's or poll's error (null: fine), and when a failing bot is polled again.
            public string Error;
            public DateTime PollAgainAt;
        }

        // By token; a token is never logged or queued, the queue names a bot by its target's name.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CapyBot> _capyBots = new();

        private static string MainCapyToken => (settings.clients.capy.token ?? "").Trim();
        private static string TokenOf(CapyChat t) => OwnBot(t) ? t.token.Trim() : MainCapyToken;
        private static CapyBot BotOf(string token) => _capyBots.GetOrAdd((token ?? MainCapyToken).Trim(), _ => new CapyBot());

        // The bots: the main one and every target's own, each once.
        private static List<string> CapyTokens =>
            (CapyMainBot ? new[] { MainCapyToken } : Array.Empty<string>())
                .Concat(CapyChats.Where(OwnBot).Select(t => t.token.Trim())).Distinct().ToList();

        // The bots whose chats are polled: the main one, and the ones with lines in sms (a phone line bot
        // only adding transcripts is not).
        private static List<string> CapyPolledTokens =>
            CapyTokens.Where(k => k == MainCapyToken || CapyChats.Any(t => TokenOf(t) == k && t.sms is { Count: > 0 })).ToList();

        // A phone line bot (a target with transcripts) has to have its chat.
        private static bool NeedsBotChat(string token) =>
            CapyChats.Any(t => OwnBot(t) && TokenOf(t) == token && t.transcripts is { Count: > 0 });

        // The name the queue knows a bot by: "" for the main one, else the first target with that token.
        private static string BotKey(string token) =>
            token == MainCapyToken ? "" : CapyChats.First(t => OwnBot(t) && TokenOf(t) == token).name.Trim();

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

        // A queued "capy:<key>": a target by its name (its bot's token, its recipient); "<recipient>|<bot>"
        // a reply into the chat a message came from, through the bot it came to (<bot>: a target's name, empty
        // for the main bot); else a recipient of the main bot (rows queued before 2026-11-27).
        private static (string token, string to) CapyRoute(string key)
        {
            var targets = settings.clients.capy.targets ?? new();
            int bar = key.LastIndexOf('|');
            if (bar >= 0 && !targets.Any(x => x.name?.Trim() == key))
            {
                string bot = key[(bar + 1)..];
                var owner = bot.Length == 0 ? null : targets.FirstOrDefault(x => x.name?.Trim() == bot && OwnBot(x));
                return (owner?.token.Trim() ?? MainCapyToken, CapyRecipient(key[..bar]) ?? CapyOwner);
            }
            var t = targets.FirstOrDefault(x => x.name?.Trim() == key);
            if (t == null) return (MainCapyToken, CapyRecipient(key) ?? CapyOwner);
            return OwnBot(t)
                ? (t.token.Trim(), CapyRecipient(t.to) ?? CapyBotChat)
                : (MainCapyToken, CapyRecipient(t.to) ?? CapyOwner);
        }

        // Lines a chat of a bot serves (token: the bot's, null: the main one): the bot's targets that are that
        // chat (its id or number; a direct chat by the person in it: @username, or the owner), and the lines
        // they get SMS of. A bot's own target without "to" is its owner too.
        internal static List<Channel> LinesOfCapyChat(Guid chatId, long number, bool direct, string username, bool isOwner,
                                                      string token = null)
        {
            token = (token ?? MainCapyToken).Trim();
            string user = username == null ? null : CapyRecipient(username);
            var mine = CapyChats.Where(t => TokenOf(t) == token).Where(t =>
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

        // Chat number -> chat id, from the bot's chat list (filled by the poll loop, refreshed when a number is
        // missing).
        private static void RememberCapyChats(CapyBot bot, JArray chats)
        {
            foreach (var c in chats)
                if ((long?)c["number"] is > 0 and var n)
                    bot.Numbers[n] = (Guid)c["id"];
        }

        // /bot/me: who the bot is, its owner (by name, for "to" empty) and a phone line bot's chat.
        private static async Task<CapyBot> CapyWhoAmI(string token)
        {
            var bot = BotOf(token);
            var me = await CapyRequest(HttpMethod.Get, "/bot/me", token: token);
            bot.Id = (Guid)me["id"];
            bot.ChatId = (Guid?)me["chatId"];
            var owner = (Guid)me["ownerId"];
            if (owner != bot.OwnerId || bot.OwnerName == null)
            {
                var user = await CapyRequest(HttpMethod.Get, $"/users/{owner}", token: token);
                bot.OwnerName = ((string)user["username"])?.ToLowerInvariant();
                bot.OwnerId = owner;
            }
            return bot;
        }

        private static async Task<CapyBot> CapyKnown(string token)
        {
            var bot = BotOf(token);
            return bot.Id == null ? await CapyWhoAmI(token) : bot;
        }

        // The chat of a phone line's bot, by its token (GET /bot/me gives chatId).
        private static async Task<Guid> CapyBotChatOf(string token) =>
            (await CapyKnown(token)).ChatId ?? throw new InvalidOperationException("this Capy bot is not a phone line's bot (no chatId)");

        // A recipient for /bot/send: @username or a chat id; the owner by name, a chat number by its id; a
        // bot's own chat (an ordinary bot has none: its owner).
        private static async Task<string> CapyTo(string to, string token = null)
        {
            var bot = await CapyKnown(token);
            if (to == CapyBotChat)
                return bot.ChatId?.ToString() ?? "@" + bot.OwnerName;
            if (to == CapyOwner)
                return "@" + bot.OwnerName;
            if (to.StartsWith('#') && long.TryParse(to.AsSpan(1), out long number))
            {
                if (!bot.Numbers.ContainsKey(number))
                    RememberCapyChats(bot, (JArray)await CapyRequest(HttpMethod.Get, "/chats", token: token));
                return bot.Numbers.TryGetValue(number, out var id) ? id.ToString()
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

        // The health of every bot in one window: a minute, the bots one after another, evenly spaced, one
        // request at a time, so Capy gets a steady trickle rather than a burst. Every bot is asked /bot/me
        // every round, polled or not. A bot with transcripts and no chat of its own is not a phone line's bot.
        private static readonly TimeSpan CapyHealthWindow = TimeSpan.FromMinutes(1);

        private static async Task CapyHealthLoop()
        {
            while (true)
            {
                var tokens = CapyTokens;
                var polled = CapyPolledTokens;
                var gap = tokens.Count > 0 ? CapyHealthWindow / tokens.Count : CapyHealthWindow;
                foreach (string token in tokens)
                {
                    var bot = BotOf(token);
                    try
                    {
                        await CapyWhoAmI(token);
                        if (bot.ChatId == null && NeedsBotChat(token))
                            throw new InvalidOperationException("has transcripts, but its token is not a phone line's bot (no chatId)");
                        if (!bot.CommandsSet && polled.Contains(token))
                        {
                            await CapyRequest(HttpMethod.Put, "/bot/commands", new[]
                            {
                                new { command = "status", description = L10n.Tg.T("tg.cmd.status") },
                                new { command = "help", description = L10n.Tg.T("tg.cmd.help") },
                            }, token: token);
                            bot.CommandsSet = true;
                        }
                        // A poll failing now keeps its error: /bot/me answering does not make the chats work.
                        if (!(polled.Contains(token) && DateTime.UtcNow < bot.PollAgainAt)) bot.Error = null;
                    }
                    catch (Exception ex)
                    {
                        bot.Error = DescribeException(ex);
                    }
                    UpdateCapyHealth();
                    await Task.Delay(gap);
                }
                if (tokens.Count == 0) await Task.Delay(gap);
            }
        }

        // One state for all the bots: every one fine, connected; some failing, partly (yellow, the failing
        // ones named); all failing, disconnected.
        private static void UpdateCapyHealth()
        {
            var tokens = CapyTokens;
            var failing = tokens.Where(t => BotOf(t).Error != null).ToList();
            string Name(string t) { string k = BotKey(t); return k.Length == 0 ? "main bot" : k; }
            string errors = string.Join("; ", failing.Select(t => $"{Name(t)}: {BotOf(t).Error}"));
            if (failing.Count == 0)
                HealthStatus.MarkConnected("capy");
            else if (failing.Count < tokens.Count)
                HealthStatus.MarkPartial("capy", errors);
            else
            {
                HealthStatus.MarkDisconnected("capy");
                HealthStatus.MarkError("capy", errors);
            }
        }

        // ---- incoming: messages written to the bot ----

        // Every few seconds: the bots' chats, and the new events of those with a newer seq. The cursor per
        // chat is kept in the DB, so messages written while ghostline was down are handled after the start.
        // A chat seen for the first time starts at its current seq: its history is not replayed.
        // The polled bots (CapyPolledTokens) one after another, one request at a time; a failing bot is left
        // alone for 30 s and does not hold up the others. The main bot's cursors keep their old keys;
        // another bot's are per bot, as two bots may sit in one chat.
        private static readonly TimeSpan CapyPollFailedPause = TimeSpan.FromSeconds(30);

        private static async Task CapyPollLoop()
        {
            var users = new Dictionary<Guid, string>();
            while (true)
            {
                foreach (string token in CapyPolledTokens)
                {
                    var bot = BotOf(token);
                    if (DateTime.UtcNow < bot.PollAgainAt) continue;
                    try
                    {
                        await CapyPollBot(token, users);
                        if (bot.Error != null) { bot.Error = null; UpdateCapyHealth(); }
                    }
                    catch (Exception ex)
                    {
                        bool first = bot.Error == null;
                        bot.Error = DescribeException(ex);
                        bot.PollAgainAt = DateTime.UtcNow + CapyPollFailedPause;
                        UpdateCapyHealth();
                        string k = BotKey(token);
                        if (first)
                            SysLog.Warn("capy", $"poll failed ({(k.Length == 0 ? "main bot" : k)}): {bot.Error}, retrying every {CapyPollFailedPause.TotalSeconds:0} s");
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }

        private static async Task CapyPollBot(string token, Dictionary<Guid, string> users)
        {
            var bot = await CapyKnown(token);
            bool main = token == MainCapyToken;
            var chats = (JArray)await CapyRequest(HttpMethod.Get, "/chats", token: token);
            RememberCapyChats(bot, chats);
            // Our other bots write into shared chats too: their messages are not commands.
            var ours = _capyBots.Values.Select(b => b.Id).ToHashSet();
            foreach (var chat in chats)
            {
                Guid chatId = (Guid)chat["id"];
                long last = (long)chat["lastSeq"];
                string key = KvCapySeq + (main ? "" : bot.Id + ".") + chatId;
                if (!long.TryParse(Store.GetKv(key), out long seen))
                {
                    Store.SetKv(key, last.ToString());
                    continue;
                }
                while (seen < last)
                {
                    var page = await CapyRequest(HttpMethod.Get, $"/chats/{chatId}/events?after={seen}&limit=100", token: token);
                    var events = (JArray)page["events"];
                    foreach (var e in events)
                    {
                        seen = Math.Max(seen, (long)e["seq"]);
                        if ((string)e["type"] != "MessageNew") continue;
                        var msg = e["payload"]?["message"];
                        if (msg == null || (Guid?)msg["senderId"] is not { } sender || ours.Contains(sender)) continue;
                        if (!users.TryGetValue(sender, out var name))
                            users[sender] = name = (string)(await CapyRequest(HttpMethod.Get, $"/users/{sender}", token: token))["username"];
                        HandleCapyMessage(token, chatId, (long?)chat["number"] ?? 0, (string)chat["type"] == "Direct", name,
                            sender == bot.OwnerId, (string)msg["text"] ?? "");
                    }
                    Store.SetKv(key, seen.ToString());
                    if (events.Count == 0 || page["hasMore"]?.Value<bool>() != true) break;
                }
            }
        }

        // A message to a bot: a command, or an SMS to send ("number / text" in a chat of one line,
        // "line / number / text" in a chat of several). Replies go into the same chat, from the same bot.
        private static void HandleCapyMessage(string token, Guid chatId, long chatNumber, bool direct, string username, bool isOwner,
                                              string message)
        {
            string botKey = BotKey(token);
            string reply = CapyTarget(botKey.Length == 0 ? chatId.ToString() : chatId + "|" + botKey);
            var lines = LinesOfCapyChat(chatId, chatNumber, direct, username, isOwner, token);
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
