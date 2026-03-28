using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace CryBar.Scenario;

// Trigger XML -> XS script conversion.
// Substitutes %Param% placeholders in cmd/Extra templates with actual argument values.
// lossless=true emits @CryBar: metadata comments for perfect XML roundtripping.
public partial class ScenarioFile
{
    [GeneratedRegex(@"%(\w+)%")]
    private static partial Regex XsPlaceholderRegex();

    public static string ConvertTriggersXmlToXs(string triggersXml, bool lossless = false)
    {
        var doc = new XmlDocument();
        doc.LoadXml(triggersXml);

        // Build group id -> name map and collect group elements for lossless footer
        var groups = new Dictionary<string, string>();
        var groupElements = new List<XmlElement>();
        var groupNodes = doc.GetElementsByTagName("Group");
        for (int i = 0; i < groupNodes.Count; i++)
        {
            var g = (XmlElement)groupNodes[i]!;
            groups[g.GetAttribute("id")] = g.GetAttribute("name");
            groupElements.Add(g);
        }

        var sb = new StringBuilder();

        // Lossless header: preserve Triggers root attributes
        if (lossless)
        {
            var root = doc.DocumentElement!;
            sb.Append("// @CryBar:triggers");
            AppendAttr(sb, "version", root.GetAttribute("version"));
            AppendAttr(sb, "unk", root.GetAttribute("unk"));
            sb.AppendLine();
            sb.AppendLine();
        }

        var triggers = doc.GetElementsByTagName("Trigger");

        for (int i = 0; i < triggers.Count; i++)
        {
            var trigger = (XmlElement)triggers[i]!;
            WriteXsTriggerRule(sb, trigger, groups, lossless);
            sb.AppendLine();
        }

        // Lossless footer: emit group definitions
        if (lossless)
        {
            foreach (var g in groupElements)
            {
                sb.Append("// @CryBar:group");
                AppendAttr(sb, "id", g.GetAttribute("id"));
                AppendAttr(sb, "name", g.GetAttribute("name"));
                AppendOptionalAttr(sb, "indexes", g.GetAttribute("indexes"));
                sb.AppendLine();
            }
        }

        return IndentXs(sb.ToString());
    }

    /// <summary>
    /// Post-processes XS output to add tab indentation based on { } nesting depth.
    /// Skips braces inside string literals and comments.
    /// </summary>
    static string IndentXs(string xs)
    {
        var sb = new StringBuilder(xs.Length);
        int depth = 0;
        foreach (var rawLine in xs.Split('\n'))
        {
            var trimmed = rawLine.TrimEnd();
            if (trimmed.Length == 0) { sb.AppendLine(); continue; }

            // Check if line starts with } (after trimming) to decrease depth first
            var stripped = trimmed.TrimStart();
            if (stripped.StartsWith('}')) depth--;
            if (depth < 0) depth = 0;

            for (int i = 0; i < depth; i++) sb.Append('\t');
            sb.AppendLine(stripped);

            // Count net brace change (skipping strings/comments) for next line
            int delta = 0;
            CountBraces(stripped, ref delta);
            // We already handled the leading }, so only apply opens
            if (stripped.StartsWith('}'))
                depth += delta + 1; // +1 to compensate for the pre-decrement
            else
                depth += delta;
            if (depth < 0) depth = 0;
        }
        return sb.ToString();
    }

