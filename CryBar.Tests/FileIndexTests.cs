using System.IO;

using CryBar;

namespace CryBar.Tests;

public class FileIndexTests
{
    static FileIndexEntry MakeEntry(string fullPath, string? barFilePath = null)
    {
        var fileName = Path.GetFileName(fullPath.Replace('\\', '/'));
        return new FileIndexEntry
        {
            FullRelativePath = fullPath,
            FileName = fileName,
            Source = barFilePath != null ? FileIndexSource.BarEntry : FileIndexSource.RootFile,
            BarFilePath = barFilePath,
        };
    }

    [Fact]
    public void ExactPathMatch()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\data\proto.xml.XMB"));
        var results = index.Find(@"game\data\proto.xml.XMB");
        Assert.Single(results);
        Assert.Equal("proto.xml.XMB", results[0].FileName);
    }

    [Fact]
    public void CaseInsensitiveMatch()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\data\Proto.xml.XMB"));
        var results = index.Find(@"GAME\DATA\PROTO.XML.XMB");
        Assert.Single(results);
    }

    [Fact]
    public void ExtensionStripping()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\data\proto.xml"));
        var results = index.Find(@"game\data\proto.xml.xmb");
        Assert.Single(results);
    }

    [Fact]
    public void ExtensionAppending()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\data\proto.xml.xmb"));
        var results = index.Find(@"game\data\proto.xml");
        Assert.Single(results);
    }

    [Fact]
    public void FileNameOnlyLookup()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\data\deep\path\myfile.ddt"));
        var results = index.Find("myfile.ddt");
        Assert.Single(results);
    }

    [Fact]
    public void SuffixMatch()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\textures\greek\armory_a.ddt"));
        index.Add(MakeEntry(@"game\textures\norse\armory_a.ddt"));
        var results = index.Find(@"textures\greek\armory_a.ddt");
        Assert.Single(results);
        Assert.Contains("greek", results[0].FullRelativePath);
    }

    [Fact]
    public void MultipleResults()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\data\proto.xml.XMB", "bar1.bar"));
        index.Add(MakeEntry(@"game\data\proto.xml.XMB", "bar2.bar"));
        var results = index.Find(@"game\data\proto.xml.XMB");
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Remove()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\data\proto.xml.XMB"));
        index.Remove(@"game\data\proto.xml.XMB");
        var results = index.Find(@"game\data\proto.xml.XMB");
        Assert.Empty(results);
    }

    [Fact]
    public void Clear()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\data\proto.xml.XMB"));
        index.Add(MakeEntry(@"game\textures\foo.ddt"));
        index.Clear();
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void FileNameWithKnownExtensionAppended()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\textures\armory_basecolor.ddt"));
        var results = index.Find("armory_basecolor");
        Assert.Single(results);
    }

    // --- FindByPartialPath tests ---

    [Fact]
    public void PartialPath_MatchesStemAcrossExtensions()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"intermediate\modelcache\greek\units\infantry\hoplite\hoplite_iron.tmm"));
        index.Add(MakeEntry(@"intermediate\modelcache\greek\units\infantry\hoplite\hoplite_iron.tmm.data"));
        index.Add(MakeEntry(@"game\art\greek\units\infantry\hoplite\hoplite_iron.fbximport"));
        index.Add(MakeEntry(@"game\art\greek\units\infantry\hoplite\hoplite_iron.material.XMB"));

        var results = index.FindByPartialPath(@"greek\units\infantry\hoplite\hoplite_iron");
        Assert.Equal(4, results.Count);
    }

    [Fact]
    public void PartialPath_SuffixFiltersProperly()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\art\greek\units\infantry\hoplite\hoplite_iron.fbximport"));
        index.Add(MakeEntry(@"game\art\norse\units\infantry\hoplite\hoplite_iron.fbximport"));

        var results = index.FindByPartialPath(@"greek\units\infantry\hoplite\hoplite_iron");
        Assert.Single(results);
        Assert.Contains("greek", results[0].FullRelativePath);
    }

    [Fact]
    public void PartialPath_ExcludesSelf()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\art\greek\units\infantry\hoplite\hoplite.xml.XMB"));

        var results = index.FindByPartialPath(
            @"greek\units\infantry\hoplite\hoplite",
            excludePath: @"game\art\greek\units\infantry\hoplite\hoplite.xml.XMB");
        Assert.Empty(results);
    }

    [Fact]
    public void PartialPath_MultipleResourceVariants()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\ui_myth_4k\resources\greek\player_color\units\hoplite_icon.png"));
        index.Add(MakeEntry(@"game\ui_myth\resources\greek\player_color\units\hoplite_icon.png"));

        var results = index.FindByPartialPath(@"resources\greek\player_color\units\hoplite_icon.png");
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void PartialPath_NoMatchWhenDirSegmentsMissing()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\art\units\hoplite\hoplite_iron.tmm"));

        // Parsed path has "infantry" segment that doesn't exist in actual path
        var results = index.FindByPartialPath(@"greek\units\infantry\hoplite\hoplite_iron");
        Assert.Empty(results);
    }

    [Fact]
    public void PartialPath_FileNameOnly()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\art\effects\impacts\hack.impacteffect.XMB"));

        // No directory segments — should match by stem alone
        var results = index.FindByPartialPath("hack");
        Assert.Single(results);
    }

    [Fact]
    public void PartialPath_WithExtension()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\art\vfx\popcornfx\Particles\impacts\crush\crush_unarmoured.pkfx"));

        var results = index.FindByPartialPath(@"impacts\crush\crush_unarmoured.pkfx");
        Assert.Single(results);
    }

    [Fact]
    public void PartialPath_RemoveCleansStemIndex()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\art\hoplite.tmm"));
        index.Remove(@"game\art\hoplite.tmm");

        var results = index.FindByPartialPath("hoplite");
        Assert.Empty(results);
    }
}
