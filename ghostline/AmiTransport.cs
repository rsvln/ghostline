using System.IO;
using System.Net;
using System.Text;
using System.Web;

namespace ghostline
{
    internal partial class Program
    {
        // Connects to AMI with retries: if the server is unavailable (on the first start
        // or when reconnecting after a drop), do not fail but retry every 5 s until it
        // works. One entry point both for the initial connection (started with Task.Run
        // from Main, so one unavailable gateway does not block the others) and for the
        // reconnect after a drop.
        private static void ConnectAmiWithRetry(Gateway gw, TcpClient client)
        {
            while (true)
            {
                try
                {
                    client.Client.Connect(IPAddress.Parse(gw.ip), gw.amiPort);
                    // If nothing at all arrives on the socket in this time (including the reply to
                    // the keepalive ping from AmiKeepaliveLoop), the read throws on timeout, which
                    // sends a "silent" drop into the same reconnect loop.
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

        // On Linux (Alpine in a container) .NET does not allow a synchronous Connect() again
        // on the same Socket object after Disconnect(reuseSocket: true), only an asynchronous
        // one and only to another EndPoint (otherwise it throws "Once the socket has been
        // disconnected..."). So the object is not reused: a new TcpClient is created and
        // replaced in the shared dictionary; everything else (DispatchSms, AmiKeepaliveLoop)
        // gets the client by gateway id each time instead of keeping a direct reference.
        private static void ReconnectClient(TcpClient oldClient, Gateway gw)
        {
            try { oldClient.Close(); } catch { }

            var newClient = new TcpClient();
            _amiClients[gw.id] = newClient;
            ConnectAmiWithRetry(gw, newClient);
        }

        // Sends "Action: Ping" every 30 s on a live connection. If the socket is already dead,
        // either the send fails (ignored, the receive timeout on the read catches the drop
        // anyway) or nothing at all arrives within 90 s and the read itself starts the
        // reconnect. Started once per gateway and survives all later reconnects: the current
        // client is taken from _amiClients on every tick, so no stale reference is kept after
        // the connection is replaced.
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

        // Collects socket bytes in a buffer until a full AMI packet (\r\n\r\n) arrives,
        // parses it and dispatches events one by one; the rest stays in the buffer until the next read.
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

        // Parses one AMI packet ("Key: Value" per line) into a dictionary.
        // The order of AMI fields is not guaranteed by the spec, so read by key name only.
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
            // An explicit AMI error (for example a failed login); otherwise it would go
            // completely unnoticed: the TCP connection succeeds, events simply never
            // come, and it looks like "connected, but silence".
            else if (evt.TryGetValue("Response", out var response) && response == "Error")
            {
                Console.WriteLine($"AMI ({gw.id}) error response: {evt.GetValueOrDefault("Message", "(no message)")}");
                HealthStatus.MarkError(gw.id, "AMI error: " + evt.GetValueOrDefault("Message", "(no message)"));
            }
            // everything else (QuectelNewCMGR, Newchannel/Hangup of the service Local channel,
            // a successful reply to Action: Login / Action: Command, etc.) is not parsed, not needed.
        }

        private static void HandleYeastarSms(Dictionary<string, string> evt, Gateway gw)
        {
            string txt = HttpUtility.UrlDecode(evt.GetValueOrDefault("Content", "").Replace("%EF%BB%BF", ""));
            // NB: the port id field was matched by position without an explicit name in the original
            // code; by AMI convention it is "Channel". If a real Yeastar event names the key
            // differently, fix this single line.
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

            // MessageLine0 often comes with a leading BOM (U+FEFF); strip it, as for yeastar.
            string full = string.Join("\n", lines).Replace("﻿", "");
            string recvTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            DeliverSms("q", gw.id, chan, sender, recvTime,
                telegramText: full.Replace("\r", "\\r"),
                logText: full.Replace("\n", " ").Replace("\r", "\\r"));
        }
    }
}
