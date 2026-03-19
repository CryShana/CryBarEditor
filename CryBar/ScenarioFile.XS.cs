using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace CryBar;

/// <summary>
/// Converts trigger XML (from TriggerFile/ScenarioFile) to XS script format.
/// The conversion substitutes %Param% placeholders in cmd/Extra templates
/// with actual argument values from the trigger definition.
/// </summary>
public partial class ScenarioFile
{
    [GeneratedRegex(@"%(\w+)%")]
    private static partial Regex XsPlaceholderRegex();

    public static string ConvertTriggersXmlToXs(string triggersXml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(triggersXml);

        // Build group id → name map
        var groups = new Dictionary<string, string>();
        var groupNodes = doc.GetElementsByTagName("Group");
        for (int i = 0; i < groupNodes.Count; i++)
        {
            var g = (XmlElement)groupNodes[i]!;
            groups[g.GetAttribute("id")] = g.GetAttribute("name");
        }

        var sb = new StringBuilder();
        var triggers = doc.GetElementsByTagName("Trigger");

        for (int i = 0; i < triggers.Count; i++)
        {
            var trigger = (XmlElement)triggers[i]!;
            WriteXsTriggerRule(sb, trigger, groups);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    static void WriteXsTriggerRule(StringBuilder sb, XmlElement trigger, Dictionary<string, string> groups)
    {
        var name = trigger.GetAttribute("name");
        var ruleName = "_" + name;
        var active = trigger.GetAttribute("active") == "1";
        var loop = trigger.GetAttribute("loop") == "1";
        var groupId = trigger.GetAttribute("group");

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
                // If the raw block starts with "}" it closes the rule scope
                // (e.g. trigger loader library that manages its own braces)
                if (code.TrimStart().StartsWith('}'))
                    closesRuleScope = true;

                foreach (var line in code.Split('\n'))
                    sb.AppendLine(line);
            }
            else
            {
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

        // Multiple <Extra> elements = raw code block (e.g. trigger loader library)
        // These manage their own scoping and should be emitted verbatim
        if (extras.Count > 1)
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
