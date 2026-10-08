using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace ghostline
{
    // Editing ghostline.yaml from the web UI. Values are replaced in place (by their position in the text),
    // so comments and the order of keys are kept. As in lookout.
    public static class ConfigYaml
    {
        // Name of a gateway or a line: path selectors refer to it (gateways[id=...], channels[name=...]).
        public static readonly Regex NameOk = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);

        // List keys: a comma-separated value in the form, [a, b] in the YAML.
        static readonly HashSet<string> ListKeys = new(StringComparer.OrdinalIgnoreCase) { "chat_ids" };

        // Value at a path: "web.password", "gateways[id=pbx].ami_port", "channels[name=home].calls.voice".
        // A missing key is added (with any missing intermediate sections); a missing list item with
        // this selector is added. An empty value for a missing key is not written.
        public static string Set(string yaml, string path, string value)
        {
            yaml ??= "";
            var parts = ParsePath(path);
            if (parts.Count == 0) return yaml;
            try
            {
                for (int attempt = 0; attempt < 32; attempt++)
                {
                    var root = Root(yaml);
                    if (root == null) return yaml;
                    if (TryReplace(yaml, root, parts, value, out string next)) return next;
                    if (!TryEnsureSelector(yaml, root, parts, out string ensured) || ensured == yaml)
                        return value == "" ? yaml : TryInsert(yaml, root, parts, value) ?? yaml;
                    yaml = ensured;
                }
                return yaml;
            }
            catch (YamlDotNet.Core.YamlException) { return yaml; }
        }

        // Form fields to YAML. The selector field (gateway id, line name) is applied last;
        // otherwise, after a rename, the other fields of the same item would not find it.
        public static string Apply(string yaml, IDictionary<string, string> fields)
        {
            if (fields == null) return yaml;
            static bool renames(string path)
            {
                var parts = ParsePath(path);
                var sel = parts.Count >= 2 ? parts[^2].selector : null;
                return sel != null && sel.StartsWith(parts[^1].key + "=", StringComparison.Ordinal);
            }
            foreach (var kv in fields.OrderBy(kv => renames(kv.Key)))
            {
                if (kv.Value == null) continue;
                yaml = Set(yaml, kv.Key, kv.Value);
            }
            return yaml;
        }

        // Removes a list item, for example "gateways[id=goip1]". When the last item goes, the list becomes [].
        public static string Remove(string yaml, string path)
        {
            if (string.IsNullOrEmpty(yaml) || string.IsNullOrEmpty(path)) return yaml;
            var parts = ParsePath(path);
            if (parts.Count == 0 || parts[^1].selector == null) return yaml;
            try
            {
                var root = Root(yaml);
                if (root == null) return yaml;
                var last = parts[^1];
                var seqParts = parts.Select((p, i) => i == parts.Count - 1 ? (p.key, (string)null) : p).ToList();
                if (Navigate(root, seqParts, out _) is not YamlSequenceNode seq) return yaml;
                var item = Pick(seq, last.selector);
                if (item == null) return yaml;
                if (seq.Children.Count == 1)
                {
                    int from = (int)item.Start.Index;
                    while (from > 0 && yaml[from - 1] is ' ' or '\t' or '\n' or '\r' or '-') from--;
                    if (from > 0 && yaml[from - 1] == ':')
                    {
                        int to = ContentEnd(yaml, item);
                        if (!SpanOf(seq, yaml, out _, out int seqEnd)) seqEnd = to;
                        to = Math.Max(to, seqEnd);
                        return yaml.Substring(0, from) + " []" + yaml.Substring(to);
                    }
                }
                if (!SpanOfListItem(yaml, item, out int start, out int end)) return yaml;
                return yaml.Substring(0, start) + yaml.Substring(end);
            }
            catch (YamlDotNet.Core.YamlException) { return yaml; }
        }

        public static string FormatScalar(string v)
        {
            v ??= "";
            if (v == "") return "\"\"";
            if (v is "true" or "false" or "null") return v;
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _) && !v.Contains(',') && !v.Contains(' '))
                return v;
            if (Regex.IsMatch(v, @"^[A-Za-z0-9_./+-]+$")) return v;
            return "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        // ---- walking the tree --------------------------------------------------------------------------

        static YamlNode Root(string yaml)
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            return stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode;
        }

        public static List<(string key, string selector)> ParsePath(string path)
        {
            var parts = new List<(string, string)>();
            foreach (Match m in Regex.Matches(path ?? "", @"([^.\[\]]+)(?:\[([^\]]+)\])?"))
                parts.Add((m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null));
            return parts;
        }

        // End of the node text: the last scalar or the closing bracket of a list. For an empty "[]"
        // YamlDotNet reports the end before the brackets, and a new line was inserted inside "chat_ids: []".
        static int ContentEnd(string yaml, YamlNode node)
        {
            switch (node)
            {
                case YamlMappingNode map when map.Children.Count > 0:
                    return map.Children.Max(kv => Math.Max(ContentEnd(yaml, kv.Key), ContentEnd(yaml, kv.Value)));
                case YamlSequenceNode seq when seq.Children.Count > 0 && !IsFlow(yaml, seq):
                    return seq.Children.Max(c => ContentEnd(yaml, c));
                case YamlSequenceNode or YamlMappingNode:
                    int i = (int)node.Start.Index;
                    if (i >= 0 && i < yaml.Length && yaml[i] is '[' or '{')
                    {
                        char open = yaml[i], close = open == '[' ? ']' : '}';
                        for (int depth = 0; i < yaml.Length; i++)
                        {
                            if (yaml[i] == open) depth++;
                            else if (yaml[i] == close && --depth == 0) return i + 1;
                        }
                    }
                    return (int)node.Start.Index;
                default:
                    return (int)node.End.Index;
            }
        }

        static bool IsFlow(string yaml, YamlNode node)
        {
            int i = (int)node.Start.Index;
            return i >= 0 && i < yaml.Length && yaml[i] is '[' or '{';
        }

        // End of the line pos is on (a new line is inserted after it, a comment stays where it is).
        static int LineEnd(string yaml, int pos)
        {
            pos = Math.Clamp(pos, 0, yaml.Length);
            while (pos < yaml.Length && yaml[pos] is not ('\n' or '\r')) pos++;
            return pos;
        }

        static bool SpanOf(YamlNode node, string yaml, out int start, out int end)
        {
            start = (int)node.Start.Index;
            end = ContentEnd(yaml, node);
            if (node is YamlSequenceNode seq && seq.Children.Count > 0)
            {
                int nodeStart = (int)seq.Start.Index;
                if (nodeStart >= 0 && nodeStart < yaml.Length && yaml[nodeStart] == '[')
                {
                    start = nodeStart;
                    int depth = 0;
                    for (int i = start; i < yaml.Length; i++)
                    {
                        if (yaml[i] == '[') depth++;
                        else if (yaml[i] == ']' && --depth == 0) { end = i + 1; break; }
                    }
                }
                else
                {
                    start = seq.Children.Min(c => (int)c.Start.Index);
                    while (start > 0 && yaml[start - 1] is ' ' or '\t') start--;
                    if (start > 0 && yaml[start - 1] == '-') start--;
                }
            }
            return end > start && start >= 0 && end <= yaml.Length;
        }

        static bool TryReplace(string yaml, YamlNode root, List<(string key, string selector)> parts, string value, out string next)
        {
            next = yaml;
            var child = Navigate(root, parts, out _);
            if (child == null || child is YamlMappingNode) return false;
            if (!SpanOf(child, yaml, out int start, out int end)) return false;
            string text = child is YamlSequenceNode || ListKeys.Contains(parts[^1].key) ? FormatSequence(value) : FormatScalar(value);
            next = yaml.Substring(0, start) + text + yaml.Substring(end);
            return true;
        }

        // Missing key: find the deepest existing section of the path and add the missing
        // sections and the key to it, indented like the neighbouring keys.
        static string TryInsert(string yaml, YamlNode root, List<(string key, string selector)> parts, string value)
        {
            YamlMappingNode map = null;
            int depth = parts.Count - 1;
            for (; depth >= 0; depth--)
            {
                var node = depth == 0 ? root : Navigate(root, parts.Take(depth).ToList(), out _);
                if (node == null) continue;
                map = node as YamlMappingNode;
                break;
            }
            if (map == null) return null;
            var missing = parts.Skip(depth).ToList();
            if (missing.Any(p => p.selector != null)) return null;
            if (map.Children.Keys.OfType<YamlScalarNode>().Any(k => k.Value == missing[0].key)) return null;

            string nl = yaml.Contains("\r\n") ? "\r\n" : "\n";
            int insertAt, indent;
            if (map.Children.Count > 0)
            {
                var last = map.Children.Last();
                insertAt = LineEnd(yaml, Math.Max((int)last.Key.End.Index, ContentEnd(yaml, last.Value)));
                indent = (int)last.Key.Start.Column - 1;
            }
            else
            {
                insertAt = (int)map.End.Index;
                indent = Math.Max(0, (int)map.Start.Column + 1);
            }
            if (insertAt < 0 || insertAt > yaml.Length) return null;

            var sb = new StringBuilder();
            for (int i = 0; i < missing.Count; i++)
            {
                string key = missing[i].key;
                sb.Append(nl).Append(' ', indent + 2 * i).Append(key).Append(':');
                if (i == missing.Count - 1)
                    sb.Append(' ').Append(ListKeys.Contains(key) ? FormatSequence(value) : FormatScalar(value));
            }
            return yaml.Substring(0, insertAt) + sb + yaml.Substring(insertAt);
        }

        static YamlNode Navigate(YamlNode node, List<(string key, string selector)> parts, out YamlMappingNode parent)
        {
            parent = null;
            for (int i = 0; i < parts.Count; i++)
            {
                var (key, sel) = parts[i];
                if (node is not YamlMappingNode map) return null;
                parent = map;
                var child = Child(map, key);
                if (child == null) return null;
                if (sel != null)
                {
                    if (child is not YamlSequenceNode seq) return null;
                    child = Pick(seq, sel);
                    if (child == null) return null;
                }
                node = child;
            }
            return node;
        }

        static YamlNode Pick(YamlSequenceNode seq, string sel)
        {
            if (int.TryParse(sel, out int idx))
                return idx >= 0 && idx < seq.Children.Count ? seq.Children[idx] : null;
            int eq = sel.IndexOf('=');
            if (eq <= 0) return null;
            string field = sel.Substring(0, eq), want = sel.Substring(eq + 1);
            foreach (var item in seq.Children.OfType<YamlMappingNode>())
                if (Child(item, field) is YamlScalarNode vs && vs.Value == want)
                    return item;
            return null;
        }

        static YamlNode Child(YamlMappingNode map, string key)
        {
            foreach (var kv in map.Children)
                if (kv.Key is YamlScalarNode ks && ks.Value == key) return kv.Value;
            return null;
        }

        // No list item with the selector field=value: adds "- field: value" (and the list itself if missing).
        static bool TryEnsureSelector(string yaml, YamlNode root, List<(string key, string selector)> parts, out string next)
        {
            next = yaml;
            YamlNode node = root;
            foreach (var (key, sel) in parts)
            {
                if (node is not YamlMappingNode map) return false;
                var child = Child(map, key);
                if (sel == null)
                {
                    if (child == null) return false;
                    node = child;
                    continue;
                }
                int eq = sel.IndexOf('=');
                if (eq <= 0) return false;
                string field = sel.Substring(0, eq), want = sel.Substring(eq + 1);
                if (child == null)
                {
                    next = InsertSequenceWithItem(yaml, map, key, field, want);
                    return next != yaml;
                }
                if (child is not YamlSequenceNode seq) return false;
                var item = Pick(seq, sel);
                if (item != null) { node = item; continue; }
                next = AppendSequenceItem(yaml, seq, field, want);
                return next != yaml;
            }
            return false;
        }

        static string InsertSequenceWithItem(string yaml, YamlMappingNode map, string key, string field, string want)
        {
            string nl = yaml.Contains("\r\n") ? "\r\n" : "\n";
            int insertAt, indent;
            if (map.Children.Count > 0)
            {
                var last = map.Children.Last();
                insertAt = LineEnd(yaml, Math.Max((int)last.Key.End.Index, ContentEnd(yaml, last.Value)));
                indent = (int)last.Key.Start.Column - 1;
            }
            else
            {
                insertAt = (int)map.End.Index;
                indent = Math.Max(0, (int)map.Start.Column + 1);
            }
            if (insertAt < 0 || insertAt > yaml.Length) return yaml;
            string block = nl + new string(' ', indent) + key + ":" + nl + new string(' ', indent + 2) + "- " + field + ": " + FormatScalar(want);
            return yaml.Substring(0, insertAt) + block + yaml.Substring(insertAt);
        }

        static string AppendSequenceItem(string yaml, YamlSequenceNode seq, string field, string want)
        {
            string nl = yaml.Contains("\r\n") ? "\r\n" : "\n";
            string line = "- " + field + ": " + FormatScalar(want);
            if (seq.Children.Count == 0)
            {
                // "key: []" to a block list with one item
                if (!SpanOf(seq, yaml, out int start, out int end) || end < start) return yaml;
                int i = start;
                while (i > 0 && yaml[i - 1] != '\n' && yaml[i - 1] != '\r') i--;
                int lineIndent = 0;
                while (i + lineIndent < yaml.Length && yaml[i + lineIndent] is ' ' or '\t') lineIndent++;
                return yaml.Substring(0, start) + nl + new string(' ', lineIndent + 2) + line + yaml.Substring(end);
            }
            var last = seq.Children.Last();
            int insertAt = LineEnd(yaml, ContentEnd(yaml, last));
            if (insertAt < 0 || insertAt > yaml.Length) return yaml;
            return yaml.Substring(0, insertAt) + nl + new string(' ', ListItemIndent(yaml, last)) + line + yaml.Substring(insertAt);
        }

        static int ListItemIndent(string yaml, YamlNode item)
        {
            int i = (int)item.Start.Index;
            while (i > 0 && yaml[i - 1] is ' ' or '\t') i--;
            if (i > 0 && yaml[i - 1] == '-') i--;
            int line = i;
            while (line > 0 && yaml[line - 1] != '\n' && yaml[line - 1] != '\r') line--;
            return Math.Max(0, i - line);
        }

        static bool SpanOfListItem(string yaml, YamlNode item, out int start, out int end)
        {
            start = (int)item.Start.Index;
            end = LineEnd(yaml, ContentEnd(yaml, item));
            while (start > 0 && yaml[start - 1] is ' ' or '\t') start--;
            if (start > 0 && yaml[start - 1] == '-') start--;
            while (start > 0 && yaml[start - 1] is ' ' or '\t') start--;
            int lineStart = start;
            while (lineStart > 0 && yaml[lineStart - 1] != '\n' && yaml[lineStart - 1] != '\r') lineStart--;
            start = lineStart;
            if (end < yaml.Length && yaml[end] == '\r') end++;
            if (end < yaml.Length && yaml[end] == '\n') end++;
            return end > start && start >= 0 && end <= yaml.Length;
        }

        static string FormatSequence(string csv)
        {
            var items = (csv ?? "").Split(',').Select(s => s.Trim()).Where(s => s != "");
            return "[" + string.Join(", ", items.Select(FormatScalar)) + "]";
        }
    }

    // Settings form: sections with fields that the web UI draws as a tree; fields go back into the YAML by path.
    public static class SettingsForm
    {
        public record Field(string path, string type, string value, string label, string[] options = null);
        public record Group(string id, string title, List<Field> fields);
        public record Item(string key, List<Field> fields);

        static readonly string[] Locales = { "", "en", "ru" };

        public static object Snapshot(Settings s)
        {
            s ??= new Settings();
            var gatewayIds = (s.gateways ?? new()).Select(g => g.id).Where(x => !string.IsNullOrEmpty(x)).ToArray();
            var c = s.calls;
            var groups = new List<Group>
            {
                G("general",
                    F("mode", "select", s.mode ?? "sms", "sms", "full"),
                    F("locale.default", "select", s.locale?.@default ?? "en", "en", "ru"),
                    F("locale.web", "select", s.locale?.web ?? "", Locales),
                    F("locale.telegram", "select", s.locale?.telegram ?? "", Locales),
                    F("locale.transcript", "select", s.locale?.transcript ?? "", Locales)),
                G("telegram",
                    F("telegram.token", "text", s.telegram?.token),
                    F("telegram.chat_ids", "text", Csv(s.telegram?.chatIds))),
                G("web",
                    F("web.port", "number", n(s.web?.port)),
                    F("web.user", "text", s.web?.user),
                    F("web.password", "text", s.web?.password)),
                G("logger", F("logger.file", "checkbox", b(s.logger?.file ?? false))),
                G("gateways"),
                G("channels"),
                G("calls",
                    F("calls.recordings_url", "text", c?.recordingsUrl),
                    F("calls.poll_seconds", "number", n(c?.pollSeconds))),
                G("cdr_db",
                    F("calls.cdr_db.host", "text", c?.cdrDb?.host),
                    F("calls.cdr_db.port", "number", n(c?.cdrDb?.port)),
                    F("calls.cdr_db.database", "text", c?.cdrDb?.database),
                    F("calls.cdr_db.user", "text", c?.cdrDb?.user),
                    F("calls.cdr_db.password", "text", c?.cdrDb?.password)),
                G("transcribe",
                    F("calls.transcribe.enabled", "checkbox", b(c?.transcribe?.enabled ?? false)),
                    F("calls.transcribe.url", "text", c?.transcribe?.url),
                    F("calls.transcribe.model", "text", c?.transcribe?.model),
                    F("calls.transcribe.language", "text", c?.transcribe?.language),
                    F("calls.transcribe.prompt", "textarea", c?.transcribe?.prompt)),
                G("goip",
                    F("goip.smtp.enabled", "checkbox", b(s.goip?.smtp?.enabled ?? false)),
                    F("goip.smtp.port", "number", n(s.goip?.smtp?.port))),
            };
            return new
            {
                groups,
                gateways = (s.gateways ?? new()).Where(g => !string.IsNullOrEmpty(g.id)).Select(g => new Item(g.id, GatewayFields(g.id, g))).ToList(),
                channels = (s.channels ?? new()).Where(ch => !string.IsNullOrEmpty(ch.name)).Select(ch => new Item(ch.name, ChannelFields(ch.name, ch, gatewayIds))).ToList(),
                gatewayTemplate = GatewayFields("{key}"),
                channelTemplate = ChannelFields("{key}", null, gatewayIds),
            };
        }

        static List<Field> GatewayFields(string id, Gateway g = null)
        {
            g ??= new Gateway { id = id, type = "quectel", amiPort = 5038 };
            string p = $"gateways[id={id}]";
            return new()
            {
                F($"{p}.id", "text", g.id),
                F($"{p}.type", "select", g.type ?? "quectel", "quectel", "yeastar", "goip"),
                F($"{p}.ip", "text", g.ip),
                F($"{p}.ami_port", "number", n(g.amiPort)),
                F($"{p}.http_port", "number", n(g.httpPort)),
                F($"{p}.user", "text", g.user),
                F($"{p}.password", "text", g.password),
            };
        }

        static List<Field> ChannelFields(string name, Channel ch, string[] gatewayIds)
        {
            ch ??= new Channel { name = name, gateway = gatewayIds.FirstOrDefault() };
            var calls = ch.calls ?? new ChannelCalls();
            string p = $"channels[name={name}]";
            var gws = gatewayIds.Contains(ch.gateway ?? "") || string.IsNullOrEmpty(ch.gateway) ? gatewayIds : gatewayIds.Append(ch.gateway).ToArray();
            return new()
            {
                F($"{p}.name", "text", ch.name),
                F($"{p}.gateway", "select", ch.gateway, gws),
                F($"{p}.pattern", "text", ch.pattern),
                F($"{p}.line", "text", ch.line),
                F($"{p}.number", "text", ch.number),
                F($"{p}.calls.voice", "checkbox", b(calls.voice)),
                F($"{p}.calls.transcript", "checkbox", b(calls.transcript)),
                F($"{p}.calls.missed", "checkbox", b(calls.missed)),
                F($"{p}.calls.chat_ids", "text", Csv(calls.chatIds)),
            };
        }

        static string Csv(List<string> v) => v == null ? "" : string.Join(", ", v);
        static string n(int? v) => v is null or 0 ? "" : v.Value.ToString(CultureInfo.InvariantCulture);
        static string b(bool v) => v ? "true" : "false";

        // Field label: web.field.<path without selectors>, for example web.field.calls.cdr_db.host.
        static string Label(string path)
        {
            string plain = string.Join(".", ConfigYaml.ParsePath(path).Select(p => p.key));
            foreach (var key in new[] { plain, plain.Substring(plain.IndexOf('.') + 1) })
                if (L10n.Web.Has("web.field." + key)) return L10n.Web.T("web.field." + key);
            return plain;
        }

        static Field F(string path, string type, string value, params string[] options) =>
            new(path, type, value ?? "", Label(path), options is { Length: > 0 } ? options : null);

        static Group G(string id, params Field[] fields) => new(id, L10n.Web.T("web.config.group." + id), fields.ToList());
    }
}
