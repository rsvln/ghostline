using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ghostline
{
    // Call transcript as PDF, for e-mail and printing. Looks like the Calls tab:
    // lines as bubbles, mine blue, the other side's grey, with the side and time above each.
    // Light theme (for printing); the IBM Plex Sans font with Cyrillic is embedded in the file.
    internal static class TranscriptPdf
    {
        private const string Font = "IBM Plex Sans";
        private static readonly object initLock = new();
        private static bool initialized;

        // QuestPDF: free Community license (individuals and companies under $1M revenue).
        // Fonts: web/fonts/*.ttf next to the app (OFL).
        private static void Init()
        {
            lock (initLock)
            {
                if (initialized) return;
                QuestPDF.Settings.License = LicenseType.Community;
                string dir = Path.Combine(AppContext.BaseDirectory, "web", "fonts");
                foreach (var f in Directory.Exists(dir) ? Directory.GetFiles(dir, "*.ttf") : Array.Empty<string>())
                    using (var s = File.OpenRead(f))
                        QuestPDF.Drawing.FontManager.RegisterFont(s);
                initialized = true;
            }
        }

        // Transcript header, for external readers, so numbers rather than line names:
        // who called whom. Shared by the PDF and the .txt.
        internal record Header(string Title, string Meta, string FromLabel, string From, string ToLabel, string To);

        internal static Header MakeHeader(CallRecord call, Strings l)
        {
            string kind = l.T(call.Missed
                ? (call.Direction == "in" ? "web.calls.kind.missed" : "web.calls.kind.no_answer")
                : (call.Direction == "in" ? "web.calls.kind.in" : "web.calls.kind.out"));
            string when = Program.ParseTs(call.Ts).ToString("dd.MM.yyyy HH:mm");
            string dur = $"{call.Billsec / 60}:{call.Billsec % 60:D2}";

            var ch = Program.settings.channels?.FirstOrDefault(c => c.name == call.Channel);
            string mine = string.IsNullOrWhiteSpace(ch?.number) ? call.Channel : FormatPhone(ch.number);
            string peer = FormatPhone(call.Peer) + (string.IsNullOrEmpty(call.PeerName) ? "" : $" ({call.PeerName})");
            bool incoming = call.Direction == "in";

            return new Header(
                l.T("web.pdf.title"),
                $"{l.T("web.pdf.date")}: {when} · {l.T("web.pdf.duration")}: {dur} · {kind}",
                l.T("web.pdf.from"), incoming ? peer : mine,
                l.T("web.pdf.to"), incoming ? mine : peer);
        }

        // +79001234567 -> +7 900 123-45-67; anything else as is.
        internal static string FormatPhone(string n)
        {
            if (string.IsNullOrEmpty(n)) return n;
            string d = new string(n.Where(char.IsDigit).ToArray());
            return n.StartsWith("+") && d.Length == 11 && d[0] == '7'
                ? $"+7 {d.Substring(1, 3)} {d.Substring(4, 3)}-{d.Substring(7, 2)}-{d.Substring(9, 2)}"
                : n;
        }

        public static byte[] Build(CallRecord call)
        {
            Init();
            var l = L10n.Transcript;
            var segs = Program.Segments(call);
            string them = string.IsNullOrEmpty(call.PeerName) ? l.T("web.calls.them") : call.PeerName;

            const string Ink = "#1f2328", Muted = "#656d76", Me = "#dbeafe", MeHead = "#1d4ed8", Them = "#eef0f2";

            return Document.Create(doc => doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(x => x.FontFamily(Font).FontSize(10.5f).FontColor(Ink).LineHeight(1.35f));

                var hd = MakeHeader(call, l);
                page.Header().PaddingBottom(14).BorderBottom(0.75f).BorderColor("#d0d7de").PaddingBottom(10).Column(h =>
                {
                    h.Item().Text(hd.Title).FontSize(15).Bold();
                    h.Item().PaddingTop(2).Text(hd.Meta).FontColor(Muted).FontSize(9.5f);
                    h.Item().PaddingTop(6).Text(t =>
                    {
                        t.Span(hd.FromLabel + ": ").FontColor(Muted);
                        t.Span(hd.From).Bold();
                    });
                    h.Item().Text(t =>
                    {
                        t.Span(hd.ToLabel + ": ").FontColor(Muted);
                        t.Span(hd.To).Bold();
                    });
                });

                page.Content().Column(col =>
                {
                    col.Spacing(7);
                    if (segs.Count == 0)
                        col.Item().Text(string.IsNullOrWhiteSpace(call.TrText) ? l.T("web.calls.no_speech") : call.TrText);

                    foreach (var s in segs)
                    {
                        string time = $"{(int)s.s / 60}:{(int)s.s % 60:D2}";
                        col.Item().ShowEntire().Column(item =>
                        {
                            if (s.who == null)
                            {
                                // Old mono recording: the side is unknown, just a line with its time.
                                item.Item().Text(time).FontSize(8).FontColor(Muted);
                                item.Item().Text(s.t);
                                return;
                            }
                            bool me = s.who == "me";
                            item.Item().Text($"{(me ? l.T("web.calls.me") : them)} · {time}")
                                .FontSize(8).FontColor(me ? MeHead : Muted);
                            // A bubble no wider than ~80% of the page, sized to the text.
                            item.Item().PaddingRight(90).AlignLeft()
                                .Background(me ? Me : Them).CornerRadius(7)
                                .PaddingVertical(5).PaddingHorizontal(9)
                                .Text(s.t);
                        });
                    }
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.DefaultTextStyle(x => x.FontSize(8).FontColor(Muted));
                    t.Span("ghostline · " + l.T("web.pdf.page") + " ");
                    t.CurrentPageNumber();
                    t.Span(" " + l.T("web.pdf.of") + " ");
                    t.TotalPages();
                });
            })).GeneratePdf();
        }
    }
}
