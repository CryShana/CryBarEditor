namespace CryBar.Cli.Models;

public class SearchResultJson
{
    public string Path { get; set; } = "";
    public string? Source { get; set; }
    public int Index { get; set; }
    public string Context { get; set; } = "";
    public bool InContent { get; set; }
}
