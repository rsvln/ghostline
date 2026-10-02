using Microsoft.Data.Sqlite;

namespace ghostline
{
    public class SmsRecord
    {
        public long Id { get; set; }
        public string Ts { get; set; }
        public string Direction { get; set; }   // "in" / "out"
        public string Gateway { get; set; }      // "y" / "g" / "q"
        public string Channel { get; set; }
        public string Peer { get; set; }         // номер отправителя (in) или получателя (out)
        public string Note { get; set; }         // контакт (in) / источник отправки (out)
        public string Content { get; set; }
    }

    public class TgQueueItem
    {
        public long Id { get; set; }
        public string ChatId { get; set; }
        public string Text { get; set; }
        public int Attempts { get; set; }
    }

    public class SmsOutQueueItem
    {
        public long Id { get; set; }
        public string ChannelName { get; set; }
        public string Number { get; set; }
        public string Text { get; set; }
        public string SourceLabel { get; set; }
        public int Attempts { get; set; }
    }

    public class ChannelStat
    {
        public string Channel { get; set; }
        public int In { get; set; }
        public int Out { get; set; }
    }

    // Хранилище ghostline: один SQLite-файл рядом с конфигом. Здесь — подключение,
    // история SMS и очереди отправки; звонки — в Store.Calls.cs.
    public static partial class Store
    {
        private static string _dbPath;

        public static void Init(string dbPath)
        {
            _dbPath = dbPath;
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());

            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS sms (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts TEXT NOT NULL,
                    direction TEXT NOT NULL,
                    gateway TEXT NOT NULL,
                    channel TEXT NOT NULL,
                    peer TEXT NOT NULL,
                    note TEXT,
                    content TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_sms_ts ON sms(ts);

                CREATE TABLE IF NOT EXISTS tg_queue (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    chat_id TEXT NOT NULL,
                    text TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0,
                    last_error TEXT
                );

                CREATE TABLE IF NOT EXISTS sms_out_queue (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    channel_name TEXT NOT NULL,
                    number TEXT NOT NULL,
                    text TEXT NOT NULL,
                    source_label TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0,
                    last_error TEXT
                );";
            cmd.ExecuteNonQuery();

            MigrateCalls(db);
        }

        internal static SqliteConnection Open()
        {
            var db = new SqliteConnection("Data Source=" + _dbPath);
            db.Open();
            return db;
        }

        public static void Insert(SmsRecord rec)
        {
            try
            {
                using var db = Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"INSERT INTO sms (ts, direction, gateway, channel, peer, note, content)
                                     VALUES ($ts, $direction, $gateway, $channel, $peer, $note, $content);";
                cmd.Parameters.AddWithValue("$ts", rec.Ts);
                cmd.Parameters.AddWithValue("$direction", rec.Direction);
                cmd.Parameters.AddWithValue("$gateway", rec.Gateway);
                cmd.Parameters.AddWithValue("$channel", rec.Channel);
                cmd.Parameters.AddWithValue("$peer", rec.Peer);
                cmd.Parameters.AddWithValue("$note", (object)rec.Note ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$content", rec.Content);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Store insert failed: " + ex.Message);
            }
        }

