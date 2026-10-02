using System.Diagnostics;
using System.Net;

namespace ghostline
{
    // Записи разговоров живут на АТС (/var/spool/asterisk/monitor, в её ночном бэкапе)
    // и отдаются по HTTP только адресу ghostline. Здесь не копируются — берутся по требованию.
    internal static class Recordings
    {
        private static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(2) };

        // Имена файлов содержат '+' (external-11-+7926...), поэтому каждый сегмент экранируется.
        private static string Url(string recPath) =>
            Program.settings.calls.recordingsUrl.TrimEnd('/') + "/" +
            string.Join("/", recPath.Split('/').Select(Uri.EscapeDataString));

        // Размер файла; 0 — файла нет; -1 — АТС недоступна (узнаем позже).
        public static async Task<long> HeadSize(string recPath)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, Url(recPath));
                using var resp = await http.SendAsync(req);
                if (resp.StatusCode == HttpStatusCode.NotFound) return 0;
                if (!resp.IsSuccessStatusCode) return -1;
                return resp.Content.Headers.ContentLength ?? -1;
            }
            catch
            {
                return -1;
            }
        }

        // null — файла нет. Недоступность АТС — исключение, пусть вызывающий повторит позже.
        public static async Task<byte[]> Fetch(string recPath)
        {
            using var resp = await http.GetAsync(Url(recPath));
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync();
        }

        // Голос собеседника приходит из GSM-модуля заметно тише, чем свой с SIP-телефона.
        // Для прослушивания, Telegram и расшифровки громкость выравнивается (dynaudnorm
        // подтягивает тихие места, не трогая громкие). Есть дорожки по сторонам —
        // каждая выравнивается отдельно и сводится заново: так уровни ровные.
        // Оригиналы на АТС не меняются. null — ffmpeg не справился.
        public static async Task<byte[]> Balanced(byte[] mixed, byte[] rx = null, byte[] tx = null)
        {
            const string norm = "dynaudnorm=f=150:g=15:p=0.9";
            bool legs = rx?.Length > Store.MinRecordingBytes && tx?.Length > Store.MinRecordingBytes;
            return legs
                ? await Ffmpeg(new[] { rx, tx }, "wav",
                    "-filter_complex", $"[0]{norm}[a];[1]{norm}[b];[a][b]amix=inputs=2:duration=longest:normalize=0",
                    "-ac", "1", "-ar", "8000", "-c:a", "pcm_s16le")
                : await Ffmpeg(new[] { mixed }, "wav", "-af", norm, "-ac", "1", "-ar", "8000", "-c:a", "pcm_s16le");
        }

        // Звонок → выровненная запись с кэшем: браузер при перемотке делает несколько
        // Range-запросов подряд, ffmpeg на каждый не нужен.
        private static readonly Dictionary<long, byte[]> cache = new();
        private static readonly Queue<long> cacheOrder = new();

        public static async Task<byte[]> BalancedForCall(CallRecord call)
        {
            lock (cache)
                if (cache.TryGetValue(call.Id, out var hit)) return hit;

            byte[] mixed = await Fetch(call.RecPath);
            if (mixed == null) return null;
            byte[] rx = await Fetch(Program.LegPath(call.RecPath, "r"));
            byte[] tx = await Fetch(Program.LegPath(call.RecPath, "t"));
            byte[] result = await Balanced(mixed, rx, tx) ?? mixed;

            lock (cache)
            {
                if (!cache.ContainsKey(call.Id))
                {
                    cache[call.Id] = result;
                    cacheOrder.Enqueue(call.Id);
                    while (cacheOrder.Count > 20) cache.Remove(cacheOrder.Dequeue());
                }
            }
            return result;
        }

        // wav 8 кГц → ogg/opus: только такой формат Telegram показывает как голосовое.
        // null — ffmpeg не справился (тогда шлём исходный wav аудиофайлом).
        public static Task<byte[]> ToOggOpus(byte[] wav) =>
            Ffmpeg(new[] { wav }, "ogg", "-ac", "1", "-c:a", "libopus", "-b:a", "24k");

        // Подавление эха в моей дорожке (half-duplex gate). Эхо — голос собеседника,
        // вернувшийся из динамика моего телефона в его микрофон: оно тише оригинала и звучит
        // одновременно с ним или с задержкой до ~300 мс. Кадр 20 мс моей дорожки глушится,
        // если собеседник в окне [кадр − 300 мс, кадр] громкий, а я — заметно тише его.
        // Когда говорю я сам (в том числе поверх собеседника), мой голос громче эха — кадр
        // остаётся. Только для расшифровки; записи на АТС не меняются.
        public static byte[] EchoGate(byte[] me, byte[] them)
        {
            if (!TryPcm16(me, out int meOff, out int meLen) || !TryPcm16(them, out int thOff, out int thLen))
                return me;
            const int Frame = 160;          // 20 мс при 8 кГц
            const int DelayFrames = 15;     // 300 мс
            const double FarActive = 400;   // собеседник говорит (RMS ≈ −38 dBFS)
            // Мой голос с SIP-телефона заметно громче голоса из GSM-модуля, эхо — тише оригинала:
            // всё, что в моей дорожке тише собеседника, — эхо. На 0.6 эхо «Выберите?» проскакивало.
            const double EchoRatio = 1.0;

            double Rms(byte[] b, int off, int len, int frame)
            {
                int start = off + frame * Frame * 2, end = Math.Min(off + len, start + Frame * 2);
                if (start >= end) return 0;
                double sum = 0; int n = 0;
                for (int i = start; i + 1 < end; i += 2, n++)
                {
                    short v = (short)(b[i] | (b[i + 1] << 8));
                    sum += (double)v * v;
                }
                return n > 0 ? Math.Sqrt(sum / n) : 0;
            }

            int frames = meLen / (Frame * 2);
            int thFrames = thLen / (Frame * 2);
            var far = new double[thFrames];
            for (int f = 0; f < thFrames; f++) far[f] = Rms(them, thOff, thLen, f);

            // Мягкое приглушение, а не обнуление: жёсткие нули рубили мой голос на куски по 20 мс,
            // и Whisper выдавал мои реплики строчными, без пунктуации, с обрывками слов.
            // Решение по кадрам, после «моего» кадра дорожка ещё Hangover кадров открыта (не
            // обрезать окончания слов), усиление меняется плавно внутри кадра.
            const double Attenuate = 0.03;  // ≈ −30 дБ
            const int Hangover = 10;        // 200 мс
            var gain = new double[frames];
            int open = 0;
            for (int f = 0; f < frames; f++)
            {
                double farMax = 0;
                for (int d = Math.Max(0, f - DelayFrames); d <= f && d < thFrames; d++)
                    farMax = Math.Max(farMax, far[d]);
                bool echo = farMax >= FarActive && Rms(me, meOff, meLen, f) < farMax * EchoRatio;
                if (!echo) open = Hangover;
                gain[f] = echo && open-- <= 0 ? Attenuate : 1.0;
            }

            byte[] result = (byte[])me.Clone();
            int muted = 0;
            double prev = 1.0;
            for (int f = 0; f < frames; f++)
            {
                if (gain[f] < 1.0) muted++;
                int baseIdx = meOff + f * Frame * 2;
                for (int i = 0; i < Frame; i++)
                {
                    double g = prev + (gain[f] - prev) * (i + 1) / Frame;   // плавный переход
                    int idx = baseIdx + i * 2;
                    short v = (short)(result[idx] | (result[idx + 1] << 8));
                    short o = (short)Math.Clamp(Math.Round(v * g), short.MinValue, short.MaxValue);
                    result[idx] = (byte)(o & 0xFF);
                    result[idx + 1] = (byte)((o >> 8) & 0xFF);
                }
                prev = gain[f];
            }
            Console.WriteLine($"EchoGate: muted {muted} of {frames} frames ({muted * 20 / 1000.0:F1} s)");
            return result;
        }

        // WAV → смещение и длина блока data, если это PCM 16 бит моно (так пишет Asterisk).
        private static bool TryPcm16(byte[] wav, out int dataOffset, out int dataLength)
        {
            dataOffset = dataLength = 0;
            if (wav == null || wav.Length < 44 || System.Text.Encoding.ASCII.GetString(wav, 0, 4) != "RIFF") return false;
            int pos = 12;
            short format = 0, channels = 0, bits = 0;
            while (pos + 8 <= wav.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
                int size = BitConverter.ToInt32(wav, pos + 4);
                if (id == "fmt ")
                {
                    format = BitConverter.ToInt16(wav, pos + 8);
                    channels = BitConverter.ToInt16(wav, pos + 10);
                    bits = BitConverter.ToInt16(wav, pos + 22);
                }
                else if (id == "data")
                {
                    dataOffset = pos + 8;
                    dataLength = Math.Min(size, wav.Length - dataOffset);
                    return format == 1 && channels == 1 && bits == 16;
                }
                pos += 8 + size + (size & 1);
            }
            return false;
        }

        // MP3 — для чатов, где голосовые запрещены: обычный аудиофайл с плеером.
        public static Task<byte[]> ToMp3(byte[] wav) =>
            Ffmpeg(new[] { wav }, "mp3", "-ac", "1", "-c:a", "libmp3lame", "-b:a", "32k");

        // Входы и выход — через временные файлы (wav с заголовком ffmpeg читает из файла надёжнее, чем из pipe).
        private static async Task<byte[]> Ffmpeg(byte[][] inputs, string outExt, params string[] args)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "ghostline-" + Guid.NewGuid().ToString("N"));
            var inFiles = inputs.Select((_, i) => $"{tmp}-in{i}.wav").ToArray();
            string outFile = tmp + "." + outExt;
            try
            {
                for (int i = 0; i < inputs.Length; i++)
                    await File.WriteAllBytesAsync(inFiles[i], inputs[i]);

                var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
                foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y" }) psi.ArgumentList.Add(a);
                foreach (var f in inFiles) { psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(f); }
                foreach (var a in args) psi.ArgumentList.Add(a);
                psi.ArgumentList.Add(outFile);

                using var p = Process.Start(psi);
                string err = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (p.ExitCode != 0 || !File.Exists(outFile))
                {
                    Console.WriteLine("ffmpeg failed: " + err.Trim());
                    return null;
                }
                return await File.ReadAllBytesAsync(outFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine("ffmpeg failed: " + ex.Message);
                return null;
            }
            finally
            {
                foreach (var f in inFiles.Append(outFile))
                    try { File.Delete(f); } catch { }
            }
        }
    }
}
