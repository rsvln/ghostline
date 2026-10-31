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
        // Name of a gateway or a line: path selectors refer to it (gateways[id=...], lines[name=...]).
        public static readonly Regex NameOk = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);

        // List keys: a comma-separated value in the form, [a, b] in the YAML.
        static readonly HashSet<string> ListKeys = new(StringComparer.OrdinalIgnoreCase) { "chat_ids", "local_nets", "ignore_ip" };

        // Value at a path: "web.password", "gateways[id=pbx].ami_port", "lines[name=home].calls.voice".
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

        // Removes a section, for example "clients.ntfy" or "lines[name=home].clients.telegram".
        // When it is the last key of its parent, the parent becomes {} (an empty section, not a
        // missing one: "clients: {}" on a line means "send nowhere").
        public static string RemoveSection(string yaml, string path)
        {
            if (string.IsNullOrEmpty(yaml)) return yaml;
            var parts = ParsePath(path);
            if (parts.Count < 2 || parts[^1].selector != null) return yaml;
            try
            {
                var root = Root(yaml);
                if (Navigate(root, parts.Take(parts.Count - 1).ToList(), out _) is not YamlMappingNode parent) return yaml;
                var kv = parent.Children.FirstOrDefault(c => c.Key is YamlScalarNode k && k.Value == parts[^1].key);
                if (kv.Key == null) return yaml;
                int end = LineEnd(yaml, ContentEnd(yaml, kv.Value));
                if (end < yaml.Length && yaml[end] == '\r') end++;
                if (end < yaml.Length && yaml[end] == '\n') end++;
                if (parent.Children.Count == 1)
                {
                    // "clients:\n  ntfy: ..." -> "clients: {}"
                    int colon = yaml.LastIndexOf(':', LineStart(yaml, (int)kv.Key.Start.Index) - 1);
                    if (colon < 0) return yaml;
                    string nl = yaml.Contains("\r\n") ? "\r\n" : "\n";
                    return yaml.Substring(0, colon + 1) + " {}" + nl + yaml.Substring(end);
                }
                return yaml.Substring(0, LineStart(yaml, (int)kv.Key.Start.Index)) + yaml.Substring(end);
            }
            catch (YamlDotNet.Core.YamlException) { return yaml; }
        }

        // Adds a section with its first keys (raw YAML values), for example "clients.telegram" with
        // chat_ids: []. Missing parents are created; an empty parent ({} or nothing) is filled in.
        // An existing section is left as it is.
        public static string AddSection(string yaml, string path, IList<(string key, string raw)> children)
        {
            var parts = ParsePath(path);
            if (parts.Count == 0 || parts[^1].selector != null) return yaml;
            try
            {
                var root = Root(yaml);
                if (root == null || Navigate(root, parts, out _) != null) return yaml;
                string nl = yaml.Contains("\r\n") ? "\r\n" : "\n";

                // The deepest part of the path that exists.
                int depth = parts.Count - 1;
                YamlNode node = null;
                for (; depth >= 0; depth--)
                {
                    node = depth == 0 ? root : Navigate(root, parts.Take(depth).ToList(), out _);
                    if (node != null) break;
                }
                if (node == null) return yaml;
                var missing = parts.Skip(depth).ToList();
                if (missing.Any(p => p.selector != null)) return yaml;

                string Block(int indent)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < missing.Count; i++)
                        sb.Append(nl).Append(' ', indent + 2 * i).Append(missing[i].key).Append(':');
                    foreach (var (key, raw) in children)
                        sb.Append(nl).Append(' ', indent + 2 * missing.Count).Append(key).Append(": ").Append(raw);
                    return sb.ToString();
                }

                if (node is YamlMappingNode map && map.Children.Count > 0)
                {
                    var last = map.Children.Last();
                    int insertAt = LineEnd(yaml, Math.Max((int)last.Key.End.Index, ContentEnd(yaml, last.Value)));
                    return yaml.Substring(0, insertAt) + Block((int)last.Key.Start.Column - 1) + yaml.Substring(insertAt);
                }

                // An empty parent: "key: {}" or "key:" with nothing after it. The block goes after the
                // key's colon, indented under the key.
                int keyStart = LineStart(yaml, (int)node.Start.Index);
                int indentOfKey = 0;
                while (keyStart + indentOfKey < yaml.Length && yaml[keyStart + indentOfKey] is ' ' or '-') indentOfKey++;
                int colonAt = yaml.LastIndexOf(':', Math.Max(0, (int)node.Start.Index - 1));
                if (colonAt < keyStart) colonAt = yaml.IndexOf(':', keyStart);
                if (colonAt < 0) return yaml;
                int valueEnd = node is YamlMappingNode ? ContentEnd(yaml, node) : colonAt + 1;
                return yaml.Substring(0, colonAt + 1) + Block(indentOfKey + 2) + yaml.Substring(Math.Max(valueEnd, colonAt + 1));
            }
            catch (YamlDotNet.Core.YamlException) { return yaml; }
        }

        // Older configs have "telegram" at the top level; the form edits clients.telegram, so before
        // the form changes are applied that block moves under "clients" (comments included).
        public static string MoveLegacyTelegram(string yaml)
        {
            string nl = yaml.Contains("\r\n") ? "\r\n" : "\n";
            var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
            static bool TopKey(string l, string key) => Regex.IsMatch(l, "^" + key + @":\s*(#.*)?$");
            int start = lines.FindIndex(l => TopKey(l, "telegram"));
            if (start < 0) return yaml;
            int end = start + 1;
            while (end < lines.Count && (lines[end].Length == 0 || lines[end][0] is ' ' or '#')) end++;
            while (end > start + 1 && lines[end - 1].Trim().Length == 0) end--;   // trailing blank lines stay
            var block = lines.GetRange(start, end - start).Select(l => l.Length == 0 ? l : "  " + l).ToList();
            lines.RemoveRange(start, end - start);

            int clients = lines.FindIndex(l => TopKey(l, "clients") || Regex.IsMatch(l, @"^clients:\s*\{\s*\}"));
            if (clients < 0)
                lines.InsertRange(start, new[] { "clients:" }.Concat(block));
            else
            {
                if (Regex.IsMatch(lines[clients], @"^clients:\s*\{\s*\}")) lines[clients] = "clients:";
                lines.InsertRange(clients + 1, block);
            }
            return string.Join(nl, lines);
        }

        // Renames a key in place, for example "channels" -> "lines" or "lines[name=home].line" -> "modem":
        // its value and comments stay.
        public static string RenameKey(string yaml, string path, string newKey)
        {
            var parts = ParsePath(path);
            if (parts.Count == 0 || parts[^1].selector != null) return yaml;
            try
            {
                var root = Root(yaml);
                var parent = parts.Count == 1 ? root as YamlMappingNode
                    : Navigate(root, parts.Take(parts.Count - 1).ToList(), out _) as YamlMappingNode;
                if (parent == null) return yaml;
                var key = parent.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == parts[^1].key);
                if (key == null || parent.Children.Keys.OfType<YamlScalarNode>().Any(k => k.Value == newKey)) return yaml;
                int start = (int)key.Start.Index, end = (int)key.End.Index;
                return yaml.Substring(0, start) + newKey + yaml.Substring(end);
            }
            catch (YamlDotNet.Core.YamlException) { return yaml; }
        }

        // Removes a top-level section with its lines (and comments inside it).
        public static string RemoveTopSection(string yaml, string key)
        {
            string nl = yaml.Contains("\r\n") ? "\r\n" : "\n";
            var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
            int start = lines.FindIndex(l => Regex.IsMatch(l, "^" + Regex.Escape(key) + @":(\s.*)?$"));
            if (start < 0) return yaml;
            int end = start + 1;
            while (end < lines.Count && (lines[end].Length == 0 || lines[end][0] is ' ' or '#')) end++;
            while (end > start + 1 && lines[end - 1].Trim().Length == 0) end--;   // trailing blank lines stay
            if (end < lines.Count && start > 0 && lines[start - 1].Trim().Length == 0 && lines[end].Trim().Length == 0)
                end++;                                                          // no double blank line
            lines.RemoveRange(start, end - start);
            return string.Join(nl, lines);
        }

        // Rewrites a file of the format before 2026-10-30 the way SettingsFile.Normalize read it: keys are
        // renamed and values set in place, so the comments stay. s: the file as parsed (normalized).
        internal static string Migrate(string yaml, Settings s)
        {
            var m = s.Migration;
            if (m == null) return yaml;
            foreach (var id in m.Merged.Keys)
                yaml = Remove(yaml, $"gateways[id={id}]");
            foreach (var id in m.Retyped)
                yaml = Set(yaml, $"gateways[id={id}].type", "asterisk");
            if (m.ChannelsRenamed)
                yaml = RenameKey(yaml, "channels", "lines");
            foreach (var ch in s.lines)
            {
                if (m.LineKeys.TryGetValue(ch.name ?? "", out var key))
                    yaml = RenameKey(yaml, $"lines[name={ch.name}].line", key);
                if (m.Regatewayed.Contains(ch.name))
                    yaml = Set(yaml, $"lines[name={ch.name}].gateway", ch.gateway);
            }
            foreach (var g in s.gateways.Where(g => g.IsAsterisk))
            {
                if (g.Managed && g.id == m.ManagedGateway)
                {
                    string at = $"gateways[id={g.id}]";
                    var p = g.managed;
                    yaml = Set(yaml, at + ".pbx", "managed");
                    yaml = Set(yaml, at + ".managed.conf_dir", p.confDir ?? "");
                    yaml = Set(yaml, at + ".managed.asterisk_dir", p.asteriskDir ?? "");
                    yaml = Set(yaml, at + ".managed.recordings_path", p.recordingsPath ?? "");
                    yaml = Set(yaml, at + ".managed.sip.tls.cert", p.sip.tls?.cert ?? "");
                    yaml = Set(yaml, at + ".managed.sip.tls.key", p.sip.tls?.key ?? "");
                    yaml = Set(yaml, at + ".managed.sip.tls.port", (p.sip.tls?.port ?? 5061).ToString(CultureInfo.InvariantCulture));
                    if (p.sip.udpPort != 0)
                        yaml = Set(yaml, at + ".managed.sip.udp_port", p.sip.udpPort.ToString(CultureInfo.InvariantCulture));
                    yaml = Set(yaml, at + ".managed.sip.external_address", p.sip.externalAddress ?? "");
                    if (p.sip.localNets?.Count > 0)
                        yaml = Set(yaml, at + ".managed.sip.local_nets", string.Join(", ", p.sip.localNets));
                }
                foreach (var modem in g.modems ?? new())
                {
                    string at = $"gateways[id={g.id}].modems[name={modem.name}]";
                    yaml = Set(yaml, at + ".driver", modem.driver ?? "");
                    yaml = Set(yaml, at + ".data", modem.data ?? "");
                    yaml = Set(yaml, at + ".audio", modem.audio ?? "");
                    yaml = Set(yaml, at + ".alsadev", modem.alsadev ?? "");
                    if (modem.noqpcmv) yaml = Set(yaml, at + ".noqpcmv", "true");
                }
            }
            if (m.PbxRemoved)
                yaml = RemoveTopSection(yaml, "pbx");
            if (m.SystemMoved)
                yaml = MoveIntoSection(yaml, LegacySystem.Keys, "system");
            return yaml;
        }

        // Moves top-level sections under a new top-level section, in place: each with its lines, the comments
        // inside it and the comment lines right above it, indented by two spaces. The new section goes where
        // the first of them was; an existing one gets them at its end.
        public static string MoveIntoSection(string yaml, IEnumerable<string> keys, string parent)
        {
            string nl = yaml.Contains("\r\n") ? "\r\n" : "\n";
            var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
            static bool Top(string l) => l.Length > 0 && l[0] is not (' ' or '#' or '-');
            static string KeyOf(string l) => Regex.Match(l, @"^([A-Za-z_][A-Za-z0-9_]*):(\s|$)").Groups[1].Value;
            var wanted = new HashSet<string>(keys);
            var moved = new List<string>();
            int at = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (!Top(lines[i]) || !wanted.Contains(KeyOf(lines[i]))) continue;
                int start = i;
                // Comment lines right above it go with it, unless they open the file.
                while (start > 0 && lines[start - 1].StartsWith('#')) start--;
                if (start == 0) start = i;
                int end = i + 1;
                while (end < lines.Count && !Top(lines[end])) end++;
                while (end > i + 1 && (lines[end - 1].Trim().Length == 0 || lines[end - 1].StartsWith('#'))) end--;
                if (moved.Count > 0) moved.Add("");
                moved.AddRange(lines.GetRange(start, end - start).Select(l => l.Length == 0 ? l : "  " + l));
                lines.RemoveRange(start, end - start);
                // The blank line after it goes too, so no double blank line is left.
                if (start < lines.Count && lines[start].Trim().Length == 0 && (start == 0 || lines[start - 1].Trim().Length == 0))
                    lines.RemoveAt(start);
                if (at < 0) at = start;
                i = start - 1;
            }
            if (moved.Count == 0) return yaml;
            int existing = lines.FindIndex(l => KeyOf(l) == parent);
            if (existing >= 0)
            {
                int end = existing + 1;
                while (end < lines.Count && !Top(lines[end])) end++;
                while (end > existing + 1 && lines[end - 1].Trim().Length == 0) end--;
                lines[existing] = Regex.Replace(lines[existing], @":\s*\{\s*\}", ":");
                lines.InsertRange(end, moved);
            }
            else
            {
                at = Math.Min(at, lines.Count);
                var block = new List<string> { parent + ":" };
                block.AddRange(moved);
                if (at < lines.Count && lines[at].Trim().Length != 0) block.Add("");
                lines.InsertRange(at, block);
            }
            return string.Join(nl, lines);
        }

        static int LineStart(string yaml, int pos)
        {
            pos = Math.Clamp(pos, 0, yaml.Length);
            while (pos > 0 && yaml[pos - 1] is not ('\n' or '\r')) pos--;
            return pos;
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
        // clients: the client sections of a line, null when the line has no "clients" section at all.
        public record Item(string key, List<Field> fields, string[] clients = null);
        // An Asterisk gateway: its modems (edited with their own form), whether ghostline writes its config.
        public record GatewayItem(string key, List<Field> fields, string type, bool managed, List<PbxModem> modems);

        static readonly string[] Locales = { "", "en", "ru" };

        public static object Snapshot(Settings s)
        {
            s ??= new Settings();
            var gatewayIds = (s.gateways ?? new()).Select(g => g.id).Where(x => !string.IsNullOrEmpty(x)).ToArray();
            var c = s.calls;
            var groups = new List<Group>
            {
                G("system",
                    F("system.mode", "select", s.mode ?? "sms", "sms", "full"),
                    F("system.locale.default", "select", s.locale?.@default ?? "en", "en", "ru"),
                    F("system.locale.web", "select", s.locale?.web ?? "", Locales),
                    F("system.locale.telegram", "select", s.locale?.telegram ?? "", Locales),
                    F("system.locale.transcript", "select", s.locale?.transcript ?? "", Locales)),
                G("clients"),
                G("telegram",
                    F("clients.telegram.token", "text", s.clients?.telegram?.token),
                    F("clients.telegram.chat_ids", "text", Csv(s.clients?.telegram?.chatIds))),
                G("ntfy",
                    F("clients.ntfy.url", "text", s.clients?.ntfy?.url),
                    F("clients.ntfy.token", "text", s.clients?.ntfy?.token),
                    F("clients.ntfy.topic", "text", s.clients?.ntfy?.topic)),
                G("web",
                    F("system.web.port", "number", n(s.web?.port)),
                    F("system.web.user", "text", s.web?.user),
                    F("system.web.password", "text", s.web?.password)),
                G("logger", F("system.logger.level", "select", string.IsNullOrEmpty(s.logger?.level) ? "info" : s.logger.level, SysLog.Levels),
                           F("system.logger.file", "checkbox", b(s.logger?.file ?? false))),
                G("monitor", F("system.monitor.alert_minutes", "number", (s.monitor?.alertMinutes ?? 2).ToString(CultureInfo.InvariantCulture))),
                G("gateways"),
                G("lines"),
                G("managed"),
                G("modems"),
                G("phone"),
                G("calls",
                    F("system.calls.recordings_url", "text", c?.recordingsUrl),
                    F("system.calls.recordings_dir", "text", c?.recordingsDir),
                    F("system.calls.cdr_sqlite", "text", c?.cdrSqlite),
                    F("system.calls.poll_seconds", "number", n(c?.pollSeconds))),
                G("cdr_db",
                    F("system.calls.cdr_db.host", "text", c?.cdrDb?.host),
                    F("system.calls.cdr_db.port", "number", n(c?.cdrDb?.port)),
                    F("system.calls.cdr_db.database", "text", c?.cdrDb?.database),
                    F("system.calls.cdr_db.user", "text", c?.cdrDb?.user),
                    F("system.calls.cdr_db.password", "text", c?.cdrDb?.password)),
                G("transcribe",
                    F("system.calls.transcribe.enabled", "checkbox", b(c?.transcribe?.enabled ?? false)),
                    F("system.calls.transcribe.url", "text", c?.transcribe?.url),
                    F("system.calls.transcribe.model", "text", c?.transcribe?.model),
                    F("system.calls.transcribe.language", "text", c?.transcribe?.language),
                    F("system.calls.transcribe.prompt", "textarea", c?.transcribe?.prompt)),
                G("goip",
                    F("system.goip.smtp.enabled", "checkbox", b(s.goip?.smtp?.enabled ?? false)),
                    F("system.goip.smtp.port", "number", n(s.goip?.smtp?.port))),
            };
            return new
            {
                groups,
                // Client sections present at the top; the form shows only these and offers to add the rest.
                clients = Present(s.clients?.telegram != null, s.clients?.ntfy != null),
                gateways = (s.gateways ?? new()).Where(g => !string.IsNullOrEmpty(g.id))
                    .Select(g => new GatewayItem(g.id, GatewayFields(g.id, g), g.type, g.Managed, g.modems ?? new())).ToList(),
                lines = (s.lines ?? new()).Where(ch => !string.IsNullOrEmpty(ch.name)).Select(ch => new Item(ch.name, LineFields(ch.name, ch, s),
                    ch.clients == null ? null : Present(ch.clients.telegram != null, ch.clients.ntfy != null))).ToList(),
                // For a line: what its gateway is, and the modems to choose from.
                gatewayInfo = (s.gateways ?? new()).Where(g => !string.IsNullOrEmpty(g.id)).ToDictionary(g => g.id, g => new
                {
                    g.type, managed = g.Managed, modems = (g.modems ?? new()).Select(m => m.name).ToList()
                }),
                // Lines whose phone has a secret (the secret itself is not sent).
                phoneSecrets = (s.lines ?? new()).Where(ch => !string.IsNullOrEmpty(ch.phone?.secret)).Select(ch => ch.name).ToList(),
                gatewayTemplate = GatewayFields("{key}"),
                lineTemplate = LineFields("{key}", null, s),
            };
        }

        static readonly string[] GatewayTypes = { "asterisk", "yeastar", "goip" };

        // All fields of every type: the form shows the ones the type has (and managed.* with pbx: managed),
        // so a changed type or PBX mode shows its fields at once.
        static List<Field> GatewayFields(string id, Gateway g = null)
        {
            g ??= new Gateway { id = id, type = "asterisk", amiPort = 5038 };
            string p = $"gateways[id={id}]";
            string type = g.type ?? "asterisk";
            var list = new List<Field>
            {
                F($"{p}.id", "text", g.id),
                F($"{p}.type", "select", type, GatewayTypes.Contains(type) ? GatewayTypes : GatewayTypes.Append(type).ToArray()),
                F($"{p}.ip", "text", g.ip),
                F($"{p}.ami_port", "number", n(g.amiPort)),
                F($"{p}.http_port", "number", n(g.httpPort)),
                F($"{p}.user", "text", g.user),
                F($"{p}.password", "text", g.password),
                F($"{p}.monitor", "checkbox", b(g.monitor)),
                F($"{p}.pbx", "select", g.Managed ? "managed" : "freepbx", "freepbx", "managed"),
            };
            var m = g.managed ?? new ManagedPbx();
            var sip = m.sip ?? new PbxSip();
            var f2b = m.fail2ban ?? new PbxFail2ban();
            list.AddRange(new[]
            {
                F($"{p}.managed.conf_dir", "text", m.confDir),
                F($"{p}.managed.asterisk_dir", "text", m.asteriskDir),
                F($"{p}.managed.recordings_path", "text", m.recordingsPath),
                F($"{p}.managed.sip.tls.cert", "text", sip.tls?.cert),
                F($"{p}.managed.sip.tls.key", "text", sip.tls?.key),
                F($"{p}.managed.sip.tls.port", "number", n(sip.tls?.port)),
                F($"{p}.managed.sip.udp_port", "number", n(sip.udpPort)),
                F($"{p}.managed.sip.external_address", "text", sip.externalAddress),
                F($"{p}.managed.sip.local_nets", "text", Csv(sip.localNets)),
                F($"{p}.managed.fail2ban.max_retry", "number", n(f2b.maxRetry)),
                F($"{p}.managed.fail2ban.find_minutes", "number", n(f2b.findMinutes)),
                F($"{p}.managed.fail2ban.ban_minutes", "number", n(f2b.banMinutes)),
                F($"{p}.managed.fail2ban.ban_growth", "checkbox", b(f2b.banGrowth)),
                F($"{p}.managed.fail2ban.ignore_ip", "text", Csv(f2b.ignoreIp)),
            });
            return list;
        }

        // A line on an Asterisk gateway chooses one of its modems; on yeastar / goip it has a port. Both are
        // here, and the phone; the form shows what the line's gateway needs.
        static List<Field> LineFields(string name, Channel ch, Settings s)
        {
            var gatewayIds = (s.gateways ?? new()).Select(g => g.id).Where(x => !string.IsNullOrEmpty(x)).ToArray();
            ch ??= new Channel { name = name, gateway = gatewayIds.FirstOrDefault() };
            var gw = s.gateways?.FirstOrDefault(g => g.id == ch.gateway);
            var calls = ch.calls ?? new ChannelCalls();
            string p = $"lines[name={name}]";
            var gws = gatewayIds.Contains(ch.gateway ?? "") || string.IsNullOrEmpty(ch.gateway) ? gatewayIds : gatewayIds.Append(ch.gateway).ToArray();
            var modems = new[] { "" }.Concat((gw?.modems ?? new()).Select(m => m.name)).ToList();
            if (!string.IsNullOrEmpty(ch.modem) && !modems.Contains(ch.modem)) modems.Add(ch.modem);
            return new List<Field>
            {
                F($"{p}.name", "text", ch.name),
                F($"{p}.gateway", "select", ch.gateway, gws),
                F($"{p}.modem", "select", ch.modem, modems.ToArray()),
                F($"{p}.port", "text", ch.port),
                F($"{p}.pattern", "text", ch.pattern),
                F($"{p}.number", "text", ch.number),
                F($"{p}.clients.telegram.chat_ids", "text", Csv(ch.clients?.telegram?.chatIds)),
                F($"{p}.clients.ntfy.topic", "text", ch.clients?.ntfy?.topic),
                F($"{p}.calls.voice", "checkbox", b(calls.voice)),
                F($"{p}.calls.transcript", "checkbox", b(calls.transcript)),
                F($"{p}.calls.missed", "checkbox", b(calls.missed)),
                F($"{p}.calls.chat_ids", "text", Csv(calls.chatIds)),
                // The phone (gateway with pbx: managed). The secret is not a form field: it is generated on save.
                F($"{p}.phone.extension", "text", ch.phone?.extension),
                // Empty for a line without a phone, so saving the form does not add a phone section to it.
                F($"{p}.phone.dial", "select", ch.phone == null ? "" : ch.phone.dial ?? "ru", new[] { "" }.Concat(PbxConfig.DialSets).ToArray()),
                F($"{p}.phone.encryption", "select", ch.phone == null ? "" : ch.phone.encryption ?? "sdes", new[] { "" }.Concat(PbxConfig.Encryptions).ToArray()),
            };
        }

        static string[] Present(bool telegram, bool ntfy) =>
            new[] { telegram ? "telegram" : null, ntfy ? "ntfy" : null }.Where(x => x != null).ToArray();

        // First keys of a client section added from the form (raw YAML).
        public static IList<(string key, string raw)> NewSection(string path) => path switch
        {
            "clients.telegram" => new[] { ("token", "\"\""), ("chat_ids", "[]") },
            "clients.ntfy" => new[] { ("url", "\"\""), ("token", "\"\""), ("topic", "\"\"") },
            _ when path.EndsWith(".clients.telegram") => new[] { ("chat_ids", "[]") },
            _ when path.EndsWith(".clients.ntfy") => new[] { ("topic", "\"\"") },
            _ => null
        };

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
