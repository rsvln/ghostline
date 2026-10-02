using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace ghostline
{
    // Звонки в Telegram — ленивая доставка, как в frte2tg (AIQueueService): сообщение
    // уходит сразу после звонка, а расшифровка дописывается в него позже правкой
    // подписи/текста. Очередь — таблица call_tg: pending → sent (с «⏳») → final.
    internal partial class Program
    {
        private const int CaptionLimit = 1024;   // подпись к голосовому
        private const int TextLimit = 4096;      // обычное сообщение
        private const int MaxCallTgAttempts = 20;

        // Решает по настройкам линии, что и кому слать про новый звонок.
        private static void EnqueueCallNotifications(CallRecord c, Channel ch)
        {
            var cc = ch.calls ?? new ChannelCalls();
            var chats = cc.chatIds is { Count: > 0 } ? cc.chatIds : settings.telegram.chatIds ?? new List<string>();
            if (chats.Count == 0) return;

            if (c.Missed)
            {
                // Исходящий без ответа — вы и так знаете, что не дозвонились.
                if (c.Direction == "in" && cc.missed)
                    Store.EnqueueCallTg(c.Id, chats, "text", wantTranscript: false);
                return;
            }

            bool rec = c.HasRecording || (c.RecSize < 0 && c.RecPath != null);
            bool wantTranscript = cc.transcript && c.TrState == "pending";
            bool voice = cc.voice && rec;
            if (!voice && !wantTranscript) return;

            Store.EnqueueCallTg(c.Id, chats, voice ? "voice" : "text", wantTranscript);
        }

        private static async Task CallTelegramWorker()
        {
            while (true)
            {
                foreach (var item in Store.PendingCallTg(20))
                    await SendCallTg(item);
                foreach (var item in Store.CallTgReadyForEdit(20))
                    await EditCallTg(item);
                await Task.Delay(3000);
            }
        }

        private static async Task SendCallTg(CallTgItem item)
        {
            try
            {
                var call = Store.GetCall(item.CallId);
                if (call == null) { Store.MarkCallTgFinal(item.Id); return; }

                bool trFinished = call.TrState is "done" or "error";
                string head = CallHeader(call);
                string text = item.WantTranscript ? head + TranscriptPart(call) : head;
                Message msg = null;

                if (item.Kind == "voice")
                {
                    byte[] wav = await Recordings.BalancedForCall(call);
                    if (wav != null && wav.Length > Store.MinRecordingBytes)
                    {
                        string caption = Fit(text, CaptionLimit, out bool cut);
                        msg = await SendRecording(item.ChatId, call, wav, caption);
                        if (cut && trFinished)
                            await SendFullTranscript(item.ChatId, msg.Id, call);
                    }
                }

                // Текстовое уведомление — и как запасной вариант, если записи не оказалось.
                if (msg == null)
                {
                    msg = await bot.SendMessage(item.ChatId, Fit(text, TextLimit, out bool cut));
                    if (cut && trFinished)
                        await SendFullTranscript(item.ChatId, msg.Id, call);
                }

                // Расшифровка не нужна или уже готова (догоняли очередь) — дописывать нечего.
                Store.MarkCallTgSent(item.Id, msg.Id, final: !item.WantTranscript || trFinished);
                HealthStatus.MarkActivity("telegram");
            }
            catch (Exception ex)
            {
                CallTgFailed(item, "send", ex);
            }
        }

        // Голосовое (ogg/opus). Если получатель запретил голосовые (приватность Telegram
        // «Голосовые сообщения — никто», ошибка VOICE_MESSAGES_FORBIDDEN) или ffmpeg не
        // справился — аудиофайлом MP3: у него тоже плеер, и подпись правится так же.
        // Именно MP3: ogg/opus Telegram считает голосовым и через sendAudio — тот же отказ
        // (2026-09-28 так и не доходили звонки в чат с запретом голосовых).
        private static async Task<Message> SendRecording(string chatId, CallRecord call, byte[] wav, string caption)
        {
            string name = CallFileBase(call);
            byte[] ogg = await Recordings.ToOggOpus(wav);
            if (ogg != null)
            {
                try
                {
                    return await bot.SendVoice(chatId, InputFile.FromStream(new MemoryStream(ogg), name + ".ogg"),
                                               caption: caption, duration: call.Billsec);
                }
                catch (ApiRequestException ex) when (ex.Message.Contains("VOICE_MESSAGES_FORBIDDEN")) { }
            }
            byte[] mp3 = await Recordings.ToMp3(wav);
            return mp3 != null
                ? await bot.SendAudio(chatId, InputFile.FromStream(new MemoryStream(mp3), name + ".mp3"),
                                      caption: caption, duration: call.Billsec, title: name)
                : await bot.SendDocument(chatId, InputFile.FromStream(new MemoryStream(wav), name + ".wav"),
                                         caption: caption);
        }

        // 2026-09-26_13-16_in_home_+79001234567
        internal static string CallFileBase(CallRecord c)
        {
            string ts = ParseTs(c.Ts).ToString("yyyy-MM-dd_HH-mm");
            string peer = new string((c.Peer ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch == '+').ToArray());
            return $"{ts}_{c.Direction}_{c.Channel}_{peer}";
        }

        private static async Task EditCallTg(CallTgItem item)
        {
            try
            {
                var call = Store.GetCall(item.CallId);
                if (call == null) { Store.MarkCallTgFinal(item.Id); return; }

                string full = CallHeader(call) + TranscriptPart(call);
                bool cut;
                if (item.Kind == "voice")
                    await bot.EditMessageCaption(item.ChatId, (int)item.MessageId, Fit(full, CaptionLimit, out cut));
                else
                    await bot.EditMessageText(item.ChatId, (int)item.MessageId, Fit(full, TextLimit, out cut));

                if (cut)
                    await SendFullTranscript(item.ChatId, (int)item.MessageId, call);
                Store.MarkCallTgFinal(item.Id);
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
            {
                Store.MarkCallTgFinal(item.Id);
            }
            catch (Exception ex)
            {
                CallTgFailed(item, "edit", ex);
            }
        }

        private static void CallTgFailed(CallTgItem item, string what, Exception ex)
        {
            string err = DescribeException(ex);
            Store.MarkCallTgFailed(item.Id, err);
            HealthStatus.MarkError("telegram", err);
            Console.WriteLine($"Call {item.CallId} Telegram {what} to {item.ChatId} failed (attempt {item.Attempts + 1}): {err}");
            // Например, сообщение удалили — править больше нечего.
            if (item.Attempts + 1 >= MaxCallTgAttempts)
                Store.MarkCallTgFinal(item.Id);
        }

        // Полный текст ответом на сообщение, если в подпись не влез. Кусками по 4096.
        private static async Task SendFullTranscript(string chatId, int replyTo, CallRecord call)
        {
            string text = "📝 " + TranscriptText(call);
            for (int i = 0; i < text.Length; i += TextLimit)
                await bot.SendMessage(chatId, text.Substring(i, Math.Min(TextLimit, text.Length - i)),
                                      replyParameters: replyTo);
        }

        private static string Fit(string text, int limit, out bool cut)
        {
            cut = text.Length > limit;
            if (!cut) return text;
            string tail = L10n.Tg.T("tg.call.full_below");
            return text.Substring(0, limit - tail.Length) + tail;
        }

        // 📞 Входящий · home · 27.09 14:05
        // Иван Петров +79161234567
        // ⏱ 2:31
        internal static string CallHeader(CallRecord c)
        {
            string kind = L10n.Tg.T(c.Missed
                ? (c.Direction == "in" ? "tg.call.missed" : "tg.call.no_answer")
                : (c.Direction == "in" ? "tg.call.in" : "tg.call.out"));
            string when = ParseTs(c.Ts).ToString("dd.MM HH:mm");
            string who = string.IsNullOrEmpty(c.PeerName) ? c.Peer : c.PeerName + " " + c.Peer;
            string s = $"{kind} · {c.Channel} · {when}\n{who}";
            if (!c.Missed)
                s += $"\n⏱ {c.Billsec / 60}:{c.Billsec % 60:D2}";
            return s;
        }

        // Текст расшифровки собирается из реплик на языке Telegram (а не берётся готовым
        // из tr_text) — чтобы смена языка в настройках действовала и на старые звонки.
        private static string TranscriptText(CallRecord c)
        {
            var segs = Segments(c);
            string text = segs.Count > 0 ? FormatDialog(segs, L10n.Tg) : c.TrText;
            return string.IsNullOrWhiteSpace(text) ? L10n.Tg.T("tg.call.no_speech") : text;
        }

        private static string TranscriptPart(CallRecord c) => c.TrState switch
        {
            "done" => "\n\n📝 " + TranscriptText(c),
            "error" => "\n\n" + L10n.Tg.T("tg.call.transcript_failed"),
            _ => "\n\n" + L10n.Tg.T("tg.call.transcribing")
        };
    }
}
