using System.IO;
using System.Net;
using System.Text;
using System.Web;

namespace ghostline
{
    internal partial class Program
    {
        // Подключение к AMI с ретраем: если сервер недоступен (при первом старте
        // или при переподключении после обрыва) — не падаем, а повторяем попытку
        // каждые 5с, пока не получится. Общая точка входа и для начального
        // подключения (запускается через Task.Run из Main — недоступность одного
        // шлюза не блокирует остальные), и для реконнекта после разрыва.
        private static void ConnectAmiWithRetry(Gateway gw, TcpClient client)
        {
            while (true)
            {
                try
                {
                    client.Client.Connect(IPAddress.Parse(gw.ip), gw.amiPort);
                    // Если за это время по сокету не придёт вообще ничего (в т.ч. ответ
                    // на keepalive-пинг из AmiKeepaliveLoop) — чтение бросит исключение
                    // по таймауту, что заведёт "тихий" обрыв в тот же реконнект-цикл.
                    client.Client.ReceiveTimeout = 90000;
                    HealthStatus.MarkConnected(gw.id);
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"AMI ({gw.id}) connect failed: {ex.Message}, retrying in 5s");
                    HealthStatus.MarkError(gw.id, ex.Message);
                    Thread.Sleep(5000);
                }
            }

            Task.Run(() => SocketReadDataLoop(client, gw));
            string payload = "Action: Login\r\nUsername: " + gw.user + "\r\nSecret: " + gw.password + "\r\n\r\n";
            client.Client.Send(Encoding.UTF8.GetBytes(payload));
        }

        // На Linux (Alpine в контейнере) .NET после Disconnect(reuseSocket: true)
        // запрещает синхронный Connect() повторно на том же Socket-объекте — только
        // асинхронный, и то на другой EndPoint (иначе кидает "Once the socket has
        // been disconnected..."). Поэтому не реюзаем объект — создаём новый TcpClient
        // и подменяем его в общем словаре; всё остальное (DispatchSms, AmiKeepaliveLoop)
        // уже берёт клиента заново по id шлюза, а не хранит прямую ссылку.
        private static void ReconnectClient(TcpClient oldClient, Gateway gw)
        {
            try { oldClient.Close(); } catch { }

            var newClient = new TcpClient();
            _amiClients[gw.id] = newClient;
            ConnectAmiWithRetry(gw, newClient);
        }

        // Раз в 30с шлёт "Action: Ping" по живому соединению — если сокет уже мёртв,
        // либо сама отправка упадёт (игнорируем — обрыв всё равно поймает
        // receive-timeout на чтении), либо в течение 90с не придёт вообще ничего и
        // чтение само запустит реконнект. Запускается один раз на шлюз и переживает
        // все последующие реконнекты — текущий клиент берётся из _amiClients на
        // каждый тик, чтобы не держать протухшую ссылку после подмены соединения.
        private static async Task AmiKeepaliveLoop(string gatewayId)
        {
            while (true)
            {
                await Task.Delay(30000);
                try
                {
                    if (_amiClients.TryGetValue(gatewayId, out var client) && client.Connected)
                        client.GetStream().Write(Encoding.UTF8.GetBytes("Action: Ping\r\n\r\n"));
                }
                catch { }
            }
        }

