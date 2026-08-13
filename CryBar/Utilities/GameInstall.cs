using System;
using System.IO;

namespace CryBar.Utilities;

/// <summary>
/// Locates the AoM:Retold game install: AOMR_GAME_PATH env var, falling back
/// to the default Steam location. Same convention the integration tests use.
/// </summary>
public static class GameInstall
{
    public const string DefaultGamePath =
        @"C:\Program Files (x86)\Steam\steamapps\common\Age of Mythology Retold\game";

    public static string RootPath =>
        Environment.GetEnvironmentVariable("AOMR_GAME_PATH") ?? DefaultGamePath;

    /// <summary>Returns the full path of a file under the game root, or null when absent.</summary>
    public static string? TryFindFile(string subdir, string fileName)
    {
        var path = Path.Combine(RootPath, subdir, fileName);
        return File.Exists(path) ? path : null;
    }
}
