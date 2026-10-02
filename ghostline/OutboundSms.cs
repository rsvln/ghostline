namespace ghostline
{
    internal partial class Program
    {
        // Быстрая проверка + постановка исходящего SMS в персистентную очередь.
        // Используется и из Telegram-бота, и из веб-UI (/api/sms/send). Реальная
        // отправка идёт асинхронно из SmsOutboxWorker с ретраями — временная
        // недоступность шлюза (HTTP или AMI) больше не теряет сообщение молча.
        internal static (bool ok, string? error) QueueSmsSend(Channel chanOut, string rawNumber, string mcontent, string sourceLabel)
        {
            // Раз шлюз есть в gateways[] — клиент для него уже создан в Main, отдельно
            // проверять "настроен ли IP" не нужно: gateways[] сам по себе источник
            // правды о том, что реально поднято. Несуществующий gateway — ошибка конфига.
            var gw = getGatewayById(chanOut.gateway);
            if (gw == null)
                return (false, "channel '" + chanOut.name + "' references unknown gateway '" + chanOut.gateway + "'");

            // AMI и большинство прошивок шлюзов ожидают однострочный текст команды;
            // "сырые" переводы строк (например, из многострочной textarea веб-UI) ломают
            // построчный протокол AMI (Action: Command у quectel), поэтому сворачиваем их
            // в пробелы здесь же, одинаково для всех типов каналов.
            mcontent = mcontent.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");

            // Канонизируем номер сразу на входе (E.164) — в очереди/БД/UI всегда один
            // и тот же вид, независимо от того, как ввёл его пользователь. Формат под
            // конкретный шлюз/диалплан выводится из этого канонического вида в DispatchSms.
            string canonical = ToE164(rawNumber);

            Store.EnqueueOutgoingSms(chanOut.name, canonical, mcontent, sourceLabel);
            return (true, null);
        }

        // Переводит канонический номер в формат набора для конкретного шлюза.
        // Короткие коды ("900") и буквенные ID набираются как есть — приписать им
        // "810"/"8" значит отправить в никуда.
        private static string FormatForGateway(string canonical, string gatewayType)
        {
            if (string.IsNullOrEmpty(canonical) || !canonical.StartsWith("+"))
                return canonical;

            // quectel ожидает E.164 с "+" — он и так уже в этом виде.
            if (gatewayType == "quectel")
                return canonical;

            // goip / yeastar — российский диалплан: "8" для домашних, "810" + код
            // страны для международных.
            return canonical.StartsWith("+7")
                ? "8" + canonical.Substring(2)
                : "810" + canonical.Substring(1);
        }

        // Собственно попытка отправки через нужный шлюз. Бросает исключение при
        // неудаче — вызывающий (SmsOutboxWorker) ловит и решает, ретраить ли.
        // rawNumber приходит уже в каноническом виде (см. QueueSmsSend) — здесь из
        // него выводится формат набора под диалплан шлюза (dest), а в историю/UI/
        // Telegram всегда идёт исходный канонический rawNumber, не dest.
        private static async Task DispatchSms(Channel chanOut, Gateway gw, string rawNumber, string mcontent, string sourceLabel)
        {
            if (gw.type == "yeastar")
            {
                string dest = FormatForGateway(rawNumber, gw.type);
                string payload = "http://" + gw.ip + ":" + gw.httpPort.ToString() + "/cgi/WebCGI?1500101=account=" + gw.user + "&password=" + gw.password + "&port=" + chanOut.line + "&destination=" + dest + "&content=" + Uri.EscapeDataString(mcontent);
                await _yeastarClient.GetAsync(payload);
                RecordOutgoing("y", chanOut, rawNumber, mcontent, sourceLabel);
                HealthStatus.MarkActivity(gw.id);
            }
            else if (gw.type == "goip")
            {
                if (!_goipClients.TryGetValue(gw.id, out var goipClient))
                    throw new InvalidOperationException("goip client for gateway '" + gw.id + "' is not initialized");

                string dest = FormatForGateway(rawNumber, gw.type);
                string smskey = new string(Enumerable.Repeat("abcdef0123456789", 8).Select(s => s[Random.Shared.Next(s.Length)]).ToArray());
                var baseUrl = "http://" + gw.ip + ":" + gw.httpPort.ToString() + "/default/en_US/sms_info.html?type=sms";
                var formContent = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("line", chanOut.line),
                    new KeyValuePair<string, string>("smskey", smskey),
                    new KeyValuePair<string, string>("action", "SMS"),
                    new KeyValuePair<string, string>("telnum", dest),
                    new KeyValuePair<string, string>("smscontent", mcontent),
                    new KeyValuePair<string, string>("send", "Send")
                });
                await goipClient.PostAsync(baseUrl, formContent);
                RecordOutgoing("g", chanOut, rawNumber, mcontent, sourceLabel);
                HealthStatus.MarkActivity(gw.id);
            }
            else if (gw.type == "quectel")
            {
                if (!_amiClients.TryGetValue(gw.id, out var amiClient))
                    throw new InvalidOperationException("AMI client for gateway '" + gw.id + "' is not initialized");

                string dest = FormatForGateway(rawNumber, gw.type);
                string command = "Action: Command\r\nCommand: quectel sms " + chanOut.line + " " + dest + " " + mcontent + "\r\n\r\n";
                amiClient.Client.Send(System.Text.Encoding.UTF8.GetBytes(command));
                RecordOutgoing("q", chanOut, rawNumber, mcontent, sourceLabel);
                HealthStatus.MarkActivity(gw.id);
            }
        }

        // Фоновый воркер очереди исходящих SMS: раз в 5с забирает накопившиеся
        // отправки и пытается их выполнить. Успех — удаляет из очереди, неудача —
        // фиксирует попытку и оставляет на следующий цикл (без ограничения по числу
        // попыток — переживает и временную недоступность шлюза, и рестарт процесса).
        private static async Task SmsOutboxWorker()
        {
            while (true)
            {
                var pending = Store.GetPendingOutgoingSms(20);
                foreach (var item in pending)
                {
                    var chan = getChannelType(item.ChannelName);
                    if (chan == null)
                    {
                        Console.WriteLine($"Outgoing SMS #{item.Id}: channel '{item.ChannelName}' not found in config, dropping");
                        Store.DeleteOutgoingSms(item.Id);
                        continue;
                    }

                    var gw = getGatewayById(chan.gateway);
                    if (gw == null)
                    {
                        Console.WriteLine($"Outgoing SMS #{item.Id}: gateway '{chan.gateway}' not found in config, dropping");
                        Store.DeleteOutgoingSms(item.Id);
                        continue;
                    }

                    try
                    {
                        await DispatchSms(chan, gw, item.Number, item.Text, item.SourceLabel);
                        Store.DeleteOutgoingSms(item.Id);
                    }
                    catch (Exception ex)
                    {
                        Store.MarkOutgoingSmsFailed(item.Id, ex.Message);
                        HealthStatus.MarkError(gw.id, ex.Message);
                        Console.WriteLine($"Outgoing SMS #{item.Id} via {chan.name} failed (attempt {item.Attempts + 1}): {ex.Message}");
                    }
                }
                await Task.Delay(5000);
            }
        }

        private static void RecordOutgoing(string gwt, Channel chan, string peer, string mcontent, string sourceLabel)
        {
            string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string logContent = mcontent.Replace("\n", "\\n");

            if (settings.logger.file)
                Log(gwt, "out", ts, chan.name, peer, logContent);
            Console.WriteLine(ts + "\t" + gwt + "\tout\t" + chan.name + "\t" + peer + "\t" + logContent);

            Store.Insert(new SmsRecord
            {
                Ts = ts,
                Direction = "out",
                Gateway = gwt,
                Channel = chan.name,
                Peer = peer,
                Note = sourceLabel,
                Content = mcontent
            });

            // Уведомление в Telegram-чаты о факте отправки — одинаково для команд из бота
            // и для отправки через веб-UI (раньше это делал только бот-путь, и отправленные
            // через веб SMS были в Telegram не видны).
            string body = "sent to " + peer + " from " + sourceLabel + " via " + chan.name + " at " + ts + "\n----------------\n\n" + mcontent;
            EnqueueTelegramBroadcast(body);
        }
    }
}