        // Копит байты сокета в буфер до появления полного AMI-пакета (\r\n\r\n),
        // разбирает и раздаёт события по одному, остаток держит в буфере до следующего чтения.
        private static void SocketReadDataLoop(TcpClient client, Gateway gw)
        {
            string buffer = "";
            while (true)
            {
                string chunk;
                try
                {
                    chunk = SocketReadChunk(client);
                    if (chunk.Length == 0)
                        throw new IOException("Connection closed by remote host");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"AMI ({gw.id}) socket error: {ex.Message}");
                    HealthStatus.MarkDisconnected(gw.id);
                    HealthStatus.MarkError(gw.id, ex.Message);
                    ReconnectClient(client, gw);
                    return;
                }

                buffer += chunk;

                int sepIdx;
                while ((sepIdx = buffer.IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                {
                    string eventBlock = buffer.Substring(0, sepIdx);
                    buffer = buffer.Substring(sepIdx + 4);
                    if (!string.IsNullOrWhiteSpace(eventBlock))
                        ProcessAmiEvent(eventBlock, gw);
                }
            }
        }

        private static string SocketReadChunk(TcpClient client)
        {
            NetworkStream stream = client.GetStream();
            byte[] buf = new byte[4096];
            int n = stream.Read(buf, 0, buf.Length);
            return Encoding.UTF8.GetString(buf, 0, n);
        }

        // Разбор одного AMI-пакета ("Key: Value" построчно) в словарь.
        // Порядок полей AMI не гарантирован спецификацией — читать только по имени ключа.
        private static Dictionary<string, string> ParseAmiEvent(string block)
        {
            var dict = new Dictionary<string, string>();
            foreach (var line in block.Replace("\r", "").Split('\n'))
            {
                if (string.IsNullOrEmpty(line)) continue;
                int idx = line.IndexOf(": ", StringComparison.Ordinal);
                if (idx < 0) continue;
                dict[line.Substring(0, idx)] = line.Substring(idx + 2);
            }
            return dict;
        }

        private static int ParseIntOr(Dictionary<string, string> dict, string key, int fallback)
            => dict.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;

        private static void ProcessAmiEvent(string eventBlock, Gateway gw)
        {
            var evt = ParseAmiEvent(eventBlock);

            if (evt.TryGetValue("Event", out var eventName) && eventName == "QuectelNewSMS")
            {
                HandleQuectelSms(evt, gw);
            }
            else if (evt.ContainsKey("Content"))
            {
                HandleYeastarSms(evt, gw);
            }
            // Явная ошибка AMI (например, неудачный логин) — иначе она бы прошла
            // абсолютно незаметно: TCP-подключение при этом успешно, событий просто
            // никогда не будет, и выглядит это как "подключились, но тишина".
            else if (evt.TryGetValue("Response", out var response) && response == "Error")
            {
                Console.WriteLine($"AMI ({gw.id}) error response: {evt.GetValueOrDefault("Message", "(no message)")}");
                HealthStatus.MarkError(gw.id, "AMI error: " + evt.GetValueOrDefault("Message", "(no message)"));
            }
            // остальное (QuectelNewCMGR, Newchannel/Hangup служебного Local-канала,
            // успешный ответ на Action: Login / Action: Command и т.п.) — не парсим, не нужно.
        }

        private static void HandleYeastarSms(Dictionary<string, string> evt, Gateway gw)
        {
            string txt = HttpUtility.UrlDecode(evt.GetValueOrDefault("Content", "").Replace("%EF%BB%BF", ""));
            // NB: поле идентификации порта в оригинальном коде сопоставлялось по позиции без
            // явного имени; по конвенции AMI это "Channel". Если у реального события Yeastar
            // ключ называется иначе — поправить здесь единственную строку.
            string channelField = evt.GetValueOrDefault("Channel", "");
            Channel chan = getChannel("Channel: " + channelField, gw.id);
            if (chan == null)
            {
                Console.WriteLine($"AMI ({gw.id}): incoming SMS on channel '{channelField}' matched no configured channel, dropping");
                return;
            }

            string sender = ToE164(evt.GetValueOrDefault("Sender", ""));
            string recvTime = evt.GetValueOrDefault("Recvtime", "");
            int total = ParseIntOr(evt, "Total", 1);
            int index = ParseIntOr(evt, "Index", 1);

            if (total > 1)
            {
                string key = gw.id + "|" + chan.name + "|" + sender;
                var parts = _multipartBuffers.GetOrAdd(key, _ => new string[total]);
                if (index >= 1 && index <= parts.Length)
                    parts[index - 1] = txt;

                if (Array.TrueForAll(parts, p => p != null))
                {
                    _multipartBuffers.TryRemove(key, out _);
                    string full = string.Join("", parts);
                    DeliverSms("y", gw.id, chan, sender, recvTime,
                        telegramText: full.Replace("+", "\n").Replace("\r", "\\r"),
                        logText: full.Replace("\n", " ").Replace("\r", "\\r"));
                }
            }
            else
            {
                DeliverSms("y", gw.id, chan, sender, recvTime,
                    telegramText: txt.Replace("\n", " ").Replace("\r", "\\r"),
                    logText: txt.Replace("\n", " ").Replace("\r", "\\r"));
            }
        }

        private static void HandleQuectelSms(Dictionary<string, string> evt, Gateway gw)
        {
            string device = evt.GetValueOrDefault("Device", "");
            Channel chan = getChannelByLine(gw.id, device);
            if (chan == null)
            {
                Console.WriteLine($"AMI ({gw.id}): incoming SMS on device '{device}' matched no configured channel, dropping");
                return;
            }

            string sender = ToE164(evt.GetValueOrDefault("From", ""));
            int lineCount = ParseIntOr(evt, "LineCount", 1);
            var lines = new List<string>();
            for (int i = 0; i < lineCount; i++)
                if (evt.TryGetValue("MessageLine" + i, out var line))
                    lines.Add(line);

            // MessageLine0 нередко приходит с ведущим BOM (U+FEFF) — убираем, как и для yeastar.
            string full = string.Join("\n", lines).Replace("﻿", "");
            string recvTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            DeliverSms("q", gw.id, chan, sender, recvTime,
                telegramText: full.Replace("\r", "\\r"),
                logText: full.Replace("\n", " ").Replace("\r", "\\r"));
        }
    }
}
