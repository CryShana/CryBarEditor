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
    public static partial Regex GetIncludeRgx();

    // trDelayedRuleActivation(string name, bool checkForTrigger)
    [GeneratedRegex("""trDelayedRuleActivation\(([^\),;]+),*[^\);]*\)\s*;""")]
    public static partial Regex GetDelayedRuleActivationRgx();

    public static bool Convert(string input_path, string output_path, string class_name)
    {
        class_name = class_name.Length > 0 ? class_name : "ExportedClass";
        if (class_name.Length == 0) class_name = "ExportedClass";

        if (!File.Exists(input_path))
            return false;

        const string FUNCTION = "_c";

        var output_text = ProcessXStoRM(input_path);

        output_text = $$"""
                #if (defined({{class_name}}_INCLUDE) == false)
                #define {{class_name}}_INCLUDE
                class {{class_name}} { 
                void {{FUNCTION}}(string l = ""){rmTriggerAddScriptLine(l);}
                void RegisterTriggers()
                {
                {{output_text}}
                }
                };
                #endif   
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
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Include '" + include + "' not found, will be ignored.");
                    Console.ResetColor();
                    continue;
                }

                var include_text = ProcessXStoRM(include_path, false);
                text_with_includes = text.Replace(whole_include, include_text);
            }

            if (wrap)
            {
                // HANDLE DELAYED RULE ACTIVATIONS
                var delayedActivations = GetDelayedRuleActivationRgx().Matches(text_with_includes);
                if (delayedActivations.Count > 0)
                {
                    text_with_includes = GetDelayedRuleActivationRgx()
                        .Replace(text_with_includes, "__delayedRuleActivations.add($1);");

                    // add this on top
                    text_with_includes = $$"""
                    string[] __delayedRuleActivations = default;
                    rule __DelayedRuleActivations
                    highFrequency
                    active
                    {
                        for(int i = 0; i < __delayedRuleActivations.size(); i++) 
                        {
                            string triggerName = __delayedRuleActivations[i];
                            xsEnableRule(triggerName);
                        }
                        __delayedRuleActivations.clear();
                    }
                    
                    {{text_with_includes}}
                    """;
                }

                // DO THE WRAPPING
                var builder = new StringBuilder();
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
