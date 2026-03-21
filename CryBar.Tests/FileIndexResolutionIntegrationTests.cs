using CryBar;
using CryBar.Indexing;

namespace CryBar.Tests;

/// <summary>
/// Integration tests for FileIndexBuilder + FileIndex resolution using real game files.
/// Skipped when the game is not installed.
/// </summary>
public class FileIndexResolutionIntegrationTests
{
    static readonly string GamePath =
        Environment.GetEnvironmentVariable("AOMR_GAME_PATH")
        ?? @"C:\Program Files (x86)\Steam\steamapps\common\Age of Mythology Retold\game";

    static bool GameInstalled => Directory.Exists(GamePath);

    [SkippableFact]
    public void GameRoot_NoSupplementalBarsNeeded()
    {
        Skip.IfNot(GameInstalled, "Game not found");

        var result = FileIndexBuilder.FindSupplementalBarFiles(GamePath);
        Assert.Empty(result);
    }

    [SkippableFact]
    public void SubdirectoryRoot_FindsSupplementalBars()
    {
        Skip.IfNot(GameInstalled, "Game not found");

        var modelcachePath = Path.Combine(GamePath, "modelcache");
        Skip.IfNot(Directory.Exists(modelcachePath), "modelcache directory not found");

        var result = FileIndexBuilder.FindSupplementalBarFiles(modelcachePath);
        Assert.NotEmpty(result);
        Assert.All(result, p => Assert.EndsWith(".bar", p, StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public void SubdirectoryRoot_MaterialResolvesWithSupplemental()
    {
        Skip.IfNot(GameInstalled, "Game not found");

        var modelcachePath = Path.Combine(GamePath, "modelcache");
        Skip.IfNot(Directory.Exists(modelcachePath), "modelcache directory not found");

        // Build index from ONLY modelcache BARs
        var index = new FileIndex();
        var modelcacheBars = Directory.GetFiles(modelcachePath, "*.bar", SearchOption.AllDirectories);
        Skip.If(modelcacheBars.Length == 0, "No BAR files found in modelcache");

        FileIndexBuilder.IndexBarFiles(index, modelcacheBars);

        // armory_a_age2.material.XMB should NOT resolve from modelcache alone
        var beforeResults = index.Find("armory_a_age2.material.XMB");
        // It might or might not be in modelcache — the key assertion is that after supplemental it works
        var countBefore = beforeResults.Count;

        // Now add supplemental BARs
        var supplementalBars = FileIndexBuilder.FindSupplementalBarFiles(modelcachePath);
        Assert.NotEmpty(supplementalBars);
        FileIndexBuilder.IndexBarFiles(index, supplementalBars);

        // Now it should resolve (material files are typically in art/ BARs)
        var afterResults = index.Find("armory_a_age2.material.XMB");
        Assert.True(afterResults.Count > countBefore,
            $"Expected more results after supplemental indexing. Before: {countBefore}, After: {afterResults.Count}");
    }
}
