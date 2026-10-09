using Microsoft.Data.Sqlite;

namespace ghostline
{
    public class CallRecord
    {
        public long Id { get; set; }
        public string UniqueId { get; set; }
        public string Ts { get; set; }
        public string Channel { get; set; }      // line name from the config (home/work)
        public string Device { get; set; }       // gsm1/gsm2
        public string Direction { get; set; }    // "in" / "out"
        public string Peer { get; set; }         // number of the other side, E.164
        public string PeerName { get; set; }     // name from contacts.csv
        public string Ext { get; set; }          // extension (11/12)
        public string Disposition { get; set; }
        public int Duration { get; set; }
        public int Billsec { get; set; }         // talk time; 0: the call did not take place
        public string RecPath { get; set; }      // YYYY/MM/DD/file.wav relative to monitor/
        public long RecSize { get; set; }        // -1 unknown, otherwise the file size on the PBX
        public string TrState { get; set; }      // none / pending / done / error
        public string TrText { get; set; }
        public string TrError { get; set; }
        public string TrSegments { get; set; }   // JSON: [{s, e, who, t}], who = "me"/"them"/null

        public bool Missed => Billsec == 0;
        public bool HasRecording => !string.IsNullOrEmpty(RecPath) && RecSize > Store.MinRecordingBytes;
    }

    public class CallTgItem
    {
        public long Id { get; set; }
        public long CallId { get; set; }
        public string ChatId { get; set; }
        public string Kind { get; set; }         // "voice" / "text"
        public bool WantTranscript { get; set; }
        public long MessageId { get; set; }
        public int Attempts { get; set; }
    }

    // Calls (from the PBX CDR), the transcription queue and the queue of call notifications to Telegram.
    // The calls schema is versioned with PRAGMA user_version.
    public static partial class Store
    {
        // An empty wav is just a header (44 bytes): an unanswered call or the SMS channel.
        public const long MinRecordingBytes = 1024;

        internal static void MigrateCalls(SqliteConnection db)
        {
            long version;
            using (var v = db.CreateCommand())
            {
                v.CommandText = "PRAGMA user_version;";
                version = (long)v.ExecuteScalar();
            }

            if (version < 1)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS calls (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        uniqueid TEXT NOT NULL UNIQUE,
                        ts TEXT NOT NULL,
                        channel TEXT NOT NULL,
                        device TEXT NOT NULL,
                        direction TEXT NOT NULL,
                        peer TEXT NOT NULL,
                        peer_name TEXT,
                        ext TEXT,
                        disposition TEXT,
                        duration INTEGER NOT NULL DEFAULT 0,
                        billsec INTEGER NOT NULL DEFAULT 0,
                        rec_path TEXT,
                        rec_size INTEGER NOT NULL DEFAULT -1,
                        tr_state TEXT NOT NULL DEFAULT 'none',
                        tr_text TEXT,
                        tr_error TEXT,
                        tr_attempts INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE INDEX IF NOT EXISTS idx_calls_ts ON calls(ts);
                    CREATE INDEX IF NOT EXISTS idx_calls_tr ON calls(tr_state);

                    CREATE VIRTUAL TABLE IF NOT EXISTS calls_fts USING fts5(text, tokenize = 'unicode61 remove_diacritics 2');

                    CREATE TABLE IF NOT EXISTS call_tg (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        call_id INTEGER NOT NULL,
                        chat_id TEXT NOT NULL,
                        kind TEXT NOT NULL,
                        want_transcript INTEGER NOT NULL,
                        state TEXT NOT NULL DEFAULT 'pending',
                        message_id INTEGER NOT NULL DEFAULT 0,
                        attempts INTEGER NOT NULL DEFAULT 0,
                        last_error TEXT
                    );
                    CREATE INDEX IF NOT EXISTS idx_call_tg_state ON call_tg(state);

                    CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT);

                    PRAGMA user_version = 1;";
                cmd.ExecuteNonQuery();
                version = 1;
            }

            if (version < 2)
            {
                // Lines with timestamps (and a side when there are per-side legs).
                // Old transcripts are plain text: queue them again so that they
                // are split into lines.
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"
                    ALTER TABLE calls ADD COLUMN tr_segments TEXT;
                    UPDATE calls SET tr_state = 'pending', tr_attempts = 0 WHERE tr_state = 'done';
                    PRAGMA user_version = 2;";
                cmd.ExecuteNonQuery();
                version = 2;
            }

