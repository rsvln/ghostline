using System.Diagnostics;
using System.Net;

namespace ghostline
{
    // Call recordings live on the PBX (/var/spool/asterisk/monitor, covered by its nightly backup)
    // and are served over HTTP only to the ghostline address. They are not copied here, only fetched on demand.
    internal static class Recordings
    {
        private static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(2) };

        // File names contain '+' (external-11-+7926...), so every path segment is escaped.
        private static string Url(string recPath) =>
            Program.settings.calls.recordingsUrl.TrimEnd('/') + "/" +
            string.Join("/", recPath.Split('/').Select(Uri.EscapeDataString));

        // File size; 0: no file; -1: PBX unavailable (find out later).
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

        // null: no file. PBX unavailable is an exception, the caller retries later.
        public static async Task<byte[]> Fetch(string recPath)
        {
            using var resp = await http.GetAsync(Url(recPath));
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync();
        }

        // The remote side comes from the GSM module much quieter than our own SIP phone.
        // For playback, Telegram and transcription the loudness is balanced (dynaudnorm
        // raises quiet parts without touching loud ones). With per-side legs each leg is
        // balanced separately and mixed again, which gives even levels.
        // The originals on the PBX are not changed. null: ffmpeg failed.
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

        // Call to balanced recording, cached: while seeking the browser sends several Range
        // requests in a row, and ffmpeg is not needed for each of them.
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

        // 8 kHz wav to ogg/opus: the only format Telegram shows as a voice message.
        // null: ffmpeg failed (the original wav is then sent as an audio file).
        public static Task<byte[]> ToOggOpus(byte[] wav) =>
            Ffmpeg(new[] { wav }, "ogg", "-ac", "1", "-c:a", "libopus", "-b:a", "24k");

