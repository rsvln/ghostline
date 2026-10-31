using Telegram.Bot.Types;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;

namespace ghostline
{
    internal partial class Program
    {
        // Puts the text into the persistent queue for sending to every Telegram chat of the line.
        // The queue is stored in SQLite (the same DB as the SMS history) and survives a process
        // restart, so a temporary Telegram API outage does not silently drop notifications.
        private static void EnqueueTelegramForLine(Channel line, string text)
        {
            foreach (string chat in LineChats(line))
                Store.EnqueueTelegramMessage(chat, text);
        }

        // Background worker of the persistent queue: every 5 s takes the pending messages
        // and tries to send them. Success removes them from the queue; failure records the
        // attempt and keeps them for the next cycle. Attempts are unlimited: a message
        // stays queued until it goes out (and survives a restart, since the queue lives in
        // the DB, not in memory). Targets "ntfy:<topic>" go to ntfy, the rest are Telegram chats;
        // rows of a channel that is switched off in the config are dropped.
        private static async Task TelegramOutboxWorker()
        {
            while (true)
            {
                var pending = Store.GetPendingTelegramMessages(50);
                foreach (var item in pending)
                {
                    bool ntfy = IsNtfyTarget(item.ChatId);
                    string kind = ntfy ? "ntfy" : "telegram";
                    try
                    {
                        if (ntfy ? !NtfyEnabled : !TelegramEnabled)
                        {
                            Store.DeleteTelegramMessage(item.Id);
                            continue;
                        }
                        if (ntfy)
                            await NtfySendQueued(item.ChatId, item.Text);
                        else
                            await bot.SendMessage(item.ChatId, item.Text);
                        Store.DeleteTelegramMessage(item.Id);
                        HealthStatus.MarkActivity(kind);
                    }
                    catch (Exception ex)
                    {
                        Store.MarkTelegramMessageFailed(item.Id, ex.Message);
                        HealthStatus.MarkError(kind, ex.Message);
                        SysLog.Warn(kind, $"send to {item.ChatId} failed (attempt {item.Attempts + 1}): {ex.Message}");
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

            // Only chats of some line (its own chat_ids or the common telegram.chat_ids) are served.
            var lines = LinesOfChat(chatId.ToString());
            if (lines.Count == 0)
            {
                await botClient.SendMessage(chatId: chatId.ToString(), text: L10n.Tg.T("tg.sms.go_away"), cancellationToken: cancellationToken);
                return;
            }
            HealthStatus.MarkActivity("telegram");

            async Task Reply(string html) =>
                await botClient.SendMessage(chatId, html, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                                            linkPreviewOptions: true, cancellationToken: cancellationToken);

            // Commands: /help (/start), /status. "/help@botname" in group chats.
            if (messageText.StartsWith('/'))
            {
                string cmd = messageText.Split(' ', '\n')[0].Split('@')[0].ToLowerInvariant();
                await Reply(cmd switch
                {
                    "/help" or "/start" => HelpText(lines),
                    "/status" => StatusText(),
                    _ => L10n.Tg.T("tg.cmd.unknown", Html(cmd))
                });
                return;
            }

            var (line, number, text) = ParseSmsCommand(lines, messageText);
            if (line == null)
            {
                await Reply(WrongFormatText(lines));
                return;
            }
            string sourceLabel = "@" + (message.Chat.Username ?? message.Chat.Id.ToString());
            var (ok, error) = QueueSmsSend(line, number, text, sourceLabel);
            if (!ok)
                await botClient.SendMessage(chatId: chatId.ToString(), text: L10n.Tg.T("tg.sms.send_failed", error), cancellationToken: cancellationToken);
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
                rows.Add(Row(gw.id, gw.id, Modems.IsAmi(gw.type)));
            if (TelegramEnabled) rows.Add(Row("telegram", "Telegram", true));
            if (NtfyEnabled) rows.Add(Row("ntfy", "ntfy", true));
            if (settings.FullMode)
            {
                rows.Add(Row("cdr", l.T("tg.status.cdr"), true));
                if (TranscribeEnabled) rows.Add(Row("transcribe", l.T("tg.status.transcribe"), true));
            }

            // Monitored lines: operator, network type, signal; or how long the line has had no network.
            var radio = (settings.lines ?? new())
                .Where(ch => RadioMonitored(getGatewayById(ch.gateway)))
                .Select(ch => LineRadio(ch) is { } r
                    ? $"{(r.Up ? "📶" : "⚠️")} {Html(ch.name)}: {Html(RadioSummary(r))}"
                    : $"⚪ {Html(ch.name)}: {l.T("tg.radio.no_data")}")
                .ToList();
            if (radio.Count > 0)
                rows.Add("\n" + l.T("tg.status.lines") + "\n" + string.Join("\n", radio));

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
                SysLog.Warn("telegram", "SetMyCommands failed: " + DescribeException(ex));
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

            SysLog.Warn("telegram", "polling error: " + ErrorMessage + ", retrying in 5 s");
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