            if (version < 3)
            {
                // Transcribing again from the button jumps the queue, ahead of the background ones.
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"
                    ALTER TABLE calls ADD COLUMN tr_requested_at TEXT;
                    PRAGMA user_version = 3;";
                cmd.ExecuteNonQuery();
            }
        }

        // --- kv ---

        public static string GetKv(string key)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT value FROM kv WHERE key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }

        public static void SetKv(string key, string value)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO kv(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }

        // --- calls ---

        public static bool CallExists(string uniqueId)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM calls WHERE uniqueid = $u;";
            cmd.Parameters.AddWithValue("$u", uniqueId);
            return cmd.ExecuteScalar() != null;
        }

        // Returns the id of the new row, or 0 if a call with this uniqueid already exists.
        public static long InsertCall(CallRecord c)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO calls (uniqueid, ts, channel, device, direction, peer, peer_name, ext,
                                             disposition, duration, billsec, rec_path, rec_size, tr_state)
                VALUES ($u, $ts, $ch, $dev, $dir, $peer, $name, $ext, $disp, $dur, $bill, $rp, $rs, $tr);
                SELECT CASE WHEN changes() > 0 THEN last_insert_rowid() ELSE 0 END;";
            cmd.Parameters.AddWithValue("$u", c.UniqueId);
            cmd.Parameters.AddWithValue("$ts", c.Ts);
            cmd.Parameters.AddWithValue("$ch", c.Channel);
            cmd.Parameters.AddWithValue("$dev", c.Device);
            cmd.Parameters.AddWithValue("$dir", c.Direction);
            cmd.Parameters.AddWithValue("$peer", c.Peer ?? "");
            cmd.Parameters.AddWithValue("$name", (object)c.PeerName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ext", (object)c.Ext ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$disp", (object)c.Disposition ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dur", c.Duration);
            cmd.Parameters.AddWithValue("$bill", c.Billsec);
            cmd.Parameters.AddWithValue("$rp", (object)c.RecPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rs", c.RecSize);
            cmd.Parameters.AddWithValue("$tr", c.TrState ?? "none");
            return (long)cmd.ExecuteScalar();
        }

        private const string CallColumns = "id, uniqueid, ts, channel, device, direction, peer, peer_name, ext, disposition, duration, billsec, rec_path, rec_size, tr_state, tr_text, tr_error, tr_segments";

        private static CallRecord ReadCall(SqliteDataReader r) => new CallRecord
        {
            Id = r.GetInt64(0),
            UniqueId = r.GetString(1),
            Ts = r.GetString(2),
            Channel = r.GetString(3),
            Device = r.GetString(4),
            Direction = r.GetString(5),
            Peer = r.GetString(6),
            PeerName = r.IsDBNull(7) ? null : r.GetString(7),
            Ext = r.IsDBNull(8) ? null : r.GetString(8),
            Disposition = r.IsDBNull(9) ? null : r.GetString(9),
            Duration = r.GetInt32(10),
            Billsec = r.GetInt32(11),
            RecPath = r.IsDBNull(12) ? null : r.GetString(12),
            RecSize = r.GetInt64(13),
            TrState = r.GetString(14),
            TrText = r.IsDBNull(15) ? null : r.GetString(15),
            TrError = r.IsDBNull(16) ? null : r.GetString(16),
            TrSegments = r.IsDBNull(17) ? null : r.GetString(17)
        };

        public static CallRecord GetCall(long id)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"SELECT {CallColumns} FROM calls WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadCall(r) : null;
        }

        // Search string to an FTS5 query: every word quoted with a prefix match, all words required.
        // Quotes and FTS special characters from user input are not passed through.
        private static string FtsQuery(string q)
        {
            var words = q.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                         .Select(w => new string(w.Where(ch => char.IsLetterOrDigit(ch)).ToArray()))
                         .Where(w => w.Length > 0)
                         .Select(w => "\"" + w + "\"*");
            return string.Join(" ", words);
        }

        public static List<CallRecord> QueryCalls(int limit, string channel, string direction, bool missedOnly,
                                                  bool withRecordingOnly, string q, string from, string to)
        {
            var result = new List<CallRecord>();
            using var db = Open();
            using var cmd = db.CreateCommand();

            var where = new List<string>();
            if (!string.IsNullOrEmpty(channel)) { where.Add("channel = $ch"); cmd.Parameters.AddWithValue("$ch", channel); }
            if (!string.IsNullOrEmpty(direction)) { where.Add("direction = $dir"); cmd.Parameters.AddWithValue("$dir", direction); }
            if (missedOnly) where.Add("billsec = 0");
            // "Recorded": there was a conversation and it was recorded. Unanswered calls can have a
            // non-empty recording (ringback, an operator announcement), but no conversation in it.
            if (withRecordingOnly) { where.Add("billsec > 0 AND rec_path IS NOT NULL AND rec_size > $minrec"); cmd.Parameters.AddWithValue("$minrec", MinRecordingBytes); }
            if (!string.IsNullOrEmpty(from)) { where.Add("ts >= $from"); cmd.Parameters.AddWithValue("$from", from); }
            if (!string.IsNullOrEmpty(to)) { where.Add("ts < date($to, '+1 day')"); cmd.Parameters.AddWithValue("$to", to); }
            if (!string.IsNullOrWhiteSpace(q))
            {
                string digits = new string(q.Where(char.IsDigit).ToArray());
                string fts = FtsQuery(q);
                var or = new List<string> { "peer_name LIKE $like" };
                cmd.Parameters.AddWithValue("$like", "%" + q.Trim() + "%");
                if (digits.Length >= 3) { or.Add("peer LIKE $digits"); cmd.Parameters.AddWithValue("$digits", "%" + digits + "%"); }
                if (fts.Length > 0) { or.Add("id IN (SELECT rowid FROM calls_fts WHERE calls_fts MATCH $fts)"); cmd.Parameters.AddWithValue("$fts", fts); }
                where.Add("(" + string.Join(" OR ", or) + ")");
            }
            string whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
            cmd.CommandText = $"SELECT {CallColumns} FROM calls {whereSql} ORDER BY ts DESC, id DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);

            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(ReadCall(r));
            return result;
        }

        public static void SetRecSize(long id, long size)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE calls SET rec_size = $s WHERE id = $id;";
            cmd.Parameters.AddWithValue("$s", size);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        // --- transcription ---

        // New calls first: a fresh conversation matters more than catching up with the archive.
        // Two queues, each with its own worker, so a button request does not wait behind the
        // background transcription of long calls: requested = true, requested with the button (latest
        // request first); false, all others (newest calls first).
        public static CallRecord NextPendingTranscription(bool requested)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = requested
                ? $"SELECT {CallColumns} FROM calls WHERE tr_state = 'pending' AND tr_requested_at IS NOT NULL ORDER BY tr_requested_at DESC LIMIT 1;"
                : $"SELECT {CallColumns} FROM calls WHERE tr_state = 'pending' AND tr_requested_at IS NULL ORDER BY id DESC LIMIT 1;";
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadCall(r) : null;
        }

        public static void SetTranscript(long id, string text, string segmentsJson)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"UPDATE calls SET tr_state = 'done', tr_text = $t, tr_segments = $seg, tr_error = NULL, tr_requested_at = NULL WHERE id = $id;
                                    DELETE FROM calls_fts WHERE rowid = $id;
                                    INSERT INTO calls_fts(rowid, text) VALUES ($id, $t);";
                cmd.Parameters.AddWithValue("$t", text ?? "");
                cmd.Parameters.AddWithValue("$seg", (object)segmentsJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        // final: do not try again (a problem with the data itself, not an unavailable service).
        public static void SetTranscriptError(long id, string error, bool final)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = final
                ? "UPDATE calls SET tr_state = 'error', tr_error = $e, tr_attempts = tr_attempts + 1, tr_requested_at = NULL WHERE id = $id;"
                : "UPDATE calls SET tr_error = $e, tr_attempts = tr_attempts + 1 WHERE id = $id;";
            cmd.Parameters.AddWithValue("$e", error);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        public static int GetTranscriptAttempts(long id)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT tr_attempts FROM calls WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }

        public static bool RequeueTranscription(long id)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            // The sent notifications of this call are "waiting for the transcript" again: when the new
            // one is ready, the Telegram caption is updated.
            cmd.CommandText = @"UPDATE calls SET tr_state = 'pending', tr_error = NULL, tr_attempts = 0,
                                                 tr_requested_at = $now
                                WHERE id = $id AND rec_path IS NOT NULL;
                                UPDATE call_tg SET state = 'sent', attempts = 0
                                WHERE call_id = $id AND message_id > 0 AND want_transcript = 1 AND changes() > 0;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
            using var check = db.CreateCommand();
            check.CommandText = "SELECT tr_state = 'pending' FROM calls WHERE id = $id;";
            check.Parameters.AddWithValue("$id", id);
            return Convert.ToInt64(check.ExecuteScalar() ?? 0L) == 1;
        }

        public static (int pending, int done, int error) TranscriptionStats()
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT SUM(tr_state='pending'), SUM(tr_state='done'), SUM(tr_state='error') FROM calls;";
            using var r = cmd.ExecuteReader();
            r.Read();
            int Get(int i) => r.IsDBNull(i) ? 0 : r.GetInt32(i);
            return (Get(0), Get(1), Get(2));
        }

        public static int CountCalls()
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM calls;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public record CallStat(string channel, int inAnswered, int inMissed, int outAnswered, int outNoAnswer, string last);

        // Calls per line for the Status tab: answered and missed, by direction, and the time of the last one.
        public static List<CallStat> CallStatsByChannel()
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT channel,
                                       SUM(direction='in' AND billsec>0), SUM(direction='in' AND billsec=0),
                                       SUM(direction='out' AND billsec>0), SUM(direction='out' AND billsec=0),
                                       MAX(ts)
                                FROM calls GROUP BY channel ORDER BY channel;";
            using var r = cmd.ExecuteReader();
            var list = new List<CallStat>();
            int Get(int i) => r.IsDBNull(i) ? 0 : r.GetInt32(i);
            while (r.Read())
                list.Add(new CallStat(r.IsDBNull(0) ? "" : r.GetString(0), Get(1), Get(2), Get(3), Get(4), r.IsDBNull(5) ? null : r.GetString(5)));
            return list;
        }

        // --- Telegram notifications ---

        public static void EnqueueCallTg(long callId, IEnumerable<string> chatIds, string kind, bool wantTranscript)
        {
            using var db = Open();
            foreach (var chat in chatIds)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "INSERT INTO call_tg (call_id, chat_id, kind, want_transcript) VALUES ($c, $chat, $k, $w);";
                cmd.Parameters.AddWithValue("$c", callId);
                cmd.Parameters.AddWithValue("$chat", chat);
                cmd.Parameters.AddWithValue("$k", kind);
                cmd.Parameters.AddWithValue("$w", wantTranscript ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
        }

        private static List<CallTgItem> ReadTg(SqliteCommand cmd)
        {
            var list = new List<CallTgItem>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new CallTgItem
                {
                    Id = r.GetInt64(0),
                    CallId = r.GetInt64(1),
                    ChatId = r.GetString(2),
                    Kind = r.GetString(3),
                    WantTranscript = r.GetInt64(4) != 0,
                    MessageId = r.GetInt64(5),
                    Attempts = r.GetInt32(6)
                });
            return list;
        }

        private const string TgColumns = "t.id, t.call_id, t.chat_id, t.kind, t.want_transcript, t.message_id, t.attempts";

        // Notifications not sent yet.
        public static List<CallTgItem> PendingCallTg(int limit)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"SELECT {TgColumns} FROM call_tg t WHERE t.state = 'pending' ORDER BY t.id LIMIT $l;";
            cmd.Parameters.AddWithValue("$l", limit);
            return ReadTg(cmd);
        }

        // Sent with the hourglass whose transcription has finished (done or failed):
        // time to add it to the message.
        public static List<CallTgItem> CallTgReadyForEdit(int limit)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = $@"SELECT {TgColumns} FROM call_tg t JOIN calls c ON c.id = t.call_id
                                 WHERE t.state = 'sent' AND c.tr_state IN ('done', 'error') ORDER BY t.id LIMIT $l;";
            cmd.Parameters.AddWithValue("$l", limit);
            return ReadTg(cmd);
        }

        public static void MarkCallTgSent(long id, long messageId, bool final)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE call_tg SET state = $s, message_id = $m, last_error = NULL WHERE id = $id;";
            cmd.Parameters.AddWithValue("$s", final ? "final" : "sent");
            cmd.Parameters.AddWithValue("$m", messageId);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        public static void MarkCallTgFinal(long id)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE call_tg SET state = 'final' WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        public static void MarkCallTgFailed(long id, string error)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE call_tg SET attempts = attempts + 1, last_error = $e WHERE id = $id;";
            cmd.Parameters.AddWithValue("$e", error);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        public static int CountPendingCallTg()
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM call_tg WHERE state <> 'final';";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }
}
