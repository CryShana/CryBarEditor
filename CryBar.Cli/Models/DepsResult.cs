namespace CryBar.Cli.Models;

public class DepsResult
{
    public string File { get; set; } = "";
    public string[] Dependencies { get; set; } = [];
}