        // Echo suppression in my leg (half-duplex gate). Echo is the other side's voice coming
        // back from my phone's speaker into its microphone: it is quieter than the original and
        // comes at the same time or up to ~300 ms later. A 20 ms frame of my leg is muted when
        // the other side is loud within [frame - 300 ms, frame] and I am clearly quieter.
        // When I speak myself (including over the other side) my voice is louder than the echo
        // and the frame stays. Only for transcription; recordings on the PBX are not changed.
        public static byte[] EchoGate(byte[] me, byte[] them)
        {
            if (!TryPcm16(me, out int meOff, out int meLen) || !TryPcm16(them, out int thOff, out int thLen))
                return me;
            const int Frame = 160;          // 20 ms at 8 kHz
            const int DelayFrames = 15;     // 300 ms
            const double FarActive = 400;   // the other side speaks (RMS about -38 dBFS)
            // My voice from the SIP phone is clearly louder than the voice from the GSM module, echo is
            // quieter than the original: anything in my leg quieter than the other side is echo. At 0.6 echo slipped through.
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

            // Soft attenuation, not zeroing: hard zeros chopped my voice into 20 ms pieces and
            // Whisper returned my lines in lower case, without punctuation, with broken words.
            // Decided per frame; after a frame of mine the leg stays open for Hangover frames (so
            // word endings are not cut), the gain changes smoothly within a frame.
            const double Attenuate = 0.03;  // about -30 dB
            const int Hangover = 10;        // 200 ms
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
                    double g = prev + (gain[f] - prev) * (i + 1) / Frame;   // smooth transition
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

        // Pieces of a leg with sound (start and end in seconds), transcribed separately.
        // Sent whole, a long leg makes Whisper get stuck: on 2026-10-02, after two minutes of a repeated
        // "your call is first in the queue", the rest of the call (with the operator) came back as a single
        // subtitle-credit hallucination, and the words of one phrase ended up three minutes apart.
        // Sound is 20 ms frames above the threshold; gaps shorter than MergeGap are joined, pieces up to
        // MaxChunk (the Whisper window is 30 s) are built from adjacent phrases with gaps up to ChunkGap.
        // null: not 16-bit mono PCM, the leg is then sent whole.
        public static List<(double s, double e)> SoundChunks(byte[] wav)
        {
            if (!TryPcm16(wav, out int off, out int len, out int rate)) return null;
            const double Threshold = 200;   // about -44 dBFS: above line noise, below a quiet speaker
            const double FrameSec = 0.02, MergeGap = 0.5, MinSound = 0.3, ChunkGap = 2.0, MaxChunk = 28.0, Pad = 0.3;
            int frame = (int)(rate * FrameSec);
            int frames = len / (frame * 2);
            double total = len / 2.0 / rate;

            // Stretches with sound
            var sounds = new List<(double s, double e)>();
            int start = -1;
            for (int f = 0; f <= frames; f++)
            {
                bool active = false;
                if (f < frames)
                {
                    double sum = 0;
                    int b = off + f * frame * 2;
                    for (int i = 0; i < frame; i++)
                    {
                        short v = (short)(wav[b + i * 2] | (wav[b + i * 2 + 1] << 8));
                        sum += (double)v * v;
                    }
                    active = Math.Sqrt(sum / frame) >= Threshold;
                }
                if (active && start < 0) start = f;
                else if (!active && start >= 0)
                {
                    double s = start * FrameSec, e = f * FrameSec;
                    if (sounds.Count > 0 && s - sounds[^1].e < MergeGap) sounds[^1] = (sounds[^1].s, e);
                    else sounds.Add((s, e));
                    start = -1;
                }
            }
            sounds.RemoveAll(x => x.e - x.s < MinSound);

            // Stretches to pieces: a long sound (hold music) is cut at MaxChunk
            var chunks = new List<(double s, double e)>();
            foreach (var (s0, e0) in sounds)
            {
                for (double s = s0; s < e0; s += MaxChunk)
                {
                    double e = Math.Min(e0, s + MaxChunk);
                    if (chunks.Count > 0 && s - chunks[^1].e <= ChunkGap && e - chunks[^1].s <= MaxChunk)
                        chunks[^1] = (chunks[^1].s, e);
                    else
                        chunks.Add((s, e));
                }
            }
            return chunks.Select(c => (Math.Max(0, c.s - Pad), Math.Min(total, c.e + Pad))).ToList();
        }

        // Piece of a WAV from s to e seconds: the same header with other sizes.
        public static byte[] Slice(byte[] wav, double s, double e)
        {
            if (!TryPcm16(wav, out int off, out int len, out int rate)) return null;
            int from = Math.Min(len, (int)(s * rate) * 2), to = Math.Min(len, (int)(e * rate) * 2);
            if (to <= from) return null;
            var result = new byte[off + to - from];
            Array.Copy(wav, result, off);
            Array.Copy(wav, off + from, result, off, to - from);
            BitConverter.GetBytes(result.Length - 8).CopyTo(result, 4);
            BitConverter.GetBytes(to - from).CopyTo(result, off - 4);
            return result;
        }

        private static bool TryPcm16(byte[] wav, out int dataOffset, out int dataLength) =>
            TryPcm16(wav, out dataOffset, out dataLength, out _);

        // WAV to the offset and length of the data chunk, if it is 16-bit mono PCM (as Asterisk writes).
        private static bool TryPcm16(byte[] wav, out int dataOffset, out int dataLength, out int rate)
        {
            dataOffset = dataLength = rate = 0;
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
                    rate = BitConverter.ToInt32(wav, pos + 12);
                    bits = BitConverter.ToInt16(wav, pos + 22);
                }
                else if (id == "data")
                {
                    dataOffset = pos + 8;
                    dataLength = Math.Min(size, wav.Length - dataOffset);
                    return format == 1 && channels == 1 && bits == 16 && rate > 0;
                }
                pos += 8 + size + (size & 1);
            }
            return false;
        }

        // MP3 for chats where voice messages are forbidden: a regular audio file with a player.
        public static Task<byte[]> ToMp3(byte[] wav) =>
            Ffmpeg(new[] { wav }, "mp3", "-ac", "1", "-c:a", "libmp3lame", "-b:a", "32k");

        // Inputs and output go through temp files (ffmpeg reads a wav with a header from a file more reliably than from a pipe).
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
