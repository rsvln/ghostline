using System.Diagnostics;
using System.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ghostline
{
    // A transcript line: time from the start of the recording (s), side and text.
    // who: "me" / "them" when there are per-side legs; null for an old mono recording.
    public record TranscriptSegment(double s, double e, string who, string t);

    // Transcription of recordings: the queue is in the DB (calls.tr_state = 'pending'), the service is
    // an OpenAI-compatible /v1/audio/transcriptions (for example speaches).
    // Service or PBX unavailable: the call stays queued and the attempt is not counted.
    // The service returned an error: the attempt counts, after MaxAttempts the call goes to 'error'.
    //
    // Dialog. Besides the mixed X.wav the PBX writes X-r.wav and X-t.wav, one per side
    // (override of [sub-record-check] on the PBX, since 2026-09-27). The legs are then
    // transcribed separately and the lines merged by time. Older calls have no legs:
    // the mixed recording is split into lines without a side.
    internal partial class Program
    {
        private const int MaxTranscribeAttempts = 5;

        private class TranscribeHttpException(string message) : Exception(message);

        // requested = true: the worker for the "Transcribe again" button; false: the background one (new calls).
        private static async Task TranscribeWorker(bool requested)
        {
            // A long call on CPU takes minutes to transcribe, so the timeout is generous.
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
            HealthStatus.MarkConfigured("transcribe");

            while (true)
            {
                CallRecord call = null;
                try
                {
                    call = Store.NextPendingTranscription(requested);
                    if (call == null)
                    {
                        await Task.Delay(5000);
                        continue;
                    }

                    byte[] wav = await Recordings.Fetch(call.RecPath);
                    if (wav == null || wav.Length <= Store.MinRecordingBytes)
                    {
                        Store.SetRecSize(call.Id, wav?.Length ?? 0);
                        Store.SetTranscriptError(call.Id, "no recording", final: true);
                        continue;
                    }

                    var sw = Stopwatch.StartNew();
                    byte[] rx = await Recordings.Fetch(LegPath(call.RecPath, "r"));
                    byte[] tx = await Recordings.Fetch(LegPath(call.RecPath, "t"));
                    bool legs = rx?.Length > Store.MinRecordingBytes && tx?.Length > Store.MinRecordingBytes;

                    List<TranscriptSegment> segs;
                    if (legs)
                    {
                        var (rxWho, txWho) = LegSpeakers(call.Direction);
                        // Echo of the other side from my phone's speaker gets into my leg; Whisper gets
                        // confused by it and puts my words at the wrong time. The echo is muted before transcription.
                        if (rxWho == "me") rx = Recordings.EchoGate(rx, tx);
                        else tx = Recordings.EchoGate(tx, rx);
                        segs = (await Transcribe(http, rx, rxWho, call)).Concat(await Transcribe(http, tx, txWho, call))
                                                                  .OrderBy(x => x.s).ToList();
                    }
                    else
                    {
                        segs = await Transcribe(http, wav, null, call);
                    }
                    // Filter again after merging: a hallucination can arrive in parts
                    // ("subtitle editor" + pause + a name).
                    segs = MergeAdjacent(segs).Select(x => x with { t = DropHallucination(x.t) })
                                              .Where(x => x.t.Length > 0).ToList();

                    string text = FormatDialog(segs, L10n.Tg);
                    Store.SetTranscript(call.Id, text, JsonConvert.SerializeObject(segs));
                    HealthStatus.MarkConnected("transcribe");
                    HealthStatus.MarkActivity("transcribe");
                    Console.WriteLine($"Transcribed call {call.Id} ({call.Billsec}s audio, {(legs ? "2 legs" : "mono")}) " +
                                      $"in {sw.Elapsed.TotalSeconds:F0}s, {segs.Count} lines");
                }
                catch (TranscribeHttpException ex)
                {
                    bool final = Store.GetTranscriptAttempts(call.Id) + 1 >= MaxTranscribeAttempts;
                    Store.SetTranscriptError(call.Id, ex.Message, final);
                    HealthStatus.MarkError("transcribe", ex.Message);
                    Console.WriteLine($"Transcribe call {call.Id} failed: {ex.Message}");
                    await Task.Delay(30000);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // Transcription service or PBX unavailable: wait, the call stays queued.
                    string err = DescribeException(ex);
                    HealthStatus.MarkDisconnected("transcribe");
                    HealthStatus.MarkError("transcribe", err);
                    Console.WriteLine($"Transcribe unavailable (call {call?.Id}): {err}, retrying in 60s");
                    await Task.Delay(60000);
                }
                catch (Exception ex)
                {
                    string err = DescribeException(ex);
                    HealthStatus.MarkError("transcribe", err);
                    Console.WriteLine($"Transcribe call {call?.Id} error: {err}");
                    if (call != null)
                        Store.SetTranscriptError(call.Id, err, Store.GetTranscriptAttempts(call.Id) + 1 >= MaxTranscribeAttempts);
                    await Task.Delay(30000);
                }
            }
        }

        // 2026/09/27/external-11-...wav → 2026/09/27/external-11-...-r.wav
        internal static string LegPath(string recPath, string leg) =>
            Path.ChangeExtension(recPath, null) + "-" + leg + Path.GetExtension(recPath);

        // MixMonitor runs on the channel where the recording check happened: for incoming calls the
        // caller's GSM channel, for outgoing calls the channel of our own phone.
        // r is what came FROM that channel, t is what went INTO it.
        private static (string rx, string tx) LegSpeakers(string direction) =>
            direction == "in" ? ("them", "me") : ("me", "them");

        // Each leg is cut at pauses and the pieces are transcribed separately (see Recordings.SoundChunks);
        // line times are shifted by the start of the piece.
        private static async Task<List<TranscriptSegment>> Transcribe(HttpClient http, byte[] wav, string who, CallRecord call)
        {
            var chunks = Recordings.SoundChunks(wav);
            // A quiet remote side is recognized worse, so the loudness is balanced here too.
            // Pieces are found on the original leg: after balancing, line noise is loud as well.
            byte[] balanced = await Recordings.Balanced(wav) ?? wav;
            if (chunks == null)
                return (await TranscribeChunk(http, balanced, who, call, true)).segs;

            var result = new List<TranscriptSegment>();
            foreach (var (s, e) in chunks)
            {
                byte[] piece = Recordings.Slice(balanced, s, e);
                if (piece == null) continue;
                foreach (var seg in await TranscribePiece(http, piece, who, call))
                    if (!IsPromptEcho(seg.t, settings.calls.transcribe.prompt))
                        result.Add(seg with { s = Math.Round(seg.s + s, 1), e = Math.Round(seg.e + s, 1) });
            }
            Console.WriteLine($"Transcribe call {call.Id} {who ?? "mono"}: {chunks.Count} chunks, {chunks.Sum(c => c.e - c.s):F0}s of sound");
            return result;
        }

        // The prompt helps with names and terms in long phrases, but Whisper bends short lines
        // towards it: on 2026-10-08 "hello" with the prompt became the bank name from the prompt
        // plus noise (word confidence 0.04-0.21), without the prompt it was right. When Whisper is
        // unsure with the prompt, the piece is transcribed again without it and the surer result wins.
        private const double PromptConfidence = 0.7;

        private static async Task<List<TranscriptSegment>> TranscribePiece(HttpClient http, byte[] wav, string who, CallRecord call)
        {
            var withPrompt = await TranscribeChunk(http, wav, who, call, true);
            if (!withPrompt.prompted || withPrompt.confidence >= PromptConfidence)
                return withPrompt.segs;
            var plain = await TranscribeChunk(http, wav, who, call, false);
            return plain.confidence > withPrompt.confidence ? plain.segs : withPrompt.segs;
        }

        // confidence: mean word probability (1 when there are no words).
        private static async Task<(List<TranscriptSegment> segs, double confidence, bool prompted)> TranscribeChunk(
            HttpClient http, byte[] wav, string who, CallRecord call, bool usePrompt)
        {
            var t = settings.calls.transcribe;
            // Prompt: the vocabulary from the settings plus the contact name of the other side.
            string prompt = usePrompt
                ? string.Join(". ", new[] { t.prompt, call.PeerName }.Where(x => !string.IsNullOrWhiteSpace(x)))
                : "";
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "call.wav");
            form.Add(new StringContent(t.model ?? ""), "model");
            if (!string.IsNullOrEmpty(t.language))
                form.Add(new StringContent(t.language), "language");
            form.Add(new StringContent("verbose_json"), "response_format");
            if (prompt.Length > 0)
                form.Add(new StringContent(prompt), "prompt");
            // Word timestamps: Whisper segment starts are unreliable, a segment can start with echo
            // or noise long before the words (2026-09-28: a phrase said at 15 s came as a segment
            // starting at 2.6 s and was placed before the answering machine's question).
            form.Add(new StringContent("word"), "timestamp_granularities[]");
            form.Add(new StringContent("segment"), "timestamp_granularities[]");
            // Without VAD Whisper "hears" subtitle credits and other junk in silence.
            form.Add(new StringContent("true"), "vad_filter");

            using var resp = await http.PostAsync(t.url, form);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new TranscribeHttpException($"HTTP {(int)resp.StatusCode}: {Trim(body, 200)}");

            var json = JObject.Parse(body);
            bool prompted = prompt.Length > 0;
            if (json["words"] is JArray words && words.Count > 0)
                return (FromWords(words, who), words.Average(w => (double?)w["probability"] ?? 1.0), prompted);

            var result = new List<TranscriptSegment>();
            foreach (var seg in json["segments"] ?? new JArray())
            {
                string text = DropHallucination((seg["text"]?.ToString() ?? "").Trim());
                if (text.Length == 0) continue;
                result.Add(new TranscriptSegment(Math.Round((double)seg["start"], 1),
                                                 Math.Round((double)seg["end"], 1), who, text));
            }
            return (result, 1.0, prompted);
        }

        // Words to lines: a new line after a pause longer than WordGap. A line starts at its first
        // word, so the sides are merged by time in the right order.
        private const double WordGap = 1.0;

        private static List<TranscriptSegment> FromWords(JArray words, string who)
        {
            var result = new List<TranscriptSegment>();
            var text = new System.Text.StringBuilder();
            double start = 0, end = 0;

            void Flush()
            {
                string t = DropHallucination(text.ToString().Trim());
                if (t.Length > 0)
                    result.Add(new TranscriptSegment(Math.Round(start, 1), Math.Round(end, 1), who, t));
                text.Clear();
            }

            foreach (var w in words)
            {
                double ws = (double)w["start"], we = (double)w["end"];
                if (text.Length > 0 && ws - end > WordGap)
                    Flush();
                if (text.Length == 0)
                    start = ws;
                text.Append(w["word"]?.ToString());
                end = we;
            }
            Flush();
            return result;
        }

        // Adjacent lines of one side with a pause under 2 s are one line.
        private static List<TranscriptSegment> MergeAdjacent(List<TranscriptSegment> segs)
        {
            var merged = new List<TranscriptSegment>();
            foreach (var s in segs)
            {
                var last = merged.LastOrDefault();
                if (last != null && last.who == s.who && s.s - last.e < 2.0)
                    merged[^1] = last with { e = Math.Max(last.e, s.e), t = last.t + " " + s.t };
                else
                    merged.Add(s);
            }
            return merged;
        }

        // One line per utterance: for search, Telegram and export. withTime adds [m:ss] marks.
        internal static string FormatDialog(List<TranscriptSegment> segs, Strings l, bool withTime = false, string prefix = "tg.call")
        {
            string me = l.T(prefix + ".me"), them = l.T(prefix + ".them");
            return string.Join("\n", segs.Select(s =>
                (withTime ? $"[{(int)s.s / 60}:{(int)s.s % 60:D2}] " : "") + s.who switch
                {
                    "me" => me + ": " + s.t,
                    "them" => them + ": " + s.t,
                    _ => "— " + s.t
                }));
        }

        internal static List<TranscriptSegment> Segments(CallRecord c)
        {
            if (string.IsNullOrEmpty(c.TrSegments)) return new();
            try { return JsonConvert.DeserializeObject<List<TranscriptSegment>>(c.TrSegments) ?? new(); }
            catch { return new(); }
        }

        // On silence and noise Whisper "hears" credits from its training data (YouTube subtitles).
        // Two kinds:
        // 1) subtitle credits ("subtitles by ...", "subtitle editor ... proofreader ...") never occur
        //    in a call and are cut from anywhere: Whisper glues them to the end of a real
        //    line (2026-10-01: a goodbye followed by such a credit);
        // 2) ordinary phrases ("to be continued", "see you next time") can be said for real,
        //    so only a line that consists entirely of such a phrase is dropped.
        private static readonly System.Text.RegularExpressions.Regex SubtitleCredits = new(
            @"\s*\b(субтитры\s+(сделал[аи]?|создавал[аи]?|делал[аи]?|подогнал[аи]?|подготовил[аи]?)\s+[\w.\-«»""]+|редактор\s+субтитров.*?корректор\s+[\w.]+(\s*[\w.]+)?|редактор\s+субтитров\s+[\w.]+(\s*[\w.]+)?|dimatorzok)[\s,.;:!?—–-]*",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static readonly string[] Hallucinations =
        {
            "продолжение следует", "спасибо за просмотр", "подписывайтесь на канал", "до новых встреч"
        };

        // Only letters and digits, single spaces, lower case.
        private static string Norm(string s) =>
            string.Join(" ", new string(s.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray())
                             .Split(' ', StringSplitOptions.RemoveEmptyEntries));

        internal static string DropHallucination(string text)
        {
            text = SubtitleCredits.Replace(text, " ").Trim();
            if (!text.Any(char.IsLetterOrDigit)) return "";
            return Hallucinations.Any(h => Norm(text) == Norm(h)) ? "" : text;
        }

        // On short noise (breathing, knocks, while the other side keeps you on hold) Whisper with a prompt
        // outputs words from it: twice on 2026-10-02. A short line whose words all match prompt words
        // by their first four letters is dropped.
        private static bool IsPromptEcho(string text, string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return false;
            string Stem(string w) => w.Length > 4 ? w[..4] : w;
            var words = Norm(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).ToList();
            if (words.Count == 0 || words.Count > 3) return false;
            var stems = Norm(prompt).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Stem).ToHashSet();
            return words.All(w => stems.Contains(Stem(w)));
        }

        private static string Trim(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");
    }
}
