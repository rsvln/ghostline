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
            List<string> mlist = messageText.Replace("\r", "").Split('\n').ToList();
            Channel chanOut = getChannelType(mlist[0]);

            if ((mlist.Count < 3) || (chanOut is null))
            {
                await botClient.SendMessage(chatId: chatId.ToString(), text: L10n.Tg.T("tg.sms.wrong_format", messageText, getAllChannelNames()), cancellationToken: cancellationToken);
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
