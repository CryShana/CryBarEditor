using System.CommandLine;
using CryBar.Cli.Config;
using CryBar.Cli.Helpers;
using Spectre.Console;

namespace CryBar.Cli.Commands;

public static class RootDirectoryCommand
{
    public static Command Create()
    {
        var rootCmd = new Command("root", "Game directory context management");

        // root set <path>
        var setPathArg = new Argument<DirectoryInfo>("path") { Description = "Path to game root directory" };
        var setCmd = new Command("set", "Set game root directory") { setPathArg };
        setCmd.SetAction((parseResult) =>
        {
            OutputHelper.ApplyGlobalOptions(parseResult);
            var path = parseResult.GetValue(setPathArg);
            if (path == null || !path.Exists)
            {
                OutputHelper.Error($"Directory not found: {path?.FullName ?? "(null)"}");
                return 1;
            }

            var config = CliConfig.Load();
            config.Root = path.FullName;
            CliConfig.Save(config);

            OutputHelper.Success($"Root directory set: {path.FullName}");

            if (!OutputHelper.Quiet)
            {
                // Count .bar and .bank files
                var barCount = Directory.GetFiles(path.FullName, "*.bar", SearchOption.AllDirectories).Length;
                var bankCount = Directory.GetFiles(path.FullName, "*.bank", SearchOption.AllDirectories).Length;
                AnsiConsole.MarkupLine($"  Found: [bold]{barCount}[/] .bar archives, [bold]{bankCount}[/] .bank files");
            }

            return 0;
        });

        // root get
        var getCmd = new Command("get", "Display current root path");
        getCmd.SetAction((parseResult) =>
        {
            OutputHelper.ApplyGlobalOptions(parseResult);
            var config = CliConfig.Load();
            if (string.IsNullOrEmpty(config.Root))
            {
                OutputHelper.Error("No root directory set. Run: crybar root set <path>");
                return 1;
            }
            // Always print the path, even in quiet mode (it IS the result)
            Console.WriteLine(config.Root);
            return 0;
        });

        // root clear
        var clearCmd = new Command("clear", "Remove stored root");
        clearCmd.SetAction((parseResult) =>
        {
            OutputHelper.ApplyGlobalOptions(parseResult);
            var config = CliConfig.Load();
            config.Root = null;
            CliConfig.Save(config);
            OutputHelper.Success("Root directory cleared.");
            return 0;
        });

        rootCmd.Add(setCmd);
        rootCmd.Add(getCmd);
        rootCmd.Add(clearCmd);
        return rootCmd;
    }
}
