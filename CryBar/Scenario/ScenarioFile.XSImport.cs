using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace CryBar.Scenario;

// XS trigger script -> trigger XML conversion (reverse of ConvertTriggersXmlToXs).
// With @CryBar: metadata (lossless mode), reconstructs full typed XML.
// Without metadata, falls back to <Extra> blocks.
public partial class ScenarioFile
{
    [GeneratedRegex(@"^rule\s+(\S+)\s*$")]
    private static partial Regex XsRuleRegex();

    [GeneratedRegex(@"^group\s+(.+)$")]
    private static partial Regex XsGroupRegex();

    [GeneratedRegex(@"^// @CryBar:(\w+)\s*(.*)$")]
    private static partial Regex CryBarCommentRegex();

    enum XsParseState
    {
        OutsideRule,
        InHeader,
        InBody
    }

    sealed class ParsedTrigger
    {
        public string Name = "";
        public string GroupName = "";
        public bool Active;
        public bool RunImmediately;
        public List<string> BodyLines = [];
        public List<string> TrailingLines = [];

        /// <summary>
        /// When @CryBar:trigger metadata is found, stores the parsed attributes.
        /// Keys: id, group, priority, unk, loop, active, runImm, flag3, flag4, note
        /// </summary>
        public Dictionary<string, string>? CryBarTriggerAttrs;
    }

    /// <summary>
    /// File-level @CryBar metadata parsed from XS input.
    /// </summary>
    sealed class CryBarFileMetadata
    {
        /// <summary>Attributes from @CryBar:triggers header (version, unk).</summary>
        public Dictionary<string, string>? TriggersAttrs;

        /// <summary>
        /// @CryBar:group definitions from the file footer.
        /// Each entry is a dict with id, name, indexes.
        /// </summary>
        public List<Dictionary<string, string>>? Groups;
    }

    /// <summary>
    /// Parses XS trigger script text into trigger XML in the same format
    /// as <see cref="SectionToTriggersXml"/> produces.
    /// </summary>
    /// <param name="xsText">The XS script text to parse.</param>
    /// <param name="triggerData">Optional trigger data index for template matching.</param>
    /// <param name="sourceDir">Optional directory for resolving <c>include "file.xs";</c> directives.</param>
    /// <returns>A trigger XML string with Triggers root element.</returns>
    public static string ParseXsToTriggersXml(string xsText, TriggerDataIndex? triggerData = null, string? sourceDir = null)
    {
        // Resolve include directives before parsing
        if (!string.IsNullOrEmpty(sourceDir))
            xsText = ResolveIncludes(xsText, sourceDir);

        var fileMeta = new CryBarFileMetadata();
        var triggers = ParseXsRules(xsText, fileMeta);
        bool hasLosslessMeta = fileMeta.TriggersAttrs != null;

        // Build group map: group name -> (id, list of trigger indices)
        // Only used when no @CryBar:group metadata is present
        var groupMap = new Dictionary<string, (int id, List<int> triggerIndices)>(StringComparer.Ordinal);
        int nextGroupId = 0;

        for (int i = 0; i < triggers.Count; i++)
        {
            var t = triggers[i];
            var gn = string.IsNullOrEmpty(t.GroupName) ? "Ungrouped" : t.GroupName;

            if (!groupMap.TryGetValue(gn, out var groupInfo))
            {
                groupInfo = (nextGroupId++, []);
                groupMap[gn] = groupInfo;
            }
            groupInfo.triggerIndices.Add(i);
        }

        // Write XML
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "\t",
            OmitXmlDeclaration = false,
            NewLineHandling = NewLineHandling.Entitize
        };

        var sb = new StringBuilder(1024);
        using var writer = XmlWriter.Create(sb, settings);
        writer.WriteStartDocument();

        writer.WriteStartElement("Triggers");
        var trigVersion = fileMeta.TriggersAttrs?.GetValueOrDefault("version") ?? "11";
        var trigUnk = fileMeta.TriggersAttrs?.GetValueOrDefault("unk") ?? "2,172,1";
        writer.WriteAttributeString("version", trigVersion);
        writer.WriteAttributeString("unk", trigUnk);