        public static List<SmsRecord> Query(int limit, string? direction, string? channel, string? text)
        {
            var result = new List<SmsRecord>();
            try
            {
                using var db = Open();
                using var cmd = db.CreateCommand();

                var where = new List<string>();
                if (!string.IsNullOrEmpty(direction)) where.Add("direction = $direction");
                if (!string.IsNullOrEmpty(channel)) where.Add("channel = $channel");
                if (!string.IsNullOrEmpty(text)) where.Add("(content LIKE $text OR peer LIKE $text OR note LIKE $text)");
                string whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

                cmd.CommandText = $"SELECT id, ts, direction, gateway, channel, peer, note, content FROM sms {whereSql} ORDER BY id DESC LIMIT $limit;";
                if (!string.IsNullOrEmpty(direction)) cmd.Parameters.AddWithValue("$direction", direction);
                if (!string.IsNullOrEmpty(channel)) cmd.Parameters.AddWithValue("$channel", channel);
                if (!string.IsNullOrEmpty(text)) cmd.Parameters.AddWithValue("$text", "%" + text + "%");
                cmd.Parameters.AddWithValue("$limit", limit);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new SmsRecord
                    {
                        Id = reader.GetInt64(0),
                        Ts = reader.GetString(1),
                        Direction = reader.GetString(2),
                        Gateway = reader.GetString(3),
                        Channel = reader.GetString(4),
                        Peer = reader.GetString(5),
                        Note = reader.IsDBNull(6) ? null : reader.GetString(6),
                        Content = reader.GetString(7)
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Store query failed: " + ex.Message);
            }
            return result;
        }

        // --- Персистентная очередь отправки в Telegram ---

        public static void EnqueueTelegramMessage(string chatId, string text)
        {
            try
            {
                using var db = Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"INSERT INTO tg_queue (chat_id, text, created_at) VALUES ($chatId, $text, $createdAt);";
                cmd.Parameters.AddWithValue("$chatId", chatId);
                cmd.Parameters.AddWithValue("$text", text);
                cmd.Parameters.AddWithValue("$createdAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Store enqueue failed: " + ex.Message);
            }
        }

        public static List<TgQueueItem> GetPendingTelegramMessages(int limit)
        {
            var result = new List<TgQueueItem>();
            try
            {
                using var db = Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT id, chat_id, text, attempts FROM tg_queue ORDER BY id ASC LIMIT $limit;";
                cmd.Parameters.AddWithValue("$limit", limit);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new TgQueueItem
                    {
                        Id = reader.GetInt64(0),
                        ChatId = reader.GetString(1),
                        Text = reader.GetString(2),
                        Attempts = reader.GetInt32(3)
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Store queue read failed: " + ex.Message);
            }
            return result;
        }

        public static void MarkTelegramMessageFailed(long id, string error)
        {
            try
            {
                using var db = Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "UPDATE tg_queue SET attempts = attempts + 1, last_error = $error WHERE id = $id;";
                cmd.Parameters.AddWithValue("$error", error);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Store queue update failed: " + ex.Message);
            }
        }

        public static void DeleteTelegramMessage(long id)
        {
            try
            {
                using var db = Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "DELETE FROM tg_queue WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Store queue delete failed: " + ex.Message);
            }
        }

        // --- Персистентная очередь отправки исходящих SMS (yeastar/goip/quectel) ---

        public static void EnqueueOutgoingSms(string channelName, string number, string text, string sourceLabel)
        {
            try
            {
                using var db = Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"INSERT INTO sms_out_queue (channel_name, number, text, source_label, created_at)
                                     VALUES ($channelName, $number, $text, $sourceLabel, $createdAt);";
                cmd.Parameters.AddWithValue("$channelName", channelName);
                cmd.Parameters.AddWithValue("$number", number);
                cmd.Parameters.AddWithValue("$text", text);
                cmd.Parameters.AddWithValue("$sourceLabel", sourceLabel);
                cmd.Parameters.AddWithValue("$createdAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Store sms-queue enqueue failed: " + ex.Message);
            }
        }

        public static List<SmsOutQueueItem> GetPendingOutgoingSms(int limit)
        {
            var result = new List<SmsOutQueueItem>();
            try
            {
                using var db = Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT id, channel_name, number, text, source_label, attempts FROM sms_out_queue ORDER BY id ASC LIMIT $limit;";
                cmd.Parameters.AddWithValue("$limit", limit);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new SmsOutQueueItem
                    {
                        Id = reader.GetInt64(0),
                        ChannelName = reader.GetString(1),
                        Number = reader.GetString(2),
                        Text = reader.GetString(3),
                        SourceLabel = reader.GetString(4),
                        Attempts = reader.GetInt32(5)
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Store sms-queue read failed: " + ex.Message);
            }
            return result;
        }

        public static void MarkOutgoingSmsFailed(long id, string error)
        {
            try
            {
                using var db = Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "UPDATE sms_out_queue SET attempts = attempts + 1, last_error = $error WHERE id = $id;";
                cmd.Parameters.AddWithValue("$error", error);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Store sms-queue update failed: " + ex.Message);
            }
        }

        public static void DeleteOutgoingSms(long id)
        {
            try
            {
                using var db = Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "DELETE FROM sms_out_queue WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Store sms-queue delete failed: " + ex.Message);
            }
        }

        // --- Статистика для вкладки Status ---

        public static List<ChannelStat> GetStats()
        {
            var result = new Dictionary<string, ChannelStat>();
            try
            {
                using var db = Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT channel, direction, COUNT(*) FROM sms GROUP BY channel, direction;";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string channel = reader.GetString(0);
                    string direction = reader.GetString(1);
                    int count = reader.GetInt32(2);

                    if (!result.TryGetValue(channel, out var stat))
                    {
                        stat = new ChannelStat { Channel = channel };
                        result[channel] = stat;
                    }
                    if (direction == "in") stat.In = count;
                    else if (direction == "out") stat.Out = count;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Store stats query failed: " + ex.Message);
            }
            return result.Values.ToList();
        }

        public static int CountPendingTelegramMessages()
        {
            try
            {
                using var db = Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM tg_queue;";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                Console.WriteLine("Store tg_queue count failed: " + ex.Message);
                return 0;
            }
        }

        public static int CountPendingOutgoingSms()
        {
            try
            {
                using var db = Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM sms_out_queue;";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                Console.WriteLine("Store sms_out_queue count failed: " + ex.Message);
                return 0;
            }
        }
    }
}
