using CryBar.Indexing;

namespace CryBar.Tests;

public class AnimfileIndexTests
{
    static FileIndexEntry MakeEntry(string path) => new()
    {
        FullRelativePath = path,
        FileName = Path.GetFileName(path),
        Source = FileIndexSource.BarEntry,
    };

    [Fact]
    public void Add_ExtractsStemFromModelPath()
    {
        var index = new AnimfileIndex();
        var entry = MakeEntry(@"game\art\greek\hoplite.xml.XMB");
        index.Add(@"greek\units\infantry\hoplite\hoplite_iron", entry);

        Assert.Equal(1, index.Count);
        Assert.NotNull(index.Find("hoplite_iron"));
    }

    [Fact]
    public void Find_CaseInsensitive()
    {
        var index = new AnimfileIndex();
        var entry = MakeEntry(@"game\art\hoplite.xml.XMB");
        index.Add("hoplite", entry);

        Assert.NotNull(index.Find("Hoplite"));
        Assert.NotNull(index.Find("HOPLITE"));
    }

    [Fact]
    public void Find_ReturnsNull_WhenNotFound()
    {
        var index = new AnimfileIndex();
        Assert.Null(index.Find("nonexistent"));
    }

    [Fact]
    public void Find_StripsTrailingSegments()
    {
        var index = new AnimfileIndex();
        var entry = MakeEntry(@"game\art\hoplite.xml.XMB");
        index.Add("hoplite", entry);

        // "hoplite_iron" strips "_iron" to find "hoplite"
        Assert.Same(entry, index.Find("hoplite_iron"));
        // "hoplite_iron_extra" strips "_extra" then "_iron"
        Assert.Same(entry, index.Find("hoplite_iron_extra"));
    }

    [Fact]
    public void Find_MultipleSegmentStripping()
    {
        var index = new AnimfileIndex();
        var entry = MakeEntry(@"game\art\armory.xml.XMB");
        index.Add("armory", entry);

        // "armory_a_age2" -> "armory_a" -> "armory"
        Assert.Same(entry, index.Find("armory_a_age2"));
    }

    [Fact]
    public void Find_PrefersExactMatch()
    {
        var index = new AnimfileIndex();
        var baseEntry = MakeEntry(@"game\art\hoplite.xml.XMB");
        var variantEntry = MakeEntry(@"game\art\hoplite_mythic.xml.XMB");
        index.Add("hoplite", baseEntry);
        index.Add("hoplite_mythic", variantEntry);

        // exact match wins
        Assert.Same(variantEntry, index.Find("hoplite_mythic"));
        // but variant of base still falls through to base
        Assert.Same(baseEntry, index.Find("hoplite_iron"));
    }

    [Fact]
    public void Find_StopsAtSingleCharStem()
    {
        var index = new AnimfileIndex();
        // stem "a" should not match when looking up "a_b"
        // because lastUnderscore=1 which is > 0, so it would try "a"
        // but if nothing is indexed, returns null
        Assert.Null(index.Find("x_y"));
    }

    [Fact]
    public void Add_HandlesForwardSlashes()
    {
        var index = new AnimfileIndex();
        var entry = MakeEntry(@"game\art\hoplite.xml.XMB");
        index.Add("greek/units/hoplite/hoplite", entry);

        Assert.NotNull(index.Find("hoplite"));
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var index = new AnimfileIndex();
        index.Add("hoplite", MakeEntry("a.xml.XMB"));
        index.Add("archer", MakeEntry("b.xml.XMB"));
        Assert.Equal(2, index.Count);

        index.Clear();
        Assert.Equal(0, index.Count);
        Assert.Null(index.Find("hoplite"));
    }
}