    static void WriteXsTriggerRule(StringBuilder sb, XmlElement trigger, Dictionary<string, string> groups, bool lossless)
    {
        var name = trigger.GetAttribute("name");
        var ruleName = "_" + name;
        var active = trigger.GetAttribute("active") == "1";
        var loop = trigger.GetAttribute("loop") == "1";
        var groupId = trigger.GetAttribute("group");

        // Lossless: emit trigger metadata comment
        if (lossless)
        {
            sb.Append("// @CryBar:trigger");
            AppendAttr(sb, "id", trigger.GetAttribute("id"));
            AppendAttr(sb, "group", groupId);
            AppendAttr(sb, "priority", trigger.GetAttribute("priority"));
            AppendAttr(sb, "unk", trigger.GetAttribute("unk"));
            AppendAttr(sb, "loop", trigger.GetAttribute("loop"));
            AppendAttr(sb, "active", trigger.GetAttribute("active"));
            AppendAttr(sb, "runImm", trigger.GetAttribute("runImm"));
            AppendOptionalAttr(sb, "flag3", trigger.GetAttribute("flag3"));
            AppendOptionalAttr(sb, "flag4", trigger.GetAttribute("flag4"));
            AppendOptionalAttr(sb, "note", trigger.GetAttribute("note"));
            sb.AppendLine();
        }

        sb.AppendLine($"rule {ruleName}");
        if (groups.TryGetValue(groupId, out var groupName) && !string.IsNullOrEmpty(groupName))
            sb.AppendLine($"group {groupName}");
        sb.AppendLine("highFrequency");
        if (active) sb.AppendLine("active");
        sb.AppendLine("{");

        var conds = new List<XmlElement>();
        var effects = new List<XmlElement>();
        foreach (XmlNode child in trigger.ChildNodes)
        {
            if (child is not XmlElement elem) continue;
            if (elem.Name == "Cond") conds.Add(elem);
            else if (elem.Name == "Effect") effects.Add(elem);
        }

        // Lossless: emit condition metadata before the condition expression
        if (lossless)
        {
            foreach (var cond in conds)
                EmitConditionMetadata(sb, cond);
        }

        var condExpr = BuildXsConditionExpression(conds);
        var hasCondition = condExpr != null;

        if (hasCondition)
        {
            sb.AppendLine($"   if ({condExpr})");
            sb.AppendLine("   {");
        }

        var indent = "      ";
        bool closesRuleScope = false;

        foreach (var effect in effects)
        {
            var (code, isRawBlock) = BuildXsEffectCall(effect);
            if (code == null) continue;

            if (isRawBlock)
            {
                // Lossless: for raw blocks, emit effect/arg metadata but skip @CryBar:extra
                // (the raw code lines ARE the data - no need to duplicate them as comments)
                if (lossless)
                    EmitEffectMetadata(sb, effect, skipExtras: true);

                // If the raw block starts with "}" it closes the rule scope
                // (e.g. trigger loader library that manages its own braces)
                if (code.TrimStart().StartsWith('}'))
                    closesRuleScope = true;

                foreach (var line in code.Split('\n'))
                    sb.AppendLine(line);
            }
            else
            {
                // Lossless: emit full metadata (including @CryBar:extra template)
                if (lossless)
                    EmitEffectMetadata(sb, effect, skipExtras: false);

                sb.AppendLine($"{indent}{code}");
            }
        }

        // Skip auto-disable when raw code closes the rule scope (manages its own rules)
        if (!loop && !closesRuleScope)
        {
            sb.AppendLine($"{indent}xsDisableRule(\"{ruleName}\");");
            sb.AppendLine($"{indent}trDisableRule(\"{name}\");");
        }

        if (hasCondition)
            sb.AppendLine("   }");

        sb.AppendLine("}");
    }

    /// <summary>
    /// Emits // @CryBar:cond and // @CryBar:arg comments for a condition element.
    /// Attribute order matches the original XML: name, type, cmd, trail.
    /// Conditions may also have Extra children, which are emitted after args.
    /// </summary>
    static void EmitConditionMetadata(StringBuilder sb, XmlElement cond)
    {
        sb.Append("// @CryBar:cond");
        AppendAttr(sb, "name", cond.GetAttribute("name"));
        var type = cond.GetAttribute("type");
        if (!string.IsNullOrEmpty(type) && type != cond.GetAttribute("name"))
            AppendAttr(sb, "type", type);
        AppendAttr(sb, "cmd", cond.GetAttribute("cmd"));
        AppendOptionalAttr(sb, "trail", cond.GetAttribute("trail"));
        sb.AppendLine();

        EmitArgMetadata(sb, cond);
        EmitExtraMetadata(sb, cond);
    }

