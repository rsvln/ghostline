using Telegram.Bot.Types;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;

namespace ghostline
{
    internal partial class Program
    {
        // Puts the text into the persistent queue for sending to every chat in telegram.chat_ids.
        // The queue is stored in SQLite (the same DB as the SMS history) and survives a process
        // restart, so a temporary Telegram API outage does not silently drop notifications.
        private static void EnqueueTelegramBroadcast(string text)
        {
            foreach (string schatid in settings.telegram.chatIds)
            {
                Store.EnqueueTelegramMessage(schatid, text);
            }
        }

        // Background worker of the persistent queue: every 5 s takes the pending messages
        // and tries to send them. Success removes them from the queue; failure records the
        // attempt and keeps them for the next cycle. Attempts are unlimited: a message
        // stays queued until it goes out (and survives a restart, since the queue lives in
        // the DB, not in memory).
        private static async Task TelegramOutboxWorker()
        {
            while (true)
            {
                var pending = Store.GetPendingTelegramMessages(50);
                foreach (var item in pending)
                {
                    try
                    {
                        await bot.SendMessage(item.ChatId, item.Text);
                        Store.DeleteTelegramMessage(item.Id);
                        HealthStatus.MarkActivity("telegram");
                    }
                    catch (Exception ex)
                    {
                        Store.MarkTelegramMessageFailed(item.Id, ex.Message);
                        HealthStatus.MarkError("telegram", ex.Message);
                        Console.WriteLine($"Telegram send to {item.ChatId} failed (attempt {item.Attempts + 1}): {ex.Message}");
                    }
                }
                await Task.Delay(5000);
            }
        }

        public static void Log(string gwt, string stream, string smstime, string from, string dest, string content)
        {
            Directory.CreateDirectory("/var/log/ghostline/");
            System.IO.File.AppendAllText("/var/log/ghostline/ghostline_"
                                            + DateTime.Now.ToString("yyyy-MM-dd") + ".log",
                                 DateTime.Now.ToString("yyyy-MM-dd")+"\t" + DateTime.Now.ToString("HH:mm:ss.fff") + "\t" + gwt + "\t" + stream + "\t" + smstime + "\t" + from + "\t" + dest + "\t" + content.Replace("\n","\\n").Replace("\r","\\r") + "\n");
        }

        async static Task TgHandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            if (update.Message is not { } message)
                return;
            if (message.Text is not { } messageText)
                return;
            var chatId = message.Chat.Id;

            if (!settings.telegram.chatIds.Contains(chatId.ToString()))
            {
                await botClient.SendMessage(chatId: chatId.ToString(), text: L10n.Tg.T("tg.sms.go_away"), cancellationToken: cancellationToken);
                return;
            }
            HealthStatus.MarkActivity("telegram");

            // Commands: /help (/start), /status. "/help@botname" in group chats.
            if (messageText.StartsWith('/'))
            {
                string cmd = messageText.Split(' ', '\n')[0].Split('@')[0].ToLowerInvariant();
                string reply = cmd switch
                {
                    "/help" or "/start" => HelpText(),
                    "/status" => StatusText(),
                    _ => L10n.Tg.T("tg.cmd.unknown", Html(cmd))
                };
                await botClient.SendMessage(chatId, reply, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                                            linkPreviewOptions: true, cancellationToken: cancellationToken);
                return;
            }

            List<string> mlist = messageText.Replace("\r", "").Split('\n').ToList();
            Channel chanOut = getChannelType(mlist[0]);

            if ((mlist.Count < 3) || (chanOut is null))
            {
                await botClient.SendMessage(chatId, L10n.Tg.T("tg.sms.wrong_format", LinesHtml()),
                                            parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, cancellationToken: cancellationToken);
                return;
            }
            else
            {
                string mcontent = "";
                for(var i = 2; i < mlist.Count; i++)
                {
                    if (i == mlist.Count - 1)
                        mcontent = mcontent + mlist[i];
                    else
                        mcontent = mcontent + mlist[i] + " ";
                }
                mcontent = mcontent.Replace("\r", "");
                string sourceLabel = "@" + (message.Chat.Username ?? message.Chat.Id.ToString());
                var (ok, error) = QueueSmsSend(chanOut, mlist[1], mcontent, sourceLabel);

                if (!ok)
                {
                    await botClient.SendMessage(chatId: chatId.ToString(), text: L10n.Tg.T("tg.sms.send_failed", error), cancellationToken: cancellationToken);
                }
            }

        }

