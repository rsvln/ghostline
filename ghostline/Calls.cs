using MySqlConnector;

namespace ghostline
{
    // Call journal: polling the PBX CDR (asteriskcdrdb.cdr) and turning rows into calls.
    // Polling rather than AMI events: survives restarts of both sides and catches up on its own.
    // One CDR row = one call (on this PBX every call is one row with its own linkedid).
    internal partial class Program
    {
        private const string KvInitialized = "calls_initialized";
        private const string KvWatermark = "cdr_watermark";

        private static async Task CallsPollerLoop()
        {
            var db = settings.calls.cdrDb;
            string cs = new MySqlConnectionStringBuilder
            {
                Server = db.host,
                Port = (uint)db.port,
                Database = db.database,
                UserID = db.user,
                Password = db.password,
                ConnectionTimeout = 10,
                SslMode = MySqlSslMode.Preferred
            }.ConnectionString;

            HealthStatus.MarkConfigured("cdr");
            while (true)
            {
                try
                {
                    int added = await PollCdrOnce(cs);
                    HealthStatus.MarkConnected("cdr");
                    if (added > 0)
                        HealthStatus.MarkActivity("cdr");
                }
                catch (Exception ex)
                {
                    string err = DescribeException(ex);
                    HealthStatus.MarkDisconnected("cdr");
                    HealthStatus.MarkError("cdr", err);
                    Console.WriteLine("CDR poll failed: " + err);
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, settings.calls.pollSeconds)));
            }
        }

        // A CDR row is written at hangup while calldate is the start time. A long call that
        // started before the last one seen shows up later, so the window overlaps one day
        // back and duplicates are dropped by UNIQUE(uniqueid).
        private static async Task<int> PollCdrOnce(string connectionString)
        {
            bool initialized = Store.GetKv(KvInitialized) != null;
            string watermark = Store.GetKv(KvWatermark);
            DateTime from = watermark != null ? ParseTs(watermark).AddDays(-1) : DateTime.MinValue;

            var rows = new List<CdrRow>();
            await using (var conn = new MySqlConnection(connectionString))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT calldate, uniqueid, src, dst, did, channel, dstchannel, disposition,
                                           duration, billsec, recordingfile
                                    FROM cdr WHERE calldate >= @from ORDER BY calldate, sequence";
                cmd.Parameters.AddWithValue("@from", from == DateTime.MinValue ? new DateTime(2000, 1, 1) : from);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    rows.Add(new CdrRow
                    {
                        CallDate = r.GetDateTime(0),
                        UniqueId = r.GetString(1),
                        Src = r.IsDBNull(2) ? "" : r.GetString(2),
                        Dst = r.IsDBNull(3) ? "" : r.GetString(3),
                        Did = r.IsDBNull(4) ? "" : r.GetString(4),
                        Channel = r.IsDBNull(5) ? "" : r.GetString(5),
                        DstChannel = r.IsDBNull(6) ? "" : r.GetString(6),
                        Disposition = r.IsDBNull(7) ? "" : r.GetString(7),
                        Duration = r.GetInt32(8),
                        Billsec = r.GetInt32(9),
                        RecordingFile = r.IsDBNull(10) ? "" : r.GetString(10)
                    });
                }
            }

            int added = 0;
            DateTime max = watermark != null ? ParseTs(watermark) : DateTime.MinValue;
            foreach (var row in rows)
            {
                if (row.CallDate > max) max = row.CallDate;

                // The poll window overlaps by a day, so known calls are skipped right away:
                // no HEAD to the PBX and no AUTOINCREMENT numbers wasted on INSERT OR IGNORE.
                if (Store.CallExists(row.UniqueId)) continue;

                var (call, ch) = MapCdr(row);
                if (call == null) continue;

                // The recording size is checked right away (HEAD) so empty ones are neither shown nor transcribed.
                if (call.RecPath != null)
                    call.RecSize = await Recordings.HeadSize(call.RecPath);

                bool recOk = call.RecPath != null && (call.RecSize > Store.MinRecordingBytes || call.RecSize < 0);
                call.TrState = TranscribeEnabled && call.Billsec > 0 && recOk ? "pending" : "none";

                long id = Store.InsertCall(call);
                if (id == 0) continue;   // already there
                call.Id = id;
                added++;
                Console.WriteLine($"Call {call.Ts} {call.Direction} {call.Channel} {call.Peer} {call.Billsec}s rec={call.RecSize}");

                // On the first start the whole history is imported; it is not sent to Telegram or the log.
                if (initialized)
                {
                    EnqueueCallNotifications(call, ch);
                    LogCall(call);
                }
            }

            if (max > DateTime.MinValue)
                Store.SetKv(KvWatermark, max.ToString("yyyy-MM-dd HH:mm:ss"));
            if (!initialized)
            {
                Store.SetKv(KvInitialized, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Console.WriteLine($"Calls: initial import done, {added} calls");
            }
            return added;
        }

        internal static DateTime ParseTs(string ts) =>
            DateTime.ParseExact(ts, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        private static bool TranscribeEnabled =>
            settings.calls?.transcribe is { enabled: true } t && !string.IsNullOrEmpty(t.url);

        private class CdrRow
        {
            public DateTime CallDate;
            public string UniqueId, Src, Dst, Did, Channel, DstChannel, Disposition, RecordingFile;
            public int Duration, Billsec;
        }

        // Quectel/gsm1-0100000002 → gsm1
        private static string QuectelDevice(string channel)
        {
            if (!channel.StartsWith("Quectel/", StringComparison.OrdinalIgnoreCase)) return null;
            string rest = channel.Substring("Quectel/".Length);
            int dash = rest.IndexOf('-');
            return dash > 0 ? rest.Substring(0, dash) : rest;
        }

        // Incoming: the GSM channel is the source (channel), or the line is in did (the call came
        // through a Local channel). Outgoing: the GSM channel is the destination (dstchannel). Everything
        // else (internal calls 11<->12, service channels for SMS/USSD) are not line calls.
        private static (CallRecord, Channel) MapCdr(CdrRow r)
        {
            if (r.Dst is "sms" or "ussd") return (null, null);

            string device, direction;
            if ((device = QuectelDevice(r.Channel)) != null) direction = "in";
            else if ((device = QuectelDevice(r.DstChannel)) != null) direction = "out";
            else if (!string.IsNullOrEmpty(r.Did) && settings.channels.Any(c => c.line == r.Did)) { device = r.Did; direction = "in"; }
            else return (null, null);

            var ch = settings.channels.FirstOrDefault(c => c.line == device);
            if (ch == null) return (null, null);

            string peer = ToE164(direction == "in" ? r.Src : r.Dst);
            string recPath = string.IsNullOrEmpty(r.RecordingFile)
                ? null
                : r.CallDate.ToString("yyyy/MM/dd") + "/" + r.RecordingFile;

            return (new CallRecord
            {
                UniqueId = r.UniqueId,
                Ts = r.CallDate.ToString("yyyy-MM-dd HH:mm:ss"),
                Channel = ch.name,
                Device = device,
                Direction = direction,
                Peer = peer ?? "",
                PeerName = ContactName(peer),
                Ext = direction == "in" ? r.Dst : r.Src,
                Disposition = r.Disposition,
                Duration = r.Duration,
                Billsec = r.Billsec,
                RecPath = recPath,
                RecSize = -1
            }, ch);
        }

        // A call in the text log, next to SMS and in the same format: "📞 Incoming 0:52".
        private static void LogCall(CallRecord c)
        {
            if (!settings.logger.file) return;
            string kind = L10n.Web.T(c.Missed
                ? (c.Direction == "in" ? "web.calls.kind.missed" : "web.calls.kind.no_answer")
                : (c.Direction == "in" ? "web.calls.kind.in" : "web.calls.kind.out"));
            string text = "📞 " + kind + (c.Missed ? "" : $" {c.Billsec / 60}:{c.Billsec % 60:D2}");
            string who = string.IsNullOrEmpty(c.PeerName) ? c.Peer : c.Peer + " " + c.PeerName;
            Log("q", c.Direction, c.Ts, c.Channel, who, text);
        }

        internal static string ContactName(string number)
        {
            if (string.IsNullOrEmpty(number)) return null;
            var c = FindContact(number);
            if (c == null) return null;
            string name = (c.LastName + " " + c.FirstName + " " + c.MiddleName).Trim();
            return name.Length > 0 ? name : null;
        }
    }
}
