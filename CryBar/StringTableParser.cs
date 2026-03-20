using System.Text.RegularExpressions;

namespace CryBar;

/// <summary>
/// Parses string_table.txt files used by Age of Mythology: Retold.
/// Format: ID = "KEY" ; Str = "value" (value can be multi-line, terminated by next ID or EOF).
/// </summary>
public static partial class StringTableParser
{
    /// <summary>
    /// Matches: ID = "STR_KEY" ; Str = "content start
    /// Captures: group 1 = key, group 2 = first line of content (may not be complete if multi-line)
    /// </summary>
    [GeneratedRegex(@"^\s*ID\s*=\s*""([^""]+)""\s*;\s*Str\s*=\s*""(.*)$", RegexOptions.Multiline)]
    private static partial Regex EntryStartPattern();

    /// <summary>
    /// Parses a string_table.txt file and returns a dictionary of ID → string content.
    /// Handles multi-line values (content continues until closing quote before next ID or EOF).
    /// </summary>
    public static Dictionary<string, string> Parse(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = EntryStartPattern().Matches(content);

        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var key = match.Groups[1].Value;
            var valueStart = match.Groups[2].Value;

            // Find the end of the value: look for closing quote
            // The value portion starts after Str = " and ends at a closing "
            // For single-line: ID = "KEY" ; Str = "value"  (possibly ; Symbol = "...")
            // For multi-line: value spans until a line with closing " before next ID

            string value;
            var closingQuote = valueStart.IndexOf('"');
            if (closingQuote >= 0)
            {
                // Single-line value
                value = valueStart[..closingQuote];
            }
            else
            {
                // Multi-line: scan forward from match end until we find closing quote
                var searchStart = match.Index + match.Length;
                var searchEnd = i + 1 < matches.Count ? matches[i + 1].Index : content.Length;
                var remainder = content.AsSpan(searchStart, searchEnd - searchStart);

                var endQuote = remainder.IndexOf('"');
                if (endQuote >= 0)
                {
                    value = valueStart + content.Substring(searchStart, endQuote);
                }
                else
                {
                    // No closing quote found - take everything
                    value = valueStart + remainder.TrimEnd().ToString();
                }
            }

            result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// Looks up a single key in string_table.txt content without parsing the entire file.
    /// Returns null if the key is not found.
    /// </summary>
    public static string? FindValue(string content, string key)
    {
        // Quick check - if key doesn't appear at all, skip parsing
        if (!content.Contains(key, StringComparison.OrdinalIgnoreCase))
            return null;

        var dict = Parse(content);
        return dict.GetValueOrDefault(key);
    }
}