        for (int i = 0; i < triggers.Count; i++)
        {
            var t = triggers[i];
            var gn = string.IsNullOrEmpty(t.GroupName) ? "Ungrouped" : t.GroupName;
            var groupId = groupMap.GetValueOrDefault(gn).id;

            // Determine loop/active from @CryBar metadata or inference
            bool loop;
            bool active;
            if (t.CryBarTriggerAttrs != null)
            {
                loop = t.CryBarTriggerAttrs.GetValueOrDefault("loop") == "1";
                active = t.CryBarTriggerAttrs.GetValueOrDefault("active") == "1";
            }
            else
            {
                loop = IsLoopingTrigger(t);
                active = t.Active;
            }

            writer.WriteStartElement("Trigger");

            if (t.CryBarTriggerAttrs != null)
            {
                // Use exact metadata from @CryBar:trigger
                writer.WriteAttributeString("name", t.Name);
                writer.WriteAttributeString("id", t.CryBarTriggerAttrs.GetValueOrDefault("id", i.ToString()));
                writer.WriteAttributeString("group", t.CryBarTriggerAttrs.GetValueOrDefault("group", groupId.ToString()));
                writer.WriteAttributeString("priority", t.CryBarTriggerAttrs.GetValueOrDefault("priority", "4"));
                writer.WriteAttributeString("unk", t.CryBarTriggerAttrs.GetValueOrDefault("unk", "-1"));
                writer.WriteAttributeString("loop", t.CryBarTriggerAttrs.GetValueOrDefault("loop", loop ? "1" : "0"));
                writer.WriteAttributeString("active", t.CryBarTriggerAttrs.GetValueOrDefault("active", active ? "1" : "0"));
                writer.WriteAttributeString("runImm", t.CryBarTriggerAttrs.GetValueOrDefault("runImm", "0"));
                WriteOptionalAttr(writer, "flag3", t.CryBarTriggerAttrs.GetValueOrDefault("flag3"));
                WriteOptionalAttr(writer, "flag4", t.CryBarTriggerAttrs.GetValueOrDefault("flag4"));
                WriteOptionalAttr(writer, "note", t.CryBarTriggerAttrs.GetValueOrDefault("note"));
            }
            else
            {
                writer.WriteAttributeString("name", t.Name);
                writer.WriteAttributeString("id", i.ToString());
                writer.WriteAttributeString("group", groupId.ToString());
                writer.WriteAttributeString("priority", "4");
                writer.WriteAttributeString("unk", "-1");
                writer.WriteAttributeString("loop", loop ? "1" : "0");
                writer.WriteAttributeString("active", active ? "1" : "0");
                writer.WriteAttributeString("runImm", t.RunImmediately ? "1" : "0");
            }

            // Build body lines for the effect, stripping auto-disable calls if not looping
            var effectLines = GetEffectBodyLines(t, loop);

            // Combine effect body lines with trailing between-rule lines
            var allExtraLines = new List<string>(effectLines.Count + t.TrailingLines.Count);
            allExtraLines.AddRange(effectLines);
            allExtraLines.AddRange(t.TrailingLines);

            // Strip auto-disable calls from anywhere in the body (they may be inside if-blocks).
            // The loop attribute already captures this information.
            if (!loop)
            {
                var ruleName = "_" + t.Name;
                allExtraLines.RemoveAll(line =>
                    line == $"xsDisableRule(\"{ruleName}\");" ||
                    line == $"trDisableRule(\"{t.Name}\");");
            }

            if (hasLosslessMeta)
            {
                // Lossless path: parse @CryBar comments from body lines
                WriteLosslessBodyElements(writer, allExtraLines);
            }
            else if (triggerData != null)
            {
                // Template matching path: try to reconstruct structured XML from trigger data templates
                WriteTemplateMatchedElements(writer, allExtraLines, triggerData);
            }
            else
            {
                // Fallback path: all lines become <Extra> blocks in a single effect
                WriteFallbackExtraElements(writer, allExtraLines);
            }

            writer.WriteEndElement(); // Trigger
        }

        // Write groups
        if (fileMeta.Groups != null)
        {
            // Lossless: use exact @CryBar:group definitions
            foreach (var g in fileMeta.Groups)
            {
                writer.WriteStartElement("Group");
                writer.WriteAttributeString("id", g.GetValueOrDefault("id", "0"));
                writer.WriteAttributeString("name", g.GetValueOrDefault("name", "Ungrouped"));
                WriteOptionalAttr(writer, "indexes", g.GetValueOrDefault("indexes"));
                writer.WriteEndElement();
            }
        }
        else
        {
            // Fallback: auto-reconstructed groups
            foreach (var (name, (id, triggerIndices)) in groupMap)
            {
                writer.WriteStartElement("Group");
                writer.WriteAttributeString("id", id.ToString());
                writer.WriteAttributeString("name", name);
                writer.WriteAttributeString("indexes", string.Join(",", triggerIndices));
                writer.WriteEndElement();
            }
        }

        writer.WriteEndElement(); // Triggers
        writer.WriteEndDocument();
        writer.Flush();

