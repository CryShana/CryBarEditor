using System.CommandLine;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace CryBar.Cli.Helpers;

public static class OutputHelper
{
    public static bool Quiet { get; set; }
    public static bool Verbose { get; set; }
    public static bool Json { get; set; }

    public static Option<bool> QuietOption { get; set; } = null!;
    public static Option<bool> VerboseOption { get; set; } = null!;
    public static Option<bool> JsonOption { get; set; } = null!;

    public static void ApplyGlobalOptions(ParseResult parseResult)
    {
        Quiet = parseResult.GetValue(QuietOption);
        Verbose = parseResult.GetValue(VerboseOption);
        Json = parseResult.GetValue(JsonOption);
        CheckFirstRun();
    }

    public static void Success(string message)
    {
        if (Quiet) return;
        AnsiConsole.MarkupLine($"[green]>[/] {message}");
    }

    public static void Info(string message)
    {
        if (Quiet) return;
        AnsiConsole.MarkupLine($"[blue]*[/] {message}");
    }

    public static void Error(string message)
    {
        AnsiConsole.MarkupLine($"[red]x[/] {message}");
    }

    public static void Warn(string message)
    {
        if (Quiet) return;
        AnsiConsole.MarkupLine($"[yellow]![/] {message}");
    }

    public static string FormatPath(string path)
    {
        var dir = Path.GetDirectoryName(path)?.Replace("\\", "/");
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(dir))
            return Markup.Escape(name);
        return $"[grey]{Markup.Escape(dir + "/")}[/]{Markup.Escape(name)}";
    }

    public static Regex GlobToRegex(string glob)
    {
        var pattern = "^" + Regex.Escape(glob)
            .Replace("\\*\\*", "\u00a7\u00a7")
            .Replace("\\*", "[^/]*")
            .Replace("\u00a7\u00a7", ".*")
            .Replace("\\?", ".")
            + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase);
    }

    public static void EnsureDir(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
    }

    static bool _firstRunChecked;

    static void CheckFirstRun()
    {
        if (_firstRunChecked || Quiet || Json) return;
        _firstRunChecked = true;
        var config = Config.CliConfig.Load();
        if (!config.SetupCompleted && !config.HintShown)
        {
            AnsiConsole.MarkupLine("[blue]*[/] First run detected. Run [bold]crybar setup[/] to enable tab completions.");
            config.HintShown = true;
            Config.CliConfig.Save(config);
        }
    }
}