    /// <summary>
    /// Emits // @CryBar:effect, // @CryBar:arg, and optionally // @CryBar:extra comments.
    /// When skipExtras=true (raw blocks), Extra lines are omitted since the code itself is the data.
    /// </summary>
    static void EmitEffectMetadata(StringBuilder sb, XmlElement effect, bool skipExtras)
    {
        sb.Append("// @CryBar:effect");
        AppendAttr(sb, "name", effect.GetAttribute("name"));
        var type = effect.GetAttribute("type");
        if (!string.IsNullOrEmpty(type) && type != effect.GetAttribute("name"))
            AppendAttr(sb, "type", type);
        AppendAttr(sb, "cmd", effect.GetAttribute("cmd"));
        AppendOptionalAttr(sb, "trail", effect.GetAttribute("trail"));
        sb.AppendLine();

        EmitArgMetadata(sb, effect);
        if (!skipExtras)
            EmitExtraMetadata(sb, effect);
    }

    /// <summary>
    /// Emits // @CryBar:extra comments for all Extra children of a parent element (Cond or Effect).
    /// </summary>
    static void EmitExtraMetadata(StringBuilder sb, XmlElement parent)
    {
        foreach (XmlNode child in parent.ChildNodes)
        {
            if (child is not XmlElement elem || elem.Name != "Extra") continue;

            // Check for complex Extra (with has/cmd attributes and S children)
            var hasAttr = elem.GetAttribute("has");
            var cmdAttr = elem.GetAttribute("cmd");
            var sChildren = new List<string>();
            foreach (XmlNode sc in elem.ChildNodes)
            {
                if (sc is XmlElement se && se.Name == "S")
                    sChildren.Add(se.InnerText);
            }

            if (!string.IsNullOrEmpty(cmdAttr) && sChildren.Count > 0)
            {
                // Complex Extra: emit as structured metadata
                sb.Append("// @CryBar:extra");
                AppendOptionalAttr(sb, "has", hasAttr);
                AppendAttr(sb, "cmd", cmdAttr);
                sb.Append($" scount=\"{sChildren.Count}\"");
                sb.AppendLine();
                foreach (var s in sChildren)
                {
                    sb.Append("// @CryBar:s ");
                    sb.AppendLine(Q(s));
                }
            }
            else if (!string.IsNullOrEmpty(hasAttr) || !string.IsNullOrEmpty(cmdAttr))
            {
                // Extra with attributes but no S children
                sb.Append("// @CryBar:extra");
                AppendOptionalAttr(sb, "has", hasAttr);
                if (!string.IsNullOrEmpty(cmdAttr))
                    AppendAttr(sb, "cmd", cmdAttr);
                sb.Append($" text={Q(elem.InnerText)}");
                sb.AppendLine();
            }
            else
            {
                // Simple Extra: just text content
                sb.Append("// @CryBar:extra ");
                sb.AppendLine(elem.InnerText);
            }
        }
    }

    /// <summary>
    /// Emits // @CryBar:arg comments for all Arg children of a parent element.
    /// For args with &lt;V&gt; children, emits separate // @CryBar:v lines to preserve structure.
    /// </summary>
    static void EmitArgMetadata(StringBuilder sb, XmlElement parent)
    {
        foreach (XmlNode child in parent.ChildNodes)
        {
            if (child is not XmlElement elem || elem.Name != "Arg") continue;

            sb.Append("// @CryBar:arg");
            var key = elem.GetAttribute("key");
            AppendAttr(sb, "key", key);
            // Emit name if the attribute exists in the XML, even when empty
            if (elem.HasAttribute("name"))
                AppendAttr(sb, "name", elem.GetAttribute("name"));
            AppendAttr(sb, "kt", elem.GetAttribute("kt"));
            AppendAttr(sb, "vt", elem.GetAttribute("vt"));
            // Emit magic/flag if present as XML attributes, even when empty
            if (elem.HasAttribute("magic"))
                AppendAttr(sb, "magic", elem.GetAttribute("magic"));
            if (elem.HasAttribute("flag"))
                AppendAttr(sb, "flag", elem.GetAttribute("flag"));

            // Check for <V> children (used by vt=4, 22, 42, 43, 50)
            var vChildren = new List<string>();
            foreach (XmlNode argChild in elem.ChildNodes)
            {
                if (argChild is XmlElement ve && ve.Name == "V")
                    vChildren.Add(ve.InnerText);
            }

            if (vChildren.Count > 0)
            {
                // Mark that this arg uses V children (value will follow as @CryBar:v lines)
                sb.Append(" vcount=\"");
                sb.Append(vChildren.Count);
                sb.Append('"');
                sb.AppendLine();
                // Emit each V child as a separate line
                foreach (var v in vChildren)
                {
                    sb.Append("// @CryBar:v ");
                    sb.AppendLine(Q(v));
                }
            }
            else if (!elem.IsEmpty)
            {
                // Element has explicit content (even if empty text) - preserve with value
                var directText = GetDirectText(elem);
                sb.Append($" value={Q(directText)}");
                sb.AppendLine();
            }
            else
            {
                // Self-closing element with no content - omit value key
                sb.AppendLine();
            }
        }
    }

