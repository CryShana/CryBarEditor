using System.CommandLine;
using CryBar.Cli.Config;
using CryBar.Cli.Helpers;
using Spectre.Console;

namespace CryBar.Cli.Commands;

public static class SetupCommand
{
    public static Command CreateSetup()
    {
        var cmd = new Command("setup", "One-time setup: install tab completions and create config directory");
        cmd.SetAction((parseResult) =>
        {
            OutputHelper.ApplyGlobalOptions(parseResult);
            var config = CliConfig.Load();

            // Detect shell and show completion instructions
            var shell = DetectShell();
            if (shell != null)
            {
                OutputHelper.Info($"Detected shell: {shell}");
                AnsiConsole.MarkupLine($"  To enable tab completions, add the output of [bold]crybar completions {shell.ToLowerInvariant()}[/] to your profile.");
            }
            else
            {
                OutputHelper.Warn("Could not detect shell. Use 'crybar completions <shell>' to get the script manually.");
            }

            config.SetupCompleted = true;
            CliConfig.Save(config);
            OutputHelper.Success($"Config directory: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".crybar")}");

            return 0;
        });
        return cmd;
    }

    public static Command CreateCompletions()
    {
        var cmd = new Command("completions", "Output shell completion scripts");

        var psCmd = new Command("powershell", "Output PowerShell completion script");
        psCmd.SetAction((parseResult) =>
        {
            OutputHelper.ApplyGlobalOptions(parseResult);
            Console.WriteLine(GetPowerShellCompletionScript());
            return 0;
        });

        var bashCmd = new Command("bash", "Output bash completion script");
        bashCmd.SetAction((parseResult) =>
        {
            OutputHelper.ApplyGlobalOptions(parseResult);
            Console.WriteLine(GetBashCompletionScript());
            return 0;
        });

        var zshCmd = new Command("zsh", "Output zsh completion script");
        zshCmd.SetAction((parseResult) =>
        {
            OutputHelper.ApplyGlobalOptions(parseResult);
            Console.WriteLine(GetZshCompletionScript());
            return 0;
        });

        cmd.Add(psCmd);
        cmd.Add(bashCmd);
        cmd.Add(zshCmd);
        return cmd;
    }

    static string? DetectShell()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PSModulePath")))
            return "PowerShell";
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (shell != null)
        {
            if (shell.Contains("zsh")) return "zsh";
            if (shell.Contains("bash")) return "bash";
        }
        return null;
    }

    static string GetPowerShellCompletionScript()
    {
        var exePath = Environment.ProcessPath ?? "crybar";
        var exeName = Path.GetFileNameWithoutExtension(exePath);
        return
            "Register-ArgumentCompleter -Native -CommandName " + exeName + " -ScriptBlock {\n" +
            "    param($wordToComplete, $commandAst, $cursorPosition)\n" +
            "    $commandText = $commandAst.ToString()\n" +
            "    $result = & \"" + exePath + "\" \"[suggest:$cursorPosition]\" $commandText | ForEach-Object {\n" +
            "        [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)\n" +
            "    }\n" +
            "    $result\n" +
            "}";
    }

    static string GetBashCompletionScript()
    {
        return """
            _crybar_completions() {
                local IFS=$'\n'
                local cursor=${COMP_POINT}
                COMPREPLY=( $(crybar "[suggest:${cursor}]" "${COMP_LINE}" 2>/dev/null) )
            }
            complete -F _crybar_completions crybar
            """;
    }

    static string GetZshCompletionScript()
    {
        return """
            _crybar() {
                local IFS=$'\n'
                local cursor=${#LBUFFER}
                compadd -- $(crybar "[suggest:${cursor}]" "${BUFFER}" 2>/dev/null)
            }
            compdef _crybar crybar
            """;
    }
}
