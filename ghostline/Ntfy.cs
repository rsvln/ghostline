using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ghostline
{
    // ntfy channel (self-hosted push over plain HTTP). Each line goes to its topic (see Routing.cs):
    //  - out: SMS, calls (MP3 attachment, replaced with the transcript later via the same sequence id)
    //    and replies to commands go through the same persistent queues as Telegram, with the
    //    target "ntfy:<topic>" instead of a chat id;
    //  - in: ghostline listens to the topics; a message written in the ntfy app is an SMS to send
    //    (ParseSmsCommand) or a command (/status, /help).
    // Messages published by ghostline carry the tag "ghost" (shown as 👻), which is how they are
    // told apart from the owner's own messages in the same topic.
    internal partial class Program
    {
        private const string NtfyPrefix = "ntfy:";
        private const string NtfyOwnTag = "ghost";
        private const string KvNtfySince = "ntfy.since";
        // ntfy turns longer messages into a text attachment; keep them below that.
        private const int NtfyTextLimit = 3800;

        private static readonly HttpClient ntfyHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

        internal static bool NtfyEnabled => !string.IsNullOrWhiteSpace(settings?.ntfy?.url);
        internal static bool TelegramEnabled => !string.IsNullOrWhiteSpace(settings?.telegram?.token);

        private static string NtfyTarget(string topic) => NtfyPrefix + topic;
        private static bool IsNtfyTarget(string target) => target != null && target.StartsWith(NtfyPrefix);

        // Queues a notification for the line's topic (persistent, retried, like Telegram messages).
        // When the topic is shared by several lines, the line name goes into the title.
        private static void EnqueueNtfy(Channel line, string title, string text)
        {
            string topic = LineTopic(line);
            if (topic == null) return;
            if (LinesOfTopic(topic).Count > 1)
                title = string.IsNullOrEmpty(title) ? line.name : $"{line.name} · {title}";
            EnqueueNtfyTopic(topic, title, text);
        }

        private static void EnqueueNtfyTopic(string topic, string title, string text) =>
            Store.EnqueueTelegramMessage(NtfyTarget(topic), new JObject { ["title"] = title, ["message"] = text }.ToString());

        // Sends a queued tg_queue row whose target is ntfy.
        private static Task NtfySendQueued(string target, string payload)
        {
            var j = JObject.Parse(payload);
            return NtfyPublish(target.Substring(NtfyPrefix.Length), (string)j["title"], (string)j["message"]);
        }

        // Publishes a text message; with sequenceId it replaces the notification with that id.
        // attachUrl reuses an attachment that was uploaded before (the call recording).
        internal static async Task<JObject> NtfyPublish(string topic, string title, string text, string sequenceId = null,
                                                       string attachUrl = null, string fileName = null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, NtfyUrl(topic, sequenceId))
            {
                Content = new StringContent(Shorten(text ?? ""), Encoding.UTF8, "text/plain")
            };
            NtfyHeaders(req, title);
            if (attachUrl != null)
            {
                req.Headers.Add("Attach", attachUrl);
                if (fileName != null) req.Headers.Add("Filename", fileName);
            }
            return await NtfySend(req);
        }

        // Uploads a file (the call recording) with a short message; returns the attachment URL.
        internal static async Task<string> NtfyPublishFile(string topic, string title, string text, byte[] file, string fileName,
                                                          string sequenceId)
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, NtfyUrl(topic, sequenceId)) { Content = new ByteArrayContent(file) };
            NtfyHeaders(req, title);
            req.Headers.Add("Filename", fileName);
            // With a file in the body the message goes in a header: RFC 2047, as for the title.
            req.Headers.Add("Message", Rfc2047(Shorten(text ?? "")));
            var reply = await NtfySend(req);
            return (string)reply["attachment"]?["url"];
        }

        private static string NtfyUrl(string topic, string sequenceId) =>
            settings.ntfy.url.TrimEnd('/') + "/" + Uri.EscapeDataString(topic) +
            (sequenceId == null ? "" : "/" + Uri.EscapeDataString(sequenceId));

        private static void NtfyHeaders(HttpRequestMessage req, string title)
        {
            if (!string.IsNullOrEmpty(settings.ntfy.token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ntfy.token);
            if (!string.IsNullOrEmpty(title))
                req.Headers.Add("Title", Rfc2047(title));
            req.Headers.Add("Tags", NtfyOwnTag);
            req.Headers.Add("Markdown", "yes");
        }

        private static async Task<JObject> NtfySend(HttpRequestMessage req)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var resp = await ntfyHttp.SendAsync(req, cts.Token);
            string body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"ntfy HTTP {(int)resp.StatusCode}: {body.Trim()}");
            HealthStatus.MarkActivity("ntfy");
            return JObject.Parse(body);
        }

        // HTTP headers are ASCII; ntfy decodes RFC 2047 ("=?UTF-8?B?...?=") for Cyrillic and emoji.
        private static string Rfc2047(string s) =>
            s.All(c => c >= 32 && c < 127) ? s : "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)) + "?=";

        private static string Shorten(string text)
        {
            if (Encoding.UTF8.GetByteCount(text) <= NtfyTextLimit) return text;
            string tail = "\n…";
            while (Encoding.UTF8.GetByteCount(text) + tail.Length > NtfyTextLimit)
                text = text.Substring(0, text.Length * 9 / 10);
            return text + tail;
        }

        // Telegram HTML (help and status texts) to the Markdown that ntfy renders.
        private static string HtmlToMarkdown(string html)
        {
            string s = Regex.Replace(html, @"<a href=""([^""]*)"">(.*?)</a>", "[$2]($1)");
            s = Regex.Replace(s, @"</?b>", "**");
            s = Regex.Replace(s, @"<pre>(.*?)</pre>", "```\n$1\n```", RegexOptions.Singleline);
            s = Regex.Replace(s, @"</?code>", "`");
            s = Regex.Replace(s, @"<[^>]+>", "");
            return System.Net.WebUtility.HtmlDecode(s);
        }

        // ---- incoming: messages written in the ntfy app ----

        private static async Task NtfyListenLoop()
        {
            var topicList = AllLines.Select(LineTopic).Where(t => t != null).Distinct().ToList();
            if (topicList.Count == 0)
            {
                Console.WriteLine("ntfy: no topics (set ntfy.topic or channels[].ntfy_topic)");
                return;
            }
            string topics = string.Join(",", topicList.Select(Uri.EscapeDataString));
            while (true)
            {
                try
                {
                    // since: the last message seen, so messages written while ghostline was down are
                    // not lost (ntfy keeps them for a day); on the very first start only new ones.
                    string since = Store.GetKv(KvNtfySince) ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"{settings.ntfy.url.TrimEnd('/')}/{topics}/json?since={Uri.EscapeDataString(since)}");
                    if (!string.IsNullOrEmpty(settings.ntfy.token))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ntfy.token);
                    using var resp = await ntfyHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                    if (!resp.IsSuccessStatusCode)
                        throw new HttpRequestException($"ntfy HTTP {(int)resp.StatusCode}");
                    HealthStatus.MarkConnected("ntfy");

                    using var stream = await resp.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(stream);
                    // ntfy sends a keepalive every 45 s; silence for longer means a dead connection.
                    while (true)
                    {
                        var readTask = reader.ReadLineAsync();
                        if (await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(120))) != readTask)
                            throw new TimeoutException("no keepalive from ntfy for 120 s");
                        string line = await readTask;
                        if (line == null) break;
                        HealthStatus.MarkConnected("ntfy");
                        if (line.Length == 0) continue;
                        var m = JObject.Parse(line);
                        if ((string)m["event"] != "message") continue;
                        Store.SetKv(KvNtfySince, (string)m["id"]);
                        if (m["tags"] is JArray tags && tags.Any(t => (string)t == NtfyOwnTag)) continue;
                        HandleNtfyMessage((string)m["topic"], (string)m["message"] ?? "");
                    }
                    throw new IOException("ntfy closed the stream");
                }
                catch (Exception ex)
                {
                    HealthStatus.MarkDisconnected("ntfy");
                    HealthStatus.MarkError("ntfy", DescribeException(ex));
                    Console.WriteLine("ntfy listen failed: " + DescribeException(ex) + ", retrying in 5s");
                    await Task.Delay(5000);
                }
            }
        }

        // A message from the owner in a topic: a command, or an SMS to send (one line in the topic:
        // "number / text"; several: "line / number / text").
        private static void HandleNtfyMessage(string topic, string message)
        {
            var lines = LinesOfTopic(topic);
            if (lines.Count == 0) return;
            string text = message.Replace("\r", "").Trim();
            Console.WriteLine($"ntfy in [{topic}]: {text.Replace("\n", " | ")}");
            var l = L10n.Tg;

            if (text.StartsWith('/'))
            {
                string cmd = text.Split(' ', '\n')[0].ToLowerInvariant();
                EnqueueNtfyTopic(topic, null, HtmlToMarkdown(cmd switch
                {
                    "/help" or "/start" => HelpText(lines),
                    "/status" => StatusText(),
                    _ => l.T("tg.cmd.unknown", cmd)
                }));
                return;
            }

            var (line, number, body) = ParseSmsCommand(lines, text);
            if (line == null)
            {
                EnqueueNtfyTopic(topic, null, HtmlToMarkdown(WrongFormatText(lines)));
                return;
            }
            var (ok, error) = QueueSmsSend(line, number, body, "ntfy");
            if (!ok)
                EnqueueNtfyTopic(topic, null, l.T("tg.sms.send_failed", error));
        }
    }
}
