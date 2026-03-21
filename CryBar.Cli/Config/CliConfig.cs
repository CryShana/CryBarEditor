using System.Text.Json;
using System.Text.Json.Serialization;

namespace CryBar.Cli.Config;

public class CliConfiguration
{
    public string? Root { get; set; }
    public bool SetupCompleted { get; set; }
    public bool HintShown { get; set; }
}

[JsonSerializable(typeof(CliConfiguration))]
internal partial class ConfigJsonContext : JsonSerializerContext { }

public static class CliConfig
{
    static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".crybar");
    static readonly string ConfigFile = Path.Combine(ConfigDir, "config.json");

    public static CliConfiguration Load()
    {
        if (!File.Exists(ConfigFile))
            return new CliConfiguration();
        var json = File.ReadAllText(ConfigFile);
        return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.CliConfiguration)
            ?? new CliConfiguration();
    }

    public static void Save(CliConfiguration config)
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.CliConfiguration);
        File.WriteAllText(ConfigFile, json);
    }

    public static string? GetRoot()
    {
        var config = Load();
        return config.Root;
    }

    /// <summary>
    /// Gets root or writes error and returns null.
    /// Use in commands that require root.
    /// </summary>
    public static string? RequireRoot()
    {
        var root = GetRoot();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            Helpers.OutputHelper.Error("No root directory set. Run: crybar root set <path>");
            return null;
        }
        return root;
    }
}
