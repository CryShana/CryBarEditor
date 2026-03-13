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
}
