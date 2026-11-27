namespace ghostline
{
    // Which chats a line goes to, and the other way round: which lines a chat serves. Each chat (a Telegram
    // chat, an ntfy topic, a Capy recipient) subscribes to what it gets (Subscription): SMS of some lines,
    // calls of some lines, alerts.
    //  - an SMS is sent from a chat/topic as "number / text" when it serves one line, and as
    //    "line / number / text" when it serves several (the line name first is accepted anyway); a chat
    //    serves the lines it has in sms or calls.
    internal partial class Program
    {
        private static IEnumerable<Channel> AllLines => settings.lines ?? new List<Channel>();

        // The client a queued target goes to: "ntfy:<topic>", "capy:<recipient>", otherwise a Telegram chat.
        internal static string TargetKind(string target) =>
            IsNtfyTarget(target) ? "ntfy" : IsCapyTarget(target) ? "capy" : "telegram";

        internal static bool ClientOn(string kind) => kind switch
        {
            "ntfy" => NtfyEnabled,
            "capy" => CapyEnabled,
            _ => TelegramEnabled
        };

        private static IEnumerable<TelegramChat> TgChats =>
            TelegramEnabled ? (settings.clients.telegram.chats ?? new()).Where(c => !string.IsNullOrWhiteSpace(c.id)) : Enumerable.Empty<TelegramChat>();

        private static IEnumerable<NtfyTopic> NtfyTopics =>
            NtfyEnabled ? (settings.clients.ntfy.topics ?? new()).Where(t => !string.IsNullOrWhiteSpace(t.topic)) : Enumerable.Empty<NtfyTopic>();

        // Telegram chats that get the line's SMS (and its "no network" notices).
        internal static List<string> LineChats(Channel ch) =>
            TgChats.Where(c => Subscription.Covers(c.sms, ch)).Select(c => c.id.Trim()).Distinct().ToList();

        internal static List<string> LineCallChats(Channel ch) =>
            TgChats.Where(c => Subscription.Covers(c.calls, ch)).Select(c => c.id.Trim()).Distinct().ToList();

        internal static List<string> LineTopics(Channel ch) =>
            NtfyTopics.Where(t => Subscription.Covers(t.sms, ch)).Select(t => t.topic.Trim()).Distinct().ToList();

        internal static List<string> LineCallTopics(Channel ch) =>
            NtfyTopics.Where(t => Subscription.Covers(t.calls, ch)).Select(t => t.topic.Trim()).Distinct().ToList();

        internal static List<string> AlertChats => TgChats.Where(c => c.alerts).Select(c => c.id.Trim()).Distinct().ToList();
        internal static List<string> AlertTopics => NtfyTopics.Where(t => t.alerts).Select(t => t.topic.Trim()).Distinct().ToList();

        // Lines a chat serves (sends SMS on, answers /help for): the ones it gets SMS or calls of.
        internal static List<Channel> LinesOfChat(string chatId)
        {
            var chats = TgChats.Where(c => c.id.Trim() == chatId).ToList();
            return AllLines.Where(l => chats.Any(c => Subscription.Covers(c.sms, l) || Subscription.Covers(c.calls, l))).ToList();
        }

        internal static List<Channel> LinesOfTopic(string topic)
        {
            var topics = NtfyTopics.Where(t => t.topic.Trim() == topic).ToList();
            return AllLines.Where(l => topics.Any(t => Subscription.Covers(t.sms, l) || Subscription.Covers(t.calls, l))).ToList();
        }

        // An SMS to send, written in a chat or topic that serves these lines; line == null: not understood.
        internal static (Channel line, string number, string text) ParseSmsCommand(List<Channel> lines, string message)
        {
            var parts = message.Replace("\r", "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (parts.Count >= 3)
            {
                var named = lines.FirstOrDefault(c => string.Equals(c.name, parts[0], StringComparison.OrdinalIgnoreCase));
                if (named != null)
                    return (named, parts[1], string.Join(" ", parts.Skip(2)));
            }
            if (lines.Count == 1 && parts.Count >= 2)
                return (lines[0], parts[0], string.Join(" ", parts.Skip(1)));
            return (null, null, null);
        }

        // Lines as "<code>home</code> +79001234567", one per line.
        private static string LinesHtml(List<Channel> lines) =>
            string.Join("\n", lines.Select(c =>
                $"<code>{Html(c.name)}</code>" + (string.IsNullOrEmpty(c.number) ? "" : " " + Html(c.number))));

        private static string Html(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        // Help for a chat/topic (Telegram HTML; ntfy converts it to Markdown).
        internal static string HelpText(List<Channel> lines)
        {
            var l = L10n.Tg;
            string sms = lines.Count == 1
                ? l.T("tg.help.sms.one", Html(lines[0].name))
                : l.T("tg.help.sms.many", Html(lines.FirstOrDefault()?.name ?? "home"), LinesHtml(lines));
            return l.T("tg.help.intro") + "\n\n" + sms
                 + (settings.FullMode ? "\n\n" + l.T("tg.help.calls") : "")
                 + "\n\n" + l.T("tg.help.commands")
                 + "\n\n" + l.T("tg.help.footer", VersionInfo.ProjectUrl, Html(VersionInfo.Version));
        }

        internal static string WrongFormatText(List<Channel> lines) =>
            lines.Count == 1
                ? L10n.Tg.T("tg.sms.wrong.one", Html(lines[0].name))
                : L10n.Tg.T("tg.sms.wrong.many", LinesHtml(lines));
    }
}
