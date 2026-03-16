using CryBar;

namespace CryBar.Tests;

public class FileIndexBuilderTests : IDisposable
{
    readonly string _tempRoot;

    public FileIndexBuilderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "CryBarTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void FindSupplemental_RootContainsBars_ReturnsEmpty()
    {
        // Root itself has .bar files — supplemental scan should return empty
        // because the root's BARs are already indexed by the main scan.
        var root = Path.Combine(_tempRoot, "parent", "child");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "test.bar"), []);

        var result = FileIndexBuilder.FindSupplementalBarFiles(root);
        Assert.Empty(result);
    }

    [Fact]
    public void FindSupplemental_SubdirectoryRoot_FindsParentBars()
    {
        // parent/sibling/ has .bar files, root is parent/child/
        var parent = Path.Combine(_tempRoot, "parent");
        var child = Path.Combine(parent, "child");
        var sibling = Path.Combine(parent, "sibling");
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(sibling);
        File.WriteAllBytes(Path.Combine(sibling, "Art.bar"), []);
        File.WriteAllBytes(Path.Combine(sibling, "Data.bar"), []);

        var result = FileIndexBuilder.FindSupplementalBarFiles(child);
        Assert.Equal(2, result.Count);
        Assert.All(result, p => Assert.EndsWith(".bar", p));
    }

    [Fact]
    public void FindSupplemental_RespectsMaxDepth()
    {
        // BARs only at grandparent+1 level (3 up), maxDepth=2 should not find them
        var level0 = Path.Combine(_tempRoot, "a");
        var level1 = Path.Combine(level0, "b");
        var level2 = Path.Combine(level1, "c");
        var root = Path.Combine(level2, "d");
        Directory.CreateDirectory(root);

        // Put BAR at level0 (3 levels up from root)
        File.WriteAllBytes(Path.Combine(level0, "Deep.bar"), []);

        var result = FileIndexBuilder.FindSupplementalBarFiles(root, maxDepth: 2);
        Assert.Empty(result);
    }

    [Fact]
    public void FindSupplemental_ExcludesFilesUnderRoot()
    {
        // BARs exist both in parent and inside root — only parent ones should be returned
        var parent = Path.Combine(_tempRoot, "parent");
        var root = Path.Combine(parent, "child");
        Directory.CreateDirectory(root);

        // BAR in parent directory
        File.WriteAllBytes(Path.Combine(parent, "Outside.bar"), []);
        // BAR inside root
        File.WriteAllBytes(Path.Combine(root, "Inside.bar"), []);

        var result = FileIndexBuilder.FindSupplementalBarFiles(root);
        Assert.Single(result);
        Assert.Contains("Outside.bar", result[0]);
    }

    [Fact]
    public void FindSupplemental_SkipsDriveRoot()
    {
        // If root is a direct child of the drive root, we should not scan the drive root
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var directChild = Path.Combine(driveRoot, "TestDirectChild_" + Guid.NewGuid().ToString("N"));

        try
        {
            // We can't actually create dirs at drive root easily, so we test that the method
            // returns empty and doesn't throw when the parent IS the drive root
            // Use maxDepth=1 with root = directChild (whose parent is drive root)
            // Since directChild likely doesn't exist, but we only need to test
            // that drive root is skipped — create it temporarily
            Directory.CreateDirectory(directChild);
            var result = FileIndexBuilder.FindSupplementalBarFiles(directChild, maxDepth: 1);
            // Should be empty — drive root is skipped
            Assert.Empty(result);
        }
        finally
        {
            try { Directory.Delete(directChild, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void FindSupplemental_SkipsParentWithTooManySubdirs()
    {
        // Parent has 60+ subdirectories — should be skipped
        var parent = Path.Combine(_tempRoot, "crowded_parent");
        var root = Path.Combine(parent, "child");
        Directory.CreateDirectory(root);

        // Create 60 sibling directories
        for (int i = 0; i < 60; i++)
        {
            var sibling = Path.Combine(parent, $"sibling_{i:D3}");
            Directory.CreateDirectory(sibling);
        }

        // Put a BAR in one of the siblings
        File.WriteAllBytes(Path.Combine(parent, "sibling_001", "Test.bar"), []);

        // maxSubdirectories=50, so this level should be skipped
        var result = FileIndexBuilder.FindSupplementalBarFiles(root, maxSubdirectories: 50);
        Assert.Empty(result);
    }

    [Fact]
    public void FindSupplemental_RespectsMaxFiles()
    {
        // Parent has many BARs — only maxFiles should be returned
        var parent = Path.Combine(_tempRoot, "bar_parent");
        var root = Path.Combine(parent, "child");
        var sibling = Path.Combine(parent, "sibling");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);

        for (int i = 0; i < 20; i++)
            File.WriteAllBytes(Path.Combine(sibling, $"File_{i:D2}.bar"), []);

        var result = FileIndexBuilder.FindSupplementalBarFiles(root, maxFiles: 5);
        Assert.Equal(5, result.Count);
    }

    [Fact]
    public void FindSupplemental_ShortCircuitsWhenBarsFound()
    {
        // BARs at level 1 — should not scan level 2
        var grandparent = Path.Combine(_tempRoot, "grandparent");
        var parent = Path.Combine(grandparent, "parent");
        var root = Path.Combine(parent, "child");
        var parentSibling = Path.Combine(parent, "sibling");
        var grandparentSibling = Path.Combine(grandparent, "sibling2");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(parentSibling);
        Directory.CreateDirectory(grandparentSibling);

        // BARs at level 1 (parent's sibling)
        File.WriteAllBytes(Path.Combine(parentSibling, "Level1.bar"), []);
        // BARs at level 2 (grandparent's sibling)
        File.WriteAllBytes(Path.Combine(grandparentSibling, "Level2.bar"), []);

        var result = FileIndexBuilder.FindSupplementalBarFiles(root, maxDepth: 2);
        // Should only contain level 1 BAR
        Assert.Single(result);
        Assert.Contains("Level1.bar", result[0]);
    }
}
