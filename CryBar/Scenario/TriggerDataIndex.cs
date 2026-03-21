using CryBar.Bar;
using CryBar.Utilities;
using System.Text.RegularExpressions;
using System.Xml;

namespace CryBar.Scenario;

/// <summary>
/// Loads and indexes the trigger_data.xml template database from Data.bar.
/// Provides definitions for all trigger conditions and effects, including
/// regex patterns built from their expression/command templates for matching
/// XS code back to trigger definitions.
/// </summary>
public class TriggerDataIndex
{
    public record TriggerParam(string Name, string DispName, string VarType, string Default);
    public record ConditionDef(string Name, string? Command, string Expression, TriggerParam[] Params, Regex? ExpressionPattern);
    public record EffectDef(string Name, string? Command, string ExtraTemplate, TriggerParam[] Params, Regex? ExtraPattern);

    public Dictionary<string, ConditionDef> Conditions { get; }
    public Dictionary<string, EffectDef> Effects { get; }

    TriggerDataIndex(Dictionary<string, ConditionDef> conditions, Dictionary<string, EffectDef> effects)
    {
        Conditions = conditions;
        Effects = effects;
    }

    /// <summary>
    /// Loads the trigger data index from the game's root folder by finding Data.bar,
    /// extracting trigger_data.xml.XMB, and parsing it.
    /// </summary>
    /// <param name="rootFolder">The game root folder (e.g. "game" directory).</param>
    /// <returns>A populated TriggerDataIndex, or null if loading failed.</returns>
    public static TriggerDataIndex? Load(string rootFolder)
    {
        try
        {
            // Try the direct path first
            var barPath = Path.Combine(rootFolder, "data", "Data.bar");
            if (!File.Exists(barPath))
            {
                // Search immediate subdirectories
                barPath = null;
                if (Directory.Exists(rootFolder))
                {
                    foreach (var subDir in Directory.GetDirectories(rootFolder))
                    {
                        var candidate = Path.Combine(subDir, "Data.bar");
                        if (File.Exists(candidate))
                        {
                            barPath = candidate;
                            break;
                        }
                    }
                }
                if (barPath == null) return null;
            }

            using var stream = File.OpenRead(barPath);
            var bar = new BarFile(stream);
            if (!bar.Load(out _)) return null;

            // Find trigger_data.xml.XMB entry
            var entry = bar.Entries!.FirstOrDefault(e =>
                e.Name.Contains("trigger_data.xml.XMB", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;

            // Read, decompress, and convert to XML text
            var raw = entry.ReadDataRaw(stream);
            var decompressed = BarCompression.EnsureDecompressed(raw, out _);
            var xmlText = ConversionHelper.ConvertXmbToXmlText(decompressed.Span);
            if (xmlText == null) return null;

            return ParseTriggerDataXml(xmlText);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses trigger_data XML text into condition and effect definitions using XmlReader.
    /// </summary>
    static TriggerDataIndex? ParseTriggerDataXml(string xmlText)
    {
        var conditions = new Dictionary<string, ConditionDef>(StringComparer.Ordinal);
        var effects = new Dictionary<string, EffectDef>(StringComparer.Ordinal);

        try
        {
            var settings = new XmlReaderSettings { IgnoreWhitespace = true };
            using var reader = XmlReader.Create(new StringReader(xmlText), settings);

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;

                if (reader.Name == "condition")
                    ReadConditionDef(reader, conditions);
                else if (reader.Name == "effect")
                    ReadEffectDef(reader, effects);
            }
        }
        catch
        {
            return null;
        }

        return new TriggerDataIndex(conditions, effects);
    }

    static void ReadConditionDef(XmlReader reader, Dictionary<string, ConditionDef> conditions)
    {
        var name = reader.GetAttribute("name") ?? "";
        if (string.IsNullOrEmpty(name)) { reader.Skip(); return; }

        string expression = "";
        string? command = null;
        var parameters = new List<TriggerParam>();

        if (!reader.IsEmptyElement)
        {
            while (reader.Read())
            {
                process:
                if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "condition") break;
                if (reader.NodeType != XmlNodeType.Element) continue;

                switch (reader.Name)
                {
                    case "expression":
                        expression = ReadTextElement(reader);
                        goto process;
                    case "command":
                        command = ReadTextElement(reader);
                        goto process;
                    case "param":
                        ReadParam(reader, parameters);
                        goto process;
                    default:
                        reader.Skip();
                        goto process;
                }
            }
        }

        var paramsArr = parameters.ToArray();
        Regex? pattern = null;
        if (!string.IsNullOrEmpty(expression))
            pattern = BuildPatternFromTemplate(expression, paramsArr);

        conditions[name] = new ConditionDef(name, command, expression, paramsArr, pattern);
    }

    static void ReadEffectDef(XmlReader reader, Dictionary<string, EffectDef> effects)
    {
        var name = reader.GetAttribute("name") ?? "";
        if (string.IsNullOrEmpty(name)) { reader.Skip(); return; }

        string? command = null;
        var parameters = new List<TriggerParam>();

        if (!reader.IsEmptyElement)
        {
            while (reader.Read())
            {
                process:
                if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "effect") break;
                if (reader.NodeType != XmlNodeType.Element) continue;

                switch (reader.Name)
                {
                    case "command":
                        command = ReadTextElement(reader);
                        goto process;
                    case "param":
                        ReadParam(reader, parameters);
                        goto process;
                    default:
                        reader.Skip();
                        goto process;
                }
            }
        }

        var paramsArr = parameters.ToArray();
        var extraTemplate = command ?? "";

        Regex? pattern = null;
        if (!string.IsNullOrEmpty(command))
            pattern = BuildPatternFromTemplate(command, paramsArr);

        effects[name] = new EffectDef(name, command, extraTemplate, paramsArr, pattern);
    }

    /// <summary>
    /// Reads a simple text element's content and advances past it.
    /// After this call, the reader is positioned at the next sibling or parent's end element.
    /// </summary>
    static string ReadTextElement(XmlReader reader)
    {
        if (reader.IsEmptyElement) { reader.Skip(); return ""; }
        return reader.ReadElementContentAsString();
    }

    static void ReadParam(XmlReader reader, List<TriggerParam> parameters)
    {
        var name = reader.GetAttribute("name") ?? "";
        var dispName = reader.GetAttribute("dispname") ?? "";
        var varType = reader.GetAttribute("vartype") ?? "";
        var defaultVal = ReadTextElement(reader);
        parameters.Add(new TriggerParam(name, dispName, varType, defaultVal));
    }

    /// <summary>
    /// Builds a regex pattern from a template string (expression or command) by replacing
    /// %ParamName% placeholders with appropriate capture groups based on vartype.
    /// </summary>
    static Regex? BuildPatternFromTemplate(string template, TriggerParam[] parameters)
    {
        try
        {
            // Escape the entire template for regex
            var escaped = Regex.Escape(template);

            // Replace each %ParamName% with the appropriate capture group.
            // After Regex.Escape, % is not special so %ParamName% stays as-is.
            foreach (var p in parameters)
            {
                var placeholder = $"%{p.Name}%";
                var capturePattern = GetCapturePatternForVarType(p.VarType, p.Name);
                escaped = escaped.Replace(placeholder, capturePattern);
            }

            // Wrap with anchors
            var fullPattern = $"^{escaped}$";

            return new Regex(fullPattern, RegexOptions.Compiled);
        }
        catch
        {
            // If pattern building fails, return null - this condition/effect won't be matchable
            return null;
        }
    }

    /// <summary>
    /// Returns the regex capture group pattern for a given vartype and parameter name.
    /// </summary>
    static string GetCapturePatternForVarType(string varType, string paramName)
    {
        return varType switch
        {
            "player" or "long" or "tech" or "difficulty" or "status" or "techstatus"
                => $"(?<{paramName}>[-\\d]+)",

            "float"
                => $"(?<{paramName}>[-\\d.]+)",

            "bool"
                => $"(?<{paramName}>true|false)",

            "string" or "protounit" or "group"
                => $"(?<{paramName}>[^\"]*)",

            "operator"
                => $"(?<{paramName}>[<>=!]+)",

            "area" or "unit"
                => $"(?<{paramName}>[-\\d.]+(?:,\\s*[-\\d.]+)*)",

            // Default: match non-whitespace
            _ => $"(?<{paramName}>\\S+)",
        };
    }
}
