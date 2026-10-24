namespace ghostline
{
    // Which Telegram chats and ntfy topics a line goes to, and the other way round: which lines a chat
    // or a topic serves. The same rules for both channels:
    //  - a line has its own chats (channels[].chat_ids) and topic (channels[].ntfy_topic), or the
    //    common ones (telegram.chat_ids, ntfy.topic);
    //  - an SMS is sent from a chat/topic as "number / text" when it serves one line, and as
    //    "line / number / text" when it serves several (the line name first is accepted anyway).
    internal partial class Program
    {
        private static IEnumerable<Channel> AllLines => settings.channels ?? new List<Channel>();

        internal static List<string> LineChats(Channel ch) =>
            !TelegramEnabled ? new List<string>()
            : ch.chatIds is { Count: > 0 } ? ch.chatIds
            : settings.telegram.chatIds ?? new List<string>();

        // calls.chat_ids still overrides the chats for calls only.
        internal static List<string> LineCallChats(Channel ch) =>
            !TelegramEnabled ? new List<string>()
            : ch.calls?.chatIds is { Count: > 0 } ? ch.calls.chatIds
            : LineChats(ch);

        // null: the line has no topic (ntfy off, or neither ntfy_topic nor ntfy.topic set).
        internal static string LineTopic(Channel ch) =>
            !NtfyEnabled ? null
            : !string.IsNullOrWhiteSpace(ch.ntfyTopic) ? ch.ntfyTopic.Trim()
            : string.IsNullOrWhiteSpace(settings.ntfy.topic) ? null : settings.ntfy.topic.Trim();

        internal static List<Channel> LinesOfChat(string chatId) =>
            AllLines.Where(c => LineChats(c).Contains(chatId) || (c.calls?.chatIds?.Contains(chatId) ?? false)).ToList();

        internal static List<Channel> LinesOfTopic(string topic) =>
            AllLines.Where(c => LineTopic(c) == topic).ToList();

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
