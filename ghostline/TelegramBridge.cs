using Telegram.Bot.Types;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;

namespace ghostline
{
    internal partial class Program
    {
        // Ставит текст в персистентную очередь на отправку каждому чату из telegram.chat_ids.
        // Очередь хранится в SQLite (та же БД, что и история SMS) и переживает рестарт
        // процесса — временная недоступность Telegram API не роняет уведомления молча.
        private static void EnqueueTelegramBroadcast(string text)
        {
            foreach (string schatid in settings.telegram.chatIds)
            {
                Store.EnqueueTelegramMessage(schatid, text);
            }
        }

        // Фоновый воркер персистентной очереди: раз в 5с забирает накопившиеся
        // сообщения и пытается отправить. Успех — удаляет из очереди, неудача —
        // фиксирует попытку и оставляет на следующий цикл. Попытки не ограничены —
        // сообщение остаётся в очереди, пока не уйдёт (в т.ч. переживает рестарт,
        // т.к. очередь лежит в БД, а не в памяти).
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

        // Поллер Telegram.Bot после возврата из обработчика ошибок сразу делает
        // следующий запрос. Без сети (DNS отвечает мгновенно EAI_AGAIN) это цикл
        // без пауз: 2026-09-27 за 10 минут без сети — 682 тыс. строк в журнале.
        // Пауза 5с, как у переподключения к AMI.
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

        // Тип внешнего исключения (+ контекст вроде "(host:port)", если он есть в
        // сообщении где-то по цепочке) -> тип и сообщение корневой причины. Без
        // exception.ToString() и без промежуточных звеньев цепочки — для сетевых
        // сбоев (таймаут/DNS/нехватка сокетов) они обычно дублируют друг друга.
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