        return sb.ToString();
    }

    /// <summary>
    /// Resolves <c>include "file.xs";</c> directives by inlining the referenced file contents.
    /// Recurses into included files. If a file is not found, the include line is left as-is.
    /// Tracks visited files to prevent infinite include cycles.
    /// </summary>
    static string ResolveIncludes(string xsText, string sourceDir, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var regex = XStoRM.GetIncludeRgx();
        return regex.Replace(xsText, match =>
        {
            var relativePath = match.Groups[1].Value;
            var fullPath = Path.GetFullPath(Path.Combine(sourceDir, relativePath));

            if (!File.Exists(fullPath) || !visited.Add(fullPath))
                return match.Value; // file not found or circular include - leave as-is

            var includedText = File.ReadAllText(fullPath);
            var includeDir = Path.GetDirectoryName(fullPath) ?? sourceDir;
            return ResolveIncludes(includedText, includeDir, visited);
        });
    }

    /// <summary>
    /// Writes an XML attribute only if the value is non-null and non-empty.
    /// </summary>
    static void WriteOptionalAttr(XmlWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            writer.WriteAttributeString(name, value);
    }

    /// <summary>
    /// Processes body lines containing @CryBar: metadata comments and writes structured
    /// Cond/Effect XML elements instead of raw Extra blocks.
    /// </summary>
    static void WriteLosslessBodyElements(XmlWriter writer, List<string> bodyLines)
    {
        int i = 0;
        while (i < bodyLines.Count)
        {
            var line = bodyLines[i];
            var cbMatch = CryBarCommentRegex().Match(line);

            if (!cbMatch.Success)
            {
                // Not a @CryBar comment - skip structural lines (if/braces)
                // and emit non-structural lines as Extra in a fallback effect
                if (!IsStructuralLine(line))
                {
                    // Emit as a single-line Extra in a wrapper effect
                    writer.WriteStartElement("Effect");
                    writer.WriteAttributeString("name", "");
                    writer.WriteAttributeString("cmd", "true");
                    writer.WriteStartElement("Extra");
                    writer.WriteString(line);
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                }
                i++;
                continue;
            }

            var commentType = cbMatch.Groups[1].Value;
            var commentBody = cbMatch.Groups[2].Value;

            switch (commentType)
            {
                case "cond":
                {
                    // Parse @CryBar:cond + subsequent @CryBar:arg/v/extra/s lines + consume the if(...) line
                    var condAttrs = ParseCryBarAttrs(commentBody);
                    var condExtras = new List<ParsedExtra>();
                    i++;

                    // Collect subsequent @CryBar:arg (and @CryBar:v) and @CryBar:extra (and @CryBar:s) lines
                    var args = CollectArgAndExtraMetadata(bodyLines, ref i, condExtras);

                    // Consume the actual if(...) expression line (and any associated code from extras)
                    if (i < bodyLines.Count && !bodyLines[i].StartsWith("// @CryBar:", StringComparison.Ordinal))
                    {
                        i++; // skip the resolved if(...) line
                    }

                    // Write <Cond> element - attribute order: name, type, cmd, trail
                    writer.WriteStartElement("Cond");
                    writer.WriteAttributeString("name", condAttrs.GetValueOrDefault("name", ""));
                    var condType = condAttrs.GetValueOrDefault("type");
                    if (!string.IsNullOrEmpty(condType))
                        writer.WriteAttributeString("type", condType);
                    writer.WriteAttributeString("cmd", condAttrs.GetValueOrDefault("cmd", "true"));
                    WriteOptionalAttr(writer, "trail", condAttrs.GetValueOrDefault("trail"));

                    // Write <Arg> children
                    foreach (var arg in args)
                        WriteArgElement(writer, arg);

                    // Write <Extra> children (conditions can have extras too)
                    foreach (var extra in condExtras)
                        WriteExtraFromParsed(writer, extra);

                    writer.WriteEndElement(); // Cond
                    break;
                }

                case "effect":
                {
                    // Parse @CryBar:effect + subsequent @CryBar:arg/v + @CryBar:extra/s lines + consume code lines
                    var effectAttrs = ParseCryBarAttrs(commentBody);
                    var extras = new List<ParsedExtra>();
                    i++;

                    // Collect subsequent @CryBar:arg (and @CryBar:v) and @CryBar:extra (and @CryBar:s) lines
                    var args = CollectArgAndExtraMetadata(bodyLines, ref i, extras);

                    // Consume all resolved code lines (everything until next @CryBar: comment or end).
                    var consumedCodeLines = new List<string>();
                    while (i < bodyLines.Count && !bodyLines[i].StartsWith("// @CryBar:", StringComparison.Ordinal))
                    {
                        consumedCodeLines.Add(bodyLines[i]);
                        i++;
                    }

                    // Write <Effect> element - attribute order: name, type, cmd, trail
                    writer.WriteStartElement("Effect");
                    writer.WriteAttributeString("name", effectAttrs.GetValueOrDefault("name", ""));
                    var effectType = effectAttrs.GetValueOrDefault("type");
                    if (!string.IsNullOrEmpty(effectType))
                        writer.WriteAttributeString("type", effectType);
                    writer.WriteAttributeString("cmd", effectAttrs.GetValueOrDefault("cmd", "true"));
                    WriteOptionalAttr(writer, "trail", effectAttrs.GetValueOrDefault("trail"));

                    // Write <Arg> children
                    foreach (var arg in args)
                        WriteArgElement(writer, arg);

                    if (extras.Count > 0)
                    {
                        // Write <Extra> children from parsed @CryBar:extra metadata
                        foreach (var extra in extras)
                            WriteExtraFromParsed(writer, extra);
                    }
                    else
                    {
                        // No @CryBar:extra comments (raw block) - consumed code lines become <Extra> elements
                        foreach (var codeLine in consumedCodeLines)
                        {
                            writer.WriteStartElement("Extra");
                            writer.WriteString(codeLine);
                            writer.WriteEndElement();
                        }
                    }

                    writer.WriteEndElement(); // Effect
                    break;
                }

                default:
                    // Unknown @CryBar comment type in body - skip
                    i++;
                    break;
            }
        }
    }

    /// <summary>
    /// Parsed Extra metadata from @CryBar:extra lines.
    /// </summary>
    sealed class ParsedExtra
    {
        /// <summary>Simple text content (for plain extras).</summary>
        public string? Text;
        /// <summary>has attribute value.</summary>
        public string? Has;
        /// <summary>cmd attribute value (for complex extras).</summary>
        public string? Cmd;
        /// <summary>S children values (for complex extras).</summary>
        public List<string>? SValues;
    }

    /// <summary>
    /// Collects @CryBar:arg, @CryBar:v, and @CryBar:extra metadata lines.
    /// Returns args with V values; extras are added to the provided list.
    /// </summary>
    static List<(Dictionary<string, string> attrs, List<string>? vValues)> CollectArgAndExtraMetadata(
        List<string> bodyLines, ref int i, List<ParsedExtra> extras)
    {
        var args = new List<(Dictionary<string, string> attrs, List<string>? vValues)>();

        while (i < bodyLines.Count)
        {
            var subMatch = CryBarCommentRegex().Match(bodyLines[i]);
            if (!subMatch.Success) break;

            var subType = subMatch.Groups[1].Value;
            if (subType == "arg")
            {
                var argAttrs = ParseCryBarAttrs(subMatch.Groups[2].Value);
                i++;

                // Check for vcount - if present, collect subsequent @CryBar:v lines
                List<string>? vValues = null;
                if (argAttrs.TryGetValue("vcount", out var vcountStr) && int.TryParse(vcountStr, out var vcount))
                {
                    vValues = new List<string>(vcount);
                    for (int v = 0; v < vcount; v++)
                    {
                        if (i < bodyLines.Count)
                        {
                            var vMatch = CryBarCommentRegex().Match(bodyLines[i]);
                            if (vMatch.Success && vMatch.Groups[1].Value == "v")
                            {
                                vValues.Add(UnquoteValue(vMatch.Groups[2].Value));
                                i++;
                            }
                        }
                    }
                }

                args.Add((argAttrs, vValues));
            }
            else if (subType == "extra")
            {
                var extraBody = subMatch.Groups[2].Value;
                var extraAttrs = ParseCryBarAttrs(extraBody);

                if (extraAttrs.ContainsKey("cmd") && extraAttrs.TryGetValue("scount", out var scountStr)
                    && int.TryParse(scountStr, out var scount))
                {
                    // Complex Extra with cmd and S children
                    var extra = new ParsedExtra
                    {
                        Has = extraAttrs.GetValueOrDefault("has"),
                        Cmd = extraAttrs.GetValueOrDefault("cmd"),
                        SValues = new List<string>(scount)
                    };
                    i++;

                    // Collect @CryBar:s lines
                    for (int s = 0; s < scount; s++)
                    {
                        if (i < bodyLines.Count)
                        {
                            var sMatch = CryBarCommentRegex().Match(bodyLines[i]);
                            if (sMatch.Success && sMatch.Groups[1].Value == "s")
                            {
                                extra.SValues.Add(UnquoteValue(sMatch.Groups[2].Value));
                                i++;
                            }
                        }
                    }

                    extras.Add(extra);
                }
                else if (extraAttrs.ContainsKey("has") || extraAttrs.ContainsKey("text"))
                {
                    // Extra with attributes but no S children
                    extras.Add(new ParsedExtra
                    {
                        Has = extraAttrs.GetValueOrDefault("has"),
                        Cmd = extraAttrs.GetValueOrDefault("cmd"),
                        Text = extraAttrs.GetValueOrDefault("text", "")
                    });
                    i++;
                }
                else
                {
                    // Simple text Extra
                    extras.Add(new ParsedExtra { Text = extraBody });
                    i++;
                }
            }
            else break;
        }

        return args;
    }

    /// <summary>
    /// Writes an &lt;Arg&gt; element from parsed @CryBar:arg attributes and optional V children.
    /// </summary>
    static void WriteArgElement(XmlWriter writer, (Dictionary<string, string> attrs, List<string>? vValues) argData)
    {
        var (argAttrs, vValues) = argData;

        writer.WriteStartElement("Arg");

        var key = argAttrs.GetValueOrDefault("key", "");
        writer.WriteAttributeString("key", key);

        // Write name attribute if it was present in the metadata (even if empty)
        if (argAttrs.ContainsKey("name"))
            writer.WriteAttributeString("name", argAttrs["name"]);

        writer.WriteAttributeString("kt", argAttrs.GetValueOrDefault("kt", ""));
        writer.WriteAttributeString("vt", argAttrs.GetValueOrDefault("vt", ""));

        // Write magic/flag if they were present in the metadata (even if empty)
        if (argAttrs.ContainsKey("magic"))
            writer.WriteAttributeString("magic", argAttrs["magic"]);
        if (argAttrs.ContainsKey("flag"))
            writer.WriteAttributeString("flag", argAttrs["flag"]);

        if (vValues != null)
        {
            // Write V children
            foreach (var v in vValues)
            {
                writer.WriteStartElement("V");
                writer.WriteString(v);
                writer.WriteEndElement();
            }
        }
        else if (argAttrs.ContainsKey("value"))
        {
            // Explicit value key present - write text content (even if empty).
            // WriteString("") ensures <Arg ...></Arg> instead of self-closing <Arg ... />.
            writer.WriteString(argAttrs["value"]);
        }
        // else: no value key and no V children - element is self-closing

        writer.WriteEndElement();
    }

    /// <summary>
    /// Writes an &lt;Extra&gt; element from parsed metadata.
    /// Handles simple text extras, complex extras with cmd/S children, etc.
    /// </summary>
    static void WriteExtraFromParsed(XmlWriter writer, ParsedExtra extra)
    {
        writer.WriteStartElement("Extra");

        if (extra.SValues != null && extra.Cmd != null)
        {
            // Complex Extra with cmd attribute and S children
            if (!string.IsNullOrEmpty(extra.Has))
                writer.WriteAttributeString("has", extra.Has);
            writer.WriteAttributeString("cmd", extra.Cmd);
            foreach (var s in extra.SValues)
            {
                writer.WriteStartElement("S");
                writer.WriteString(s);
                writer.WriteEndElement();
            }
        }
        else if (extra.Cmd != null || extra.Has != null)
        {
            // Extra with attributes but no S children
            if (!string.IsNullOrEmpty(extra.Has))
                writer.WriteAttributeString("has", extra.Has);
            if (!string.IsNullOrEmpty(extra.Cmd))
                writer.WriteAttributeString("cmd", extra.Cmd);
            if (!string.IsNullOrEmpty(extra.Text))
                writer.WriteString(extra.Text);
        }
        else
        {
            // Simple text Extra
            writer.WriteString(extra.Text ?? "");
        }

        writer.WriteEndElement();
    }

    /// <summary>
    /// Checks if a body line is structural XS syntax that should be skipped
    /// in lossless mode (braces, if-expressions that were consumed by cond metadata).
    /// </summary>
    static bool IsStructuralLine(string trimmedLine)
    {
        return trimmedLine == "{" || trimmedLine == "}";
    }

    /// <summary>
    /// Removes surrounding quotes and unescapes a quoted value from @CryBar metadata.
    /// Handles: \" -> ", \\ -> \, \n -> newline, \r -> carriage return.
    /// </summary>
    static string UnquoteValue(string text)
    {
        text = text.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            text = text[1..^1];

        if (!text.Contains('\\'))
            return text;

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                switch (text[i + 1])
                {
                    case '"':  sb.Append('"');  i++; break;
                    case '\\': sb.Append('\\'); i++; break;
                    case 'n':  sb.Append('\n'); i++; break;
                    case 'r':  sb.Append('\r'); i++; break;
                    default:   sb.Append(text[i]); break;
                }
            }
            else
            {
                sb.Append(text[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parses key="value" pairs from a @CryBar comment's attribute text.
    /// Handles escape sequences: \", \\, \n, \r.
    /// </summary>
    static Dictionary<string, string> ParseCryBarAttrs(string attrText)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        int pos = 0;
        while (pos < attrText.Length)
        {
            // Skip whitespace
            while (pos < attrText.Length && char.IsWhiteSpace(attrText[pos]))
                pos++;

            if (pos >= attrText.Length) break;

            // Read key (word characters)
            int keyStart = pos;
            while (pos < attrText.Length && (char.IsLetterOrDigit(attrText[pos]) || attrText[pos] == '_'))
                pos++;

            if (pos == keyStart) { pos++; continue; } // skip unexpected chars

            var key = attrText[keyStart..pos];

            // Expect '='
            if (pos >= attrText.Length || attrText[pos] != '=') continue;
            pos++;

            // Expect opening '"'
            if (pos >= attrText.Length || attrText[pos] != '"') continue;
            pos++;

            // Read value until unescaped '"', handling escape sequences
            var valueSb = new StringBuilder();
            while (pos < attrText.Length)
            {
                if (attrText[pos] == '\\' && pos + 1 < attrText.Length)
                {
                    char next = attrText[pos + 1];
                    switch (next)
                    {
                        case '"':  valueSb.Append('"');  pos += 2; break;
                        case '\\': valueSb.Append('\\'); pos += 2; break;
                        case 'n':  valueSb.Append('\n'); pos += 2; break;
                        case 'r':  valueSb.Append('\r'); pos += 2; break;
                        default:   valueSb.Append(attrText[pos]); pos++; break;
                    }
                }
                else if (attrText[pos] == '"')
                {
                    pos++;
                    break;
                }
                else
                {
                    valueSb.Append(attrText[pos]);
                    pos++;
                }
            }

            result[key] = valueSb.ToString();
        }

        return result;
    }

    static List<ParsedTrigger> ParseXsRules(string xsText, CryBarFileMetadata fileMeta)
    {
        var triggers = new List<ParsedTrigger>();
        var preambleLines = new List<string>();
        Dictionary<string, string>? pendingTriggerAttrs = null;

        var lines = xsText.Split('\n');
        var state = XsParseState.OutsideRule;
        ParsedTrigger? current = null;
        int braceDepth = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i].TrimEnd('\r');
            var trimmed = rawLine.Trim();

            switch (state)
            {
                case XsParseState.OutsideRule:
                {
                    // Check for @CryBar comments outside rules
                    var cbMatch = CryBarCommentRegex().Match(trimmed);
                    if (cbMatch.Success)
                    {
                        var commentType = cbMatch.Groups[1].Value;
                        var commentBody = cbMatch.Groups[2].Value;

                        switch (commentType)
                        {
                            case "triggers":
                                fileMeta.TriggersAttrs = ParseCryBarAttrs(commentBody);
                                break;
                            case "trigger":
                                pendingTriggerAttrs = ParseCryBarAttrs(commentBody);
                                break;
                            case "group":
                                fileMeta.Groups ??= [];
                                fileMeta.Groups.Add(ParseCryBarAttrs(commentBody));
                                break;
                            default:
                                // Unknown @CryBar comment type outside rules - treat as regular line
                                if (triggers.Count > 0)
                                    triggers[^1].TrailingLines.Add(trimmed);
                                else
                                    preambleLines.Add(trimmed);
                                break;
                        }
                        break;
                    }

                    var ruleMatch = XsRuleRegex().Match(trimmed);
                    if (ruleMatch.Success)
                    {
                        // Flush preamble lines if this is the very first rule
                        if (triggers.Count == 0 && preambleLines.Count > 0)
                        {
                            var preamble = new ParsedTrigger
                            {
                                Name = "__preamble__",
                                Active = true
                            };
                            preamble.BodyLines.AddRange(preambleLines);
                            triggers.Add(preamble);
                            preambleLines.Clear();
                        }

                        var ruleName = ruleMatch.Groups[1].Value;
                        current = new ParsedTrigger
                        {
                            Name = ruleName.StartsWith('_') ? ruleName[1..] : ruleName
                        };

                        // Attach pending @CryBar:trigger metadata
                        if (pendingTriggerAttrs != null)
                        {
                            current.CryBarTriggerAttrs = pendingTriggerAttrs;
                            pendingTriggerAttrs = null;
                        }

                        state = XsParseState.InHeader;
                    }
                    else if (!string.IsNullOrWhiteSpace(trimmed))
                    {
                        // Between-rule code or preamble
                        if (triggers.Count > 0)
                        {
                            // Attach to preceding trigger's trailing lines
                            triggers[^1].TrailingLines.Add(trimmed);
                        }
                        else
                        {
                            preambleLines.Add(trimmed);
                        }
                    }
                    break;
                }

                case XsParseState.InHeader:
                {
                    if (trimmed == "{")
                    {
                        braceDepth = 1;
                        state = XsParseState.InBody;
                    }
                    else if (trimmed == "active")
                    {
                        current!.Active = true;
                    }
                    else if (trimmed == "runImmediately")
                    {
                        current!.RunImmediately = true;
                    }
                    else if (trimmed == "highFrequency")
                    {
                        // Ignored - all triggers are high frequency in this format
                    }
                    else
                    {
                        var groupMatch = XsGroupRegex().Match(trimmed);
                        if (groupMatch.Success)
                        {
                            current!.GroupName = groupMatch.Groups[1].Value.Trim();
                        }
                    }
                    break;
                }

                case XsParseState.InBody:
                {
                    // Count braces, ignoring those inside string literals and comments
                    CountBraces(rawLine, ref braceDepth);

                    if (braceDepth <= 0)
                    {
                        // Rule body is complete
                        // Don't add the closing brace line itself
                        triggers.Add(current!);
                        current = null;
                        state = XsParseState.OutsideRule;
                    }
                    else
                    {
                        // Strip leading/trailing whitespace from body lines
                        if (!string.IsNullOrWhiteSpace(trimmed))
                        {
                            current!.BodyLines.Add(trimmed);
                        }
                    }
                    break;
                }
            }
        }

        // Handle preamble if no rules were found at all
        if (triggers.Count == 0 && preambleLines.Count > 0)
        {
            var preamble = new ParsedTrigger
            {
                Name = "__preamble__",
                Active = true
            };
            preamble.BodyLines.AddRange(preambleLines);
            triggers.Add(preamble);
        }

        // If a rule was being parsed when input ended, add it
        if (current != null)
        {
            triggers.Add(current);
        }

        return triggers;
    }

    /// <summary>
    /// Counts braces in a line, ignoring those inside string literals and // comments.
    /// </summary>
    static void CountBraces(string line, ref int depth)
    {
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inString)
            {
                if (c == '\\' && i + 1 < line.Length) { i++; continue; } // skip escaped char
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break; // rest is comment
            if (c == '{') depth++;
            else if (c == '}') depth--;
        }
    }

    /// <summary>
    /// Determines whether a trigger is looping (i.e., does NOT have auto-disable calls at the end).
    /// </summary>
    static bool IsLoopingTrigger(ParsedTrigger trigger)
    {
        if (trigger.BodyLines.Count == 0)
            return true;

        // Check if the last lines are xsDisableRule and/or trDisableRule calls
        int lastIndex = trigger.BodyLines.Count - 1;
        bool hasXsDisable = false;
        bool hasTrDisable = false;

        for (int i = lastIndex; i >= 0 && i >= lastIndex - 1; i--)
        {
            var line = trigger.BodyLines[i];
            if (line.StartsWith("xsDisableRule(", StringComparison.Ordinal))
                hasXsDisable = true;
            else if (line.StartsWith("trDisableRule(", StringComparison.Ordinal))
                hasTrDisable = true;
        }

        return !(hasXsDisable || hasTrDisable);
    }

    /// <summary>
    /// Returns body lines for a trigger's effect, stripping auto-disable calls if not looping.
    /// </summary>
    static List<string> GetEffectBodyLines(ParsedTrigger trigger, bool loop)
    {
        if (loop)
            return trigger.BodyLines;

        // Strip trailing xsDisableRule / trDisableRule lines
        var result = new List<string>(trigger.BodyLines.Count);
        int end = trigger.BodyLines.Count;

        // Walk backwards from the end to find and strip disable calls
        while (end > 0)
        {
            var line = trigger.BodyLines[end - 1];
            if (line.StartsWith("xsDisableRule(", StringComparison.Ordinal) ||
                line.StartsWith("trDisableRule(", StringComparison.Ordinal))
            {
                end--;
            }
            else
            {
                break;
            }
        }

        for (int i = 0; i < end; i++)
            result.Add(trigger.BodyLines[i]);

        return result;
    }

    /// <summary>
    /// Writes all body lines as &lt;Extra&gt; blocks within a single anonymous Effect.
    /// Used as the fallback when neither lossless metadata nor trigger data templates are available.
    /// </summary>
    static void WriteFallbackExtraElements(XmlWriter writer, List<string> bodyLines)
    {
        if (bodyLines.Count == 0) return;

        writer.WriteStartElement("Effect");
        writer.WriteAttributeString("name", "");
        writer.WriteAttributeString("cmd", "true");

        foreach (var line in bodyLines)
        {
            writer.WriteStartElement("Extra");
            writer.WriteString(line);
            writer.WriteEndElement();
        }

        writer.WriteEndElement(); // Effect
    }

    /// <summary>
    /// Regex for matching <c>if (...)</c> lines. Captures the inner expression.
    /// </summary>
    [GeneratedRegex(@"^if\s*\((.+)\)\s*$")]
    private static partial Regex IfExpressionRegex();

    /// <summary>
    /// Maps a trigger_data vartype string to the numeric vt value used in trigger XML.
    /// </summary>
    static string MapVarTypeToVt(string varType)
    {
        return varType switch
        {
            "string" or "float" or "long" or "operator" or "group" or "difficulty" => "0",
            "bool" => "3",
            "unit" => "4",
            "area" => "5",
            "player" => "6",
            "tech" => "10",
            "status" or "techstatus" => "11",
            "godpower" => "12",
            "protounit" => "13",
            _ => "0",
        };
    }

    /// <summary>
    /// Processes body lines using TriggerDataIndex template matching to reconstruct
    /// structured Cond/Effect XML elements. Falls back to Extra blocks for unmatched lines.
    /// </summary>
    static void WriteTemplateMatchedElements(XmlWriter writer, List<string> bodyLines, TriggerDataIndex triggerData)
    {
        int i = 0;
        bool hasCondition = false;

        while (i < bodyLines.Count)
        {
            var line = bodyLines[i];

            // Try to match if (...) { ... } blocks as conditions
            var ifMatch = IfExpressionRegex().Match(line);
            if (ifMatch.Success)
            {
                var innerExpr = ifMatch.Groups[1].Value.Trim();

                // Strip outermost layer of redundant parentheses from the expression
                innerExpr = StripOuterParens(innerExpr);

                // Try matching against condition templates
                var condResult = TryMatchCondition(innerExpr, triggerData);
                if (condResult != null)
                {
                    hasCondition = true;
                    WriteCondElement(writer, condResult.Value.def, condResult.Value.match);
                    i++;

                    // Consume the opening brace
                    if (i < bodyLines.Count && bodyLines[i] == "{")
                        i++;

                    // Process lines inside the if block until closing brace
                    while (i < bodyLines.Count && bodyLines[i] != "}")
                    {
                        WriteEffectLineOrFallback(writer, bodyLines[i], triggerData);
                        i++;
                    }

                    // Consume the closing brace
                    if (i < bodyLines.Count && bodyLines[i] == "}")
                        i++;

                    continue;
                }
                else
                {
                    // No condition match - emit the if line and its block as Extra blocks
                    WriteExtraEffectLine(writer, line);
                    i++;

                    if (i < bodyLines.Count && bodyLines[i] == "{")
                    {
                        WriteExtraEffectLine(writer, bodyLines[i]);
                        i++;
                    }

                    while (i < bodyLines.Count && bodyLines[i] != "}")
                    {
                        WriteExtraEffectLine(writer, bodyLines[i]);
                        i++;
                    }

                    if (i < bodyLines.Count && bodyLines[i] == "}")
                    {
                        WriteExtraEffectLine(writer, bodyLines[i]);
                        i++;
                    }

                    continue;
                }
            }

            // Skip structural braces
            if (line == "{" || line == "}")
            {
                i++;
                continue;
            }

            // Regular line (not inside an if block) - try effect template matching
            WriteEffectLineOrFallback(writer, line, triggerData);
            i++;
        }

        // If no explicit condition was matched, add an implicit "Always" condition
        if (!hasCondition)
        {
            writer.WriteStartElement("Cond");
            writer.WriteAttributeString("name", "Always");
            writer.WriteAttributeString("cmd", "true");
            writer.WriteEndElement();
        }
    }

    /// <summary>
    /// Strips the outermost layer of parentheses from an expression if they are balanced.
    /// Handles cases like <c>((expr >= val))</c> -> <c>(expr >= val)</c> -> <c>expr >= val</c>.
    /// </summary>
    static string StripOuterParens(string expr)
    {
        while (expr.Length >= 2 && expr[0] == '(' && expr[^1] == ')')
        {
            // Verify the outer parens are matched (not part of separate sub-expressions)
            int depth = 0;
            bool matched = true;
            for (int j = 0; j < expr.Length - 1; j++)
            {
                if (expr[j] == '(') depth++;
                else if (expr[j] == ')') depth--;

                if (depth == 0)
                {
                    // The opening paren closed before the end - they aren't a wrapping pair
                    matched = false;
                    break;
                }
            }

            if (matched)
                expr = expr[1..^1].Trim();
            else
                break;
        }

        return expr;
    }

    /// <summary>
    /// Tries to match an expression (extracted from <c>if (...)</c>) against all condition templates.
    /// </summary>
    static (TriggerDataIndex.ConditionDef def, Match match)? TryMatchCondition(
        string expression, TriggerDataIndex triggerData)
    {
        foreach (var (_, condDef) in triggerData.Conditions)
        {
            if (condDef.ExpressionPattern == null) continue;

            var match = condDef.ExpressionPattern.Match(expression);
            if (match.Success)
                return (condDef, match);
        }

        return null;
    }

    /// <summary>
    /// Writes a &lt;Cond&gt; element from a matched condition definition and regex match.
    /// </summary>
    static void WriteCondElement(XmlWriter writer, TriggerDataIndex.ConditionDef condDef, Match match)
    {
        writer.WriteStartElement("Cond");
        writer.WriteAttributeString("name", condDef.Name);
        writer.WriteAttributeString("cmd", condDef.Expression);

        foreach (var param in condDef.Params)
        {
            var group = match.Groups[param.Name];
            if (!group.Success) continue;

            writer.WriteStartElement("Arg");
            writer.WriteAttributeString("key", param.Name);
            if (!string.IsNullOrEmpty(param.DispName) && param.DispName != param.Name)
                writer.WriteAttributeString("name", param.DispName);
            writer.WriteAttributeString("kt", "10");
            writer.WriteAttributeString("vt", MapVarTypeToVt(param.VarType));
            writer.WriteString(group.Value);
            writer.WriteEndElement(); // Arg
        }

        writer.WriteEndElement(); // Cond
    }

    /// <summary>
    /// Tries to match a single body line against effect templates.
    /// If matched, writes a structured &lt;Effect&gt; element; otherwise writes a fallback Extra.
    /// </summary>
    static void WriteEffectLineOrFallback(XmlWriter writer, string line, TriggerDataIndex triggerData)
    {
        var matchLine = line.TrimEnd();

        foreach (var (_, effectDef) in triggerData.Effects)
        {
            if (effectDef.ExtraPattern == null) continue;

            var match = effectDef.ExtraPattern.Match(matchLine);
            if (!match.Success) continue;

            // Matched - write structured Effect element
            writer.WriteStartElement("Effect");
            writer.WriteAttributeString("name", effectDef.Name);
            writer.WriteAttributeString("cmd", "true");

            foreach (var param in effectDef.Params)
            {
                var group = match.Groups[param.Name];
                if (!group.Success) continue;

                writer.WriteStartElement("Arg");
                writer.WriteAttributeString("key", param.Name);
                if (!string.IsNullOrEmpty(param.DispName) && param.DispName != param.Name)
                    writer.WriteAttributeString("name", param.DispName);
                writer.WriteAttributeString("kt", "10");
                writer.WriteAttributeString("vt", MapVarTypeToVt(param.VarType));
                writer.WriteString(group.Value);
                writer.WriteEndElement(); // Arg
            }

            // Write the Extra template
            if (!string.IsNullOrEmpty(effectDef.ExtraTemplate))
            {
                writer.WriteStartElement("Extra");
                writer.WriteString(effectDef.ExtraTemplate);
                writer.WriteEndElement();
            }

            writer.WriteEndElement(); // Effect
            return;
        }

        // No match - fallback to Extra
        WriteExtraEffectLine(writer, line);
    }

    /// <summary>
    /// Writes a single line as an &lt;Extra&gt; block wrapped in an anonymous Effect element.
    /// </summary>
    static void WriteExtraEffectLine(XmlWriter writer, string line)
    {
        writer.WriteStartElement("Effect");
        writer.WriteAttributeString("name", "");
        writer.WriteAttributeString("cmd", "true");
        writer.WriteStartElement("Extra");
        writer.WriteString(line);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }
}
