using System.Diagnostics;
using System.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ghostline
{
    // Реплика расшифровки: время от начала записи (с), сторона и текст.
    // who: "me" / "them" — если есть дорожки по сторонам; null — общая моно-запись.
    public record TranscriptSegment(double s, double e, string who, string t);

    // Расшифровка записей: очередь в БД (calls.tr_state = 'pending'), сервис —
    // OpenAI-совместимый /v1/audio/transcriptions (например, speaches).
    // Сервис или АТС недоступны — звонок остаётся в очереди, попытка не засчитывается.
    // Сервис ответил ошибкой — попытка засчитывается, после MaxAttempts звонок уходит в 'error'.
    //
    // Диалог. АТС пишет, кроме общей записи X.wav, ещё X-r.wav и X-t.wav — каждую сторону
    // отдельно (override [sub-record-check] на АТС, с 2026-09-27). Тогда дорожки
    // расшифровываются по отдельности и реплики сводятся по времени. Для старых звонков
    // дорожек нет — общая запись разбивается на реплики без указания стороны.
    internal partial class Program
    {
        private const int MaxTranscribeAttempts = 5;

        private class TranscribeHttpException(string message) : Exception(message);

        // requested = true — воркер очереди «по кнопке», false — фоновый (новые звонки, пересчёт).
        private static async Task TranscribeWorker(bool requested)
        {
            // Длинный разговор на CPU расшифровывается минуты — таймаут с запасом.
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
                        Store.SetTranscriptError(call.Id, "нет записи", final: true);
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
                        // В мою дорожку из динамика телефона попадает эхо собеседника — Whisper
                        // путается в нём и ставит мои слова не на то время. Глушим эхо до расшифровки.
                        if (rxWho == "me") rx = Recordings.EchoGate(rx, tx);
                        else tx = Recordings.EchoGate(tx, rx);
                        segs = (await Transcribe(http, rx, rxWho, call)).Concat(await Transcribe(http, tx, txWho, call))
                                                                  .OrderBy(x => x.s).ToList();
                    }
                    else
                    {
                        segs = await Transcribe(http, wav, null, call);
                    }
                    // Фильтр ещё раз после склейки: галлюцинация может прийти по частям
                    // («Редактор субтитров» + пауза + имя).
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
                    // Сервис расшифровки или АТС недоступны — ждём, звонок остаётся в очереди.
                    string err = DescribeException(ex);
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

        // MixMonitor запускается на канале, где сработала проверка записи: у входящего —
        // на GSM-канале звонящего, у исходящего — на канале нашего телефона.
        // r — что пришло ОТ этого канала, t — что ушло В него.
        private static (string rx, string tx) LegSpeakers(string direction) =>
            direction == "in" ? ("them", "me") : ("me", "them");

        // Дорожка режется по паузам, куски расшифровываются по отдельности (см. Recordings.SoundChunks),
        // время реплик сдвигается на начало куска.
        private static async Task<List<TranscriptSegment>> Transcribe(HttpClient http, byte[] wav, string who, CallRecord call)
        {
            var chunks = Recordings.SoundChunks(wav);
            // Тихий собеседник распознаётся хуже — выравниваем громкость и здесь.
            // Куски ищутся по исходной дорожке: после выравнивания шум линии тоже громкий.
            byte[] balanced = await Recordings.Balanced(wav) ?? wav;
            if (chunks == null)
                return await TranscribeChunk(http, balanced, who, call);

            var result = new List<TranscriptSegment>();
            foreach (var (s, e) in chunks)
            {
                byte[] piece = Recordings.Slice(balanced, s, e);
                if (piece == null) continue;
                foreach (var seg in await TranscribeChunk(http, piece, who, call))
                    if (!IsPromptEcho(seg.t, settings.calls.transcribe.prompt))
                        result.Add(seg with { s = Math.Round(seg.s + s, 1), e = Math.Round(seg.e + s, 1) });
            }
            Console.WriteLine($"Transcribe call {call.Id} {who ?? "mono"}: {chunks.Count} chunks, {chunks.Sum(c => c.e - c.s):F0}s of sound");
            return result;
        }

        private static async Task<List<TranscriptSegment>> TranscribeChunk(HttpClient http, byte[] wav, string who, CallRecord call)
        {
            var t = settings.calls.transcribe;
            // Подсказка: словарь из настроек + имя собеседника.
            string prompt = string.Join(". ", new[] { t.prompt, call.PeerName }.Where(x => !string.IsNullOrWhiteSpace(x)));
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
            // Время по словам: у отрезков Whisper начало ненадёжно — отрезок может начаться
            // с эха или шума задолго до слов (2026-09-28 «Мобильная связь», сказанная на 15 с,
            // пришла отрезком с 2,6 с и встала раньше вопроса автоответчика).
            form.Add(new StringContent("word"), "timestamp_granularities[]");
            form.Add(new StringContent("segment"), "timestamp_granularities[]");
            // Без VAD Whisper на тишине «слышит» титры и прочий мусор.
            form.Add(new StringContent("true"), "vad_filter");

            using var resp = await http.PostAsync(t.url, form);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new TranscribeHttpException($"HTTP {(int)resp.StatusCode}: {Trim(body, 200)}");

            var json = JObject.Parse(body);
            if (json["words"] is JArray words && words.Count > 0)
                return FromWords(words, who);

            var result = new List<TranscriptSegment>();
            foreach (var seg in json["segments"] ?? new JArray())
            {
                string text = DropHallucination((seg["text"]?.ToString() ?? "").Trim());
                if (text.Length == 0) continue;
                result.Add(new TranscriptSegment(Math.Round((double)seg["start"], 1),
                                                 Math.Round((double)seg["end"], 1), who, text));
            }
            return result;
        }

        // Слова → реплики: новая реплика после паузы больше WordGap. Начало реплики — начало
        // её первого слова, поэтому стороны сводятся по времени в правильном порядке.
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

        // Соседние реплики одной стороны с паузой меньше 2 с — одна реплика.
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

        // Текст по реплике на строку: для поиска, Telegram и выгрузки. withTime — с отметками [м:сс].
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

        // На тишине и шуме Whisper «слышит» титры из обучающих данных (YouTube-субтитры).
        // Два вида:
        // 1) подписи субтитров («Субтитры создавал DimaTorzok», «Редактор субтитров … Корректор …») —
        //    в разговоре их не бывает, вырезаются из любого места: Whisper приклеивает их к концу
        //    настоящей реплики (2026-10-01: «…До свидания. Субтитры создавал DimaTorzok»);
        // 2) обычные фразы («продолжение следует», «до новых встреч») — их могут сказать и всерьёз,
        //    поэтому выбрасывается только реплика, целиком из такой фразы.
        private static readonly System.Text.RegularExpressions.Regex SubtitleCredits = new(
            @"\s*\b(субтитры\s+(сделал[аи]?|создавал[аи]?|делал[аи]?|подогнал[аи]?|подготовил[аи]?)\s+[\w.\-«»""]+|редактор\s+субтитров.*?корректор\s+[\w.]+(\s*[\w.]+)?|редактор\s+субтитров\s+[\w.]+(\s*[\w.]+)?|dimatorzok)[\s,.;:!?—–-]*",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static readonly string[] Hallucinations =
        {
            "продолжение следует", "спасибо за просмотр", "подписывайтесь на канал", "до новых встреч"
        };

        // Только буквы и цифры через одиночный пробел, в нижнем регистре.
        private static string Norm(string s) =>
            string.Join(" ", new string(s.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray())
                             .Split(' ', StringSplitOptions.RemoveEmptyEntries));

        internal static string DropHallucination(string text)
        {
            text = SubtitleCredits.Replace(text, " ").Trim();
            if (!text.Any(char.IsLetterOrDigit)) return "";
            return Hallucinations.Any(h => Norm(text) == Norm(h)) ? "" : text;
        }

        // На коротком шуме (дыхание, стук, пока собеседник держит на удержании) Whisper с подсказкой
        // выдаёт слова из неё: 2026-10-02 «Финанский кодекс» дважды. Короткая реплика, все слова
        // которой совпадают со словами подсказки по первым четырём буквам, — выбрасывается.
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
