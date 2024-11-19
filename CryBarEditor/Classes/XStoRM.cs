using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CryBarEditor.Classes;

// Source: https://github.com/CryShana/XStoRM
public static partial class XStoRM
{
    [GeneratedRegex("""[^a-zA-Z]""")]
    public static partial Regex GetSafeClassNameRgx();

    [GeneratedRegex("""include "([^"]+)"\;""")]
    private static partial Regex GetIncludeRgx();

    public static bool Convert(string input_path, string output_path, string class_name)
    {
        class_name = class_name.Length > 0 ? class_name : "ExportedClass";
        if (class_name.Length == 0) class_name = "ExportedClass";

        if (!File.Exists(input_path))
            return false;
        
        const string FUNCTION = "_c";

        var output_text = ProcessXStoRM(input_path);

        output_text = $$"""
                class {{class_name}} { 
                void {{FUNCTION}}(string l = ""){rmTriggerAddScriptLine(l);}
                void RegisterTriggers()
                {
                {{output_text}}
                }
                };   
                """;

        File.WriteAllText(output_path, output_text);
        return true;

        static string ProcessXStoRM(string file_path, bool wrap = true)
        {
            var root = Path.GetDirectoryName(file_path);
            var text = File.ReadAllText(file_path);
            var text_with_includes = text;

            // PROCESS INCLUDES
            var include_rgx = GetIncludeRgx();
            var to_replace = new List<(string, string)>();
            foreach (Match m in include_rgx.Matches(text))
            {
                var whole_include = m.Groups[0].Value;
                var include = m.Groups[1].Value;
                var include_path = Path.Combine(root ?? ".", include);
                if (!File.Exists(include_path))
                {
                    // TODO: log warning that include was not found
                    continue;
                }

                var include_text = ProcessXStoRM(include_path, false);
                text_with_includes = text.Replace(whole_include, include_text);
            }

            if (wrap)
            {
                // DO THE WRAPPING
                StringBuilder builder = new();
                var new_lines = text_with_includes.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in new_lines)
                {
                    var corrected_line = line.Replace("\r", "");
                    builder.AppendLine($"{FUNCTION}(\"{EscapeText(corrected_line)}\");");
                }
                text_with_includes = builder.ToString();
            }

            return text_with_includes;
        }

        static string EscapeText(string text)
        {
            return text.Replace("\"", "\\\"");
        }
    }
}
