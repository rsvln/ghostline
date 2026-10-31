using System.Data.Common;
using Microsoft.Data.Sqlite;
using MySqlConnector;

namespace ghostline
{
    // Call journal: polling the PBX CDR and turning rows into calls. The CDR is either the FreePBX
    // MySQL table (asteriskcdrdb.cdr) or a SQLite file written by Asterisk's cdr_sqlite3_custom.
    // Polling rather than AMI events: survives restarts of both sides and catches up on its own.
    // One CDR row = one call (on this PBX every call is one row with its own linkedid).
    internal partial class Program
    {
        private const string KvInitialized = "calls_initialized";
        private const string KvWatermark = "cdr_watermark";

        private static bool CdrIsSqlite => !string.IsNullOrEmpty(settings.calls.cdrSqlite);

        private static DbConnection CdrConnection()
        {
            if (CdrIsSqlite)
                // Read-only: Asterisk is the only writer. A busy timeout rides over its short write locks.
                return new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = settings.calls.cdrSqlite,
                    Mode = SqliteOpenMode.ReadOnly,
                    DefaultTimeout = 10
                }.ConnectionString);

            var db = settings.calls.cdrDb;
            return new MySqlConnection(new MySqlConnectionStringBuilder
            {
                Server = db.host,
                Port = (uint)db.port,
                Database = db.database,
                UserID = db.user,
                Password = db.password,
                ConnectionTimeout = 10,
                SslMode = MySqlSslMode.Preferred
            }.ConnectionString);
        }

        // Where the CDR comes from, for the Status tab.
        internal static string CdrSource(CallsSettings c) =>
            !string.IsNullOrEmpty(c?.cdrSqlite) ? c.cdrSqlite
            : c?.cdrDb is { } db ? $"{db.host}:{db.port}"
            : null;

        private static async Task CallsPollerLoop()
        {
            HealthStatus.MarkConfigured("cdr");
            while (true)
            {
                try
                {
                    int added = await PollCdrOnce();
                    HealthStatus.MarkConnected("cdr");
                    if (added > 0)
                        HealthStatus.MarkActivity("cdr");
                }
                catch (Exception ex)
                {
                    string err = DescribeException(ex);
                    HealthStatus.MarkDisconnected("cdr");
                    HealthStatus.MarkError("cdr", err);
                    SysLog.Warn("calls", "CDR poll failed: " + err);
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, settings.calls.pollSeconds)));
            }
        }

        // A CDR row is written at hangup while calldate is the start time. A long call that
        // started before the last one seen shows up later, so the window overlaps one day
        // back and duplicates are dropped by UNIQUE(uniqueid).
        private static async Task<int> PollCdrOnce()
        {
            bool initialized = Store.GetKv(KvInitialized) != null;
            string watermark = Store.GetKv(KvWatermark);
            DateTime from = watermark != null ? ParseTs(watermark).AddDays(-1) : new DateTime(2000, 1, 1);

            var rows = new List<CdrRow>();
            await using (var conn = CdrConnection())
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                // SQLite keeps calldate as "yyyy-MM-dd HH:mm:ss" text, which compares in time order; it has no
                // sequence column, rowid is the insert order there.
                cmd.CommandText = @"SELECT calldate, uniqueid, src, dst, did, channel, dstchannel, disposition,
                                           duration, billsec, recordingfile
                                    FROM cdr WHERE calldate >= @from ORDER BY calldate, " + (CdrIsSqlite ? "rowid" : "sequence");
                var p = cmd.CreateParameter();
                p.ParameterName = "@from";
                p.Value = CdrIsSqlite ? from.ToString("yyyy-MM-dd HH:mm:ss") : from;
                cmd.Parameters.Add(p);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    string Text(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
                    rows.Add(new CdrRow
                    {
                        CallDate = r.GetValue(0) is DateTime dt ? dt : ParseTs(Text(0)),
                        UniqueId = Text(1),
                        Src = Text(2),
                        Dst = Text(3),
                        Did = Text(4),
                        Channel = Text(5),
                        DstChannel = Text(6),
                        Disposition = Text(7),
                        Duration = int.TryParse(Text(8), out var dur) ? dur : 0,
                        Billsec = int.TryParse(Text(9), out var bs) ? bs : 0,
                        RecordingFile = Text(10)
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

                // On the first start the whole history is imported; it is not sent to Telegram or the log.
                if (initialized)
                {
                    SysLog.Info("calls", $"{call.Channel}: {(call.Direction == "in" ? "incoming" : "outgoing")} call " +
                                         $"{(call.Missed ? "not answered" : $"{call.Billsec} s")}");
                    EnqueueCallNotifications(call, ch);
                    LogCall(call);
                }
            }

            if (max > DateTime.MinValue)
                Store.SetKv(KvWatermark, max.ToString("yyyy-MM-dd HH:mm:ss"));
            if (!initialized)
            {
                Store.SetKv(KvInitialized, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                SysLog.Info("calls", $"initial import done, {added} calls");
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

        // Incoming: the GSM channel is the source (channel), or the line is in did (the call came
        // through a Local channel). Outgoing: the GSM channel is the destination (dstchannel). Everything
        // else (internal calls 11<->12, service channels for SMS/USSD) are not line calls.
        private static (CallRecord, Channel) MapCdr(CdrRow r)
        {
            if (r.Dst is "sms" or "ussd") return (null, null);

            string device, direction;
            if ((device = Modems.Device(r.Channel)) != null) direction = "in";
            else if ((device = Modems.Device(r.DstChannel)) != null) direction = "out";
            else if (!string.IsNullOrEmpty(r.Did) && settings.lines.Any(c => c.Device == r.Did)) { device = r.Did; direction = "in"; }
            else return (null, null);

            var ch = settings.lines.FirstOrDefault(c => c.Device == device);
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

        // A call next to SMS, in the journal and in the text log: "📞 Incoming 0:52".
        internal static string CallText(CallRecord c)
        {
            string kind = L10n.Web.T(c.Missed
                ? (c.Direction == "in" ? "web.calls.kind.missed" : "web.calls.kind.no_answer")
                : (c.Direction == "in" ? "web.calls.kind.in" : "web.calls.kind.out"));
            return "📞 " + kind + (c.Missed ? "" : $" {c.Billsec / 60}:{c.Billsec % 60:D2}");
        }

        private static void LogCall(CallRecord c)
        {
            if (!settings.logger.file) return;
            string who = string.IsNullOrEmpty(c.PeerName) ? c.Peer : c.Peer + " " + c.PeerName;
            Log("q", c.Direction, c.Ts, c.Channel, who, CallText(c));
        }

        // An SMS as the web lists show it, next to calls: the contact name for both directions, and for
        // an outgoing SMS where it was sent from (web-ui, telegram, ntfy).
        internal record SmsItem(string kind, long id, string ts, string direction, string channel, string peer,
                                string peerName, string text, string origin);

        internal static List<SmsItem> SmsList(int limit, string direction, string channel, string q, string from, string to) =>
            Store.Query(limit, direction, channel, q, from, to).Select(m => new SmsItem("sms", m.Id, m.Ts, m.Direction, m.Channel, m.Peer,
                m.Direction == "in" ? m.Note : ContactName(m.Peer), m.Content, m.Direction == "out" ? m.Note : null)).ToList();

        // The PBX tab: SMS and calls in one list, newest first (both tables keep ts as "yyyy-MM-dd HH:mm:ss").
        // kind: "sms", "calls" or empty for both; missed / rec filter the calls only.
        internal static List<object> PbxList(int limit, string kind, string direction, string channel, string q,
                                             string from, string to, bool missed, bool rec)
        {
            var items = new List<(string ts, object item)>();
            if (kind != "calls")
                items.AddRange(SmsList(limit, direction, channel, q, from, to).Select(s => (s.ts, (object)s)));
            if (kind != "sms" && settings?.FullMode == true)
                items.AddRange(Store.QueryCalls(limit, channel, direction, missed, rec, q, from, to).Select(c => (c.Ts, (object)c)));
            return items.OrderByDescending(x => x.ts, StringComparer.Ordinal).Take(limit).Select(x => x.item).ToList();
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