        private static string Html(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        // Lines as "<code>home</code> +79001234567", one per line.
        private static string LinesHtml() =>
            string.Join("\n", (settings.channels ?? new()).Select(c =>
                $"<code>{Html(c.name)}</code>" + (string.IsNullOrEmpty(c.number) ? "" : " " + Html(c.number))));

        private static string HelpText()
        {
            var l = L10n.Tg;
            return l.T("tg.help", LinesHtml(), Html(settings.channels?.FirstOrDefault()?.name ?? "home"))
                 + (settings.FullMode ? "\n\n" + l.T("tg.help.calls") : "")
                 + "\n\n" + l.T("tg.help.commands")
                 + "\n\n" + l.T("tg.help.footer", VersionInfo.ProjectUrl, Html(VersionInfo.Version));
        }

        // /status: the same states as the Status tab of the web interface.
        private static string StatusText()
        {
            var l = L10n.Tg;
            string Dot(string state) => state switch { "ok" => "🟢", "err" => "🔴", _ => "⚪" };
            string Row(string kind, string title, bool connectionBased)
            {
                string state = HealthStatus.State(kind, connectionBased);
                var h = HealthStatus.Find(kind);
                string err = state == "err" && h?.LastError != null ? " — " + Html(Trim(h.LastError, 120)) : "";
                return $"{Dot(state)} {Html(title)}: {l.T("tg.status." + state)}{err}";
            }

            var rows = new List<string>();
            foreach (var gw in settings.gateways ?? new())
                rows.Add(Row(gw.id, gw.id, gw.type is "quectel" or "yeastar"));
            rows.Add(Row("telegram", "Telegram", true));
            if (settings.FullMode)
            {
                rows.Add(Row("cdr", l.T("tg.status.cdr"), true));
                if (TranscribeEnabled) rows.Add(Row("transcribe", l.T("tg.status.transcribe"), true));
            }

            var up = DateTime.Now - StartedAt;
            string uptime = up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h" : up.TotalHours >= 1 ? $"{(int)up.TotalHours}h {up.Minutes}m" : $"{up.Minutes}m";
            string queues = l.T("tg.status.queues", Store.CountPendingOutgoingSms(), Store.CountPendingTelegramMessages());
            if (settings.FullMode)
                queues += "\n" + l.T("tg.status.calls_queue", Store.TranscriptionStats().pending, Store.CountPendingCallTg());
            return $"<b>ghostline</b> {Html(VersionInfo.Version)} · {l.T("tg.status.uptime", uptime)}\n\n"
                 + string.Join("\n", rows) + "\n\n" + queues;
        }

        // Menu of bot commands in the Telegram client, in the Telegram language.
        private static async Task SetBotCommands()
        {
            try
            {
                await bot.SetMyCommands(new[]
                {
                    new BotCommand { Command = "status", Description = L10n.Tg.T("tg.cmd.status") },
                    new BotCommand { Command = "help", Description = L10n.Tg.T("tg.cmd.help") },
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("Telegram SetMyCommands failed: " + DescribeException(ex));
            }
        }

        // Polling errors alone say little: a single EAI_AGAIN from DNS is followed by a successful
        // poll that leaves no trace. So the connection is checked with getMe once a minute, like
        // the AMI keepalive: Telegram is "connected" while that works.
        private static async Task TelegramHealthLoop()
        {
            while (true)
            {
                try
                {
                    await bot.GetMe();
                    HealthStatus.MarkConnected("telegram");
                }
                catch (Exception ex)
                {
                    HealthStatus.MarkDisconnected("telegram");
                    HealthStatus.MarkError("telegram", DescribeException(ex));
                }
                await Task.Delay(TimeSpan.FromMinutes(1));
            }
        }

        // After returning from the error handler the Telegram.Bot poller immediately makes
        // the next request. Without network (DNS answers EAI_AGAIN instantly) this is a loop
        // without pauses: on 2026-09-27, 10 minutes offline produced 682k log lines.
        // Pause 5 s, as for the AMI reconnect.
        static async Task TgHandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, HandleErrorSource source, CancellationToken cancellationToken)
        {
            var ErrorMessage = exception switch
            {
                ApiRequestException apiRequestException
                    => $"Telegram API error [{apiRequestException.ErrorCode}]: {apiRequestException.Message}",
                _ => DescribeException(exception)
            };

            Console.WriteLine("Telegram polling error: " + ErrorMessage + ", retrying in 5s");
            HealthStatus.MarkError("telegram", ErrorMessage);
            try { await Task.Delay(5000, cancellationToken); } catch (OperationCanceledException) { }
        }

        // Type of the outer exception (plus context like "(host:port)" if it appears in a
        // message somewhere along the chain) -> type and message of the root cause. Without
        // exception.ToString() and without the intermediate links: for network failures
        // (timeout, DNS, socket exhaustion) they usually repeat each other.
        private static string DescribeException(Exception ex)
        {
            if (ex.InnerException == null)
                return $"{ex.GetType().Name}: {ex.Message}";

            string? context = null;
            var innermost = ex;
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (context == null)
                {
                    int start = e.Message.IndexOf('(');
                    int end = start >= 0 ? e.Message.IndexOf(')', start + 1) : -1;
                    if (end > start)
                        context = e.Message.Substring(start, end - start + 1);
                }
                innermost = e;
            }

            string outer = ex.GetType().Name + (context != null ? " " + context : "");
            return $"{outer} -> {innermost.GetType().Name}: {innermost.Message}";
        }
    }
}