    /// <summary>
    /// Gets the direct text content of an element, excluding child element text.
    /// For elements with only text nodes, this is equivalent to InnerText.
    /// For elements with V children, this returns only the non-V text.
    /// </summary>
    static string GetDirectText(XmlElement elem)
    {
        var sb = new StringBuilder();
        foreach (XmlNode child in elem.ChildNodes)
        {
            if (child is XmlText text)
                sb.Append(text.Value);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Appends a key="value" attribute pair to the metadata comment.
    /// Always emits, even if value is empty.
    /// </summary>
    static void AppendAttr(StringBuilder sb, string key, string value)
    {
        sb.Append($" {key}={Q(value)}");
    }

    /// <summary>
    /// Appends a key="value" attribute pair only when value is non-empty.
    /// </summary>
    static void AppendOptionalAttr(StringBuilder sb, string key, string value)
    {
        if (!string.IsNullOrEmpty(value))
            sb.Append($" {key}={Q(value)}");
    }

    /// <summary>
    /// Quotes a value for metadata comments. Escapes internal quotes, backslashes, and newlines.
    /// </summary>
    static string Q(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")}\"";

    static string? BuildXsConditionExpression(List<XmlElement> conds)
    {
        var parts = new List<string>();

        foreach (var cond in conds)
        {
            var cmd = cond.GetAttribute("cmd");
            if (string.IsNullOrEmpty(cmd) || cmd == "true") continue;

            var args = CollectXsArgs(cond);
            var expr = SubstituteXsPlaceholders(cmd, args);
            parts.Add($"({expr})");
        }

        if (parts.Count == 0) return null;
        return string.Join(" && ", parts);
    }

    /// <summary>
    /// Builds XS code from an effect's Extra elements.
    /// Returns (code, isRawBlock) where isRawBlock=true means the code contains
    /// raw XS with its own scoping (e.g. trigger loader library) and should be emitted verbatim.
    /// </summary>
    static (string? code, bool isRawBlock) BuildXsEffectCall(XmlElement effect)
    {
        var extras = new List<string>();
        foreach (XmlNode child in effect.ChildNodes)
        {
            if (child is XmlElement elem && elem.Name == "Extra")
                extras.Add(elem.InnerText);
        }

        if (extras.Count == 0) return (null, false);

        var args = CollectXsArgs(effect);

        // Raw code block: multiple extras where the first starts with "}"
        // (e.g. trigger loader library that closes the rule scope and injects code)
        // Other multi-Extra effects (e.g. structured extras with cmd/S children) are NOT raw blocks
        if (extras.Count > 1 && extras[0].TrimStart().StartsWith('}'))
        {
            var raw = string.Join("\n", extras);
            return (SubstituteXsPlaceholders(raw, args), true);
        }

        var template = extras[0];
        if (string.IsNullOrWhiteSpace(template)) return (null, false);

        return (SubstituteXsPlaceholders(template, args), false);
    }

    static Dictionary<string, string> CollectXsArgs(XmlElement parent)
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (XmlNode child in parent.ChildNodes)
        {
            if (child is not XmlElement elem || elem.Name != "Arg") continue;
            var key = elem.GetAttribute("key");
            if (!string.IsNullOrEmpty(key))
                args[key] = elem.InnerText;
        }
        return args;
    }

    static string SubstituteXsPlaceholders(string template, Dictionary<string, string> args)
    {
        return XsPlaceholderRegex().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            return args.TryGetValue(key, out var value) ? value : match.Value;
        });
    }
}
