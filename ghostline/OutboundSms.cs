namespace ghostline
{
    internal partial class Program
    {
        // Quick check and queuing of an outgoing SMS in the persistent queue.
        // Used both by the Telegram bot and by the web UI (/api/sms/send). The actual
        // sending happens asynchronously in SmsOutboxWorker with retries, so a temporary
        // gateway outage (HTTP or AMI) no longer loses a message silently.
        internal static (bool ok, string? error) QueueSmsSend(Channel chanOut, string rawNumber, string mcontent, string sourceLabel)
        {
            // If the gateway is in gateways[], its client was already created in Main; there is
            // no need to check separately whether "the IP is set": gateways[] itself is the source
            // of truth about what is running. An unknown gateway is a config error.
            var gw = getGatewayById(chanOut.gateway);
            if (gw == null)
                return (false, "channel '" + chanOut.name + "' references unknown gateway '" + chanOut.gateway + "'");

            // AMI and most gateway firmwares expect a single-line command text; raw line breaks
            // (for example from the multi-line textarea of the web UI) break the line-based AMI
            // protocol (Action: Command for quectel), so they are folded into spaces here, the
            // same for all channel types.
            mcontent = mcontent.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");

            // The number is made canonical (E.164) right at the input, so the queue, DB and UI always
            // show the same form whatever the user typed. The dial format for a particular
            // gateway or dial plan is derived from this canonical form in DispatchSms.
            string canonical = ToE164(rawNumber);

            Store.EnqueueOutgoingSms(chanOut.name, canonical, mcontent, sourceLabel);
            return (true, null);
        }

        // Converts the canonical number to the dial format of a particular gateway.
        // Short codes ("900") and alphanumeric ids are dialed as is: prefixing them with
        // "810"/"8" would send them nowhere.
        private static string FormatForGateway(string canonical, string gatewayType)
        {
            if (string.IsNullOrEmpty(canonical) || !canonical.StartsWith("+"))
                return canonical;

            // quectel expects E.164 with "+", which is already the case.
            if (gatewayType == "quectel")
                return canonical;

            // goip / yeastar: Russian dial plan, "8" for domestic numbers, "810" plus the
            // country code for international ones.
            return canonical.StartsWith("+7")
                ? "8" + canonical.Substring(2)
                : "810" + canonical.Substring(1);
        }

        // The actual send attempt through the right gateway. Throws on failure; the
        // caller (SmsOutboxWorker) catches it and decides whether to retry.
        // rawNumber is already canonical (see QueueSmsSend); here the dial format for the
        // gateway's dial plan (dest) is derived from it, while history, UI and Telegram
        // always get the original canonical rawNumber, not dest.
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

        // Background worker of the outgoing SMS queue: every 5 s takes the pending sends
        // and tries them. Success removes the item from the queue; failure records the
        // attempt and keeps it for the next cycle (no limit on attempts, so it survives
        // both a temporary gateway outage and a process restart).
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

            // Notification to the Telegram chats that an SMS was sent, the same for bot commands
            // and for sending from the web UI (before, only the bot path did this, so SMS sent
            // from the web were not visible in Telegram).
            string body = "sent to " + peer + " from " + sourceLabel + " via " + chan.name + " at " + ts + "\n----------------\n\n" + mcontent;
            EnqueueTelegramBroadcast(body);
        }
    }
}
