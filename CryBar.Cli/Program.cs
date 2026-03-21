using System.CommandLine;
using CryBar.Cli.Commands;
using CryBar.Cli.Helpers;

var rootCommand = new RootCommand("CryBar CLI - modding toolkit for Age of Mythology: Retold");

// Global options (Recursive = true makes them available on all subcommands)
var quietOption = new Option<bool>("--quiet", "-q") { Description = "Suppress non-essential output", Recursive = true };
var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Extra detail in output", Recursive = true };
var jsonOption = new Option<bool>("--json") { Description = "Machine-readable JSON output", Recursive = true };

rootCommand.Add(quietOption);
rootCommand.Add(verboseOption);
rootCommand.Add(jsonOption);

// Subcommands
var barCommand = BarCommands.Create();
var convertCommand = ConvertCommands.Create();
var (compressCommand, decompressCommand) = CompressCommands.Create();
var depsCommand = DepsCommands.Create();
var searchCommand = SearchCommand.Create();
var rootDirCommand = RootDirectoryCommand.Create();
var setupCommand = SetupCommand.CreateSetup();
var completionsCommand = SetupCommand.CreateCompletions();

rootCommand.Add(barCommand);
rootCommand.Add(convertCommand);
rootCommand.Add(compressCommand);
rootCommand.Add(decompressCommand);
rootCommand.Add(depsCommand);
rootCommand.Add(searchCommand);
rootCommand.Add(rootDirCommand);
rootCommand.Add(setupCommand);
rootCommand.Add(completionsCommand);

// Store global option instances so command handlers can access them via OutputHelper.ApplyGlobalOptions
OutputHelper.QuietOption = quietOption;
OutputHelper.VerboseOption = verboseOption;
OutputHelper.JsonOption = jsonOption;

// No subcommand given - show help
rootCommand.SetAction((parseResult) =>
{
    OutputHelper.ApplyGlobalOptions(parseResult);
    new System.CommandLine.Help.HelpAction().Invoke(parseResult);
});

return await rootCommand.Parse(args).InvokeAsync();
