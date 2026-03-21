namespace CryBar.Cli.Models;

public class BarListResult
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Compression { get; set; } = "";
    public string Convert { get; set; } = "";
}
