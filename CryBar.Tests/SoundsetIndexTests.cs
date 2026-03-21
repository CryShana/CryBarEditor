using CryBar;
using CryBar.Indexing;
using CryBar.Sound;

namespace CryBar.Tests;

public class SoundsetIndexTests
{
    static FileIndexEntry MakeEntry(string fullPath) => new()
    {
        FullRelativePath = fullPath,
        FileName = System.IO.Path.GetFileName(fullPath.Replace('\\', '/')),
        Source = FileIndexSource.BarEntry,
    };

    [Fact]
    public void Find_ReturnsMatchingEntry()
    {
        var index = new SoundsetIndex();
        var soundsetFile = MakeEntry(@"game\sound\soundsets_greek.soundset.XMB");
        var bankFile = MakeEntry(@"game\sound\banks\Desktop\greek.bank");

        var definitions = new List<SoundsetDefinition>
        {
            new() { Name = "GreekMilitarySelect", Sounds = [] },
            new() { Name = "GreekMilitaryDeath", Sounds = [] },
        };

        index.AddFromParsedFile(definitions, "greek", soundsetFile, bankFile);

        var result = index.Find("GreekMilitarySelect");
        Assert.NotNull(result);
        Assert.Equal("greek", result.Culture);
        Assert.Equal(soundsetFile, result.SoundsetFile);
        Assert.Equal(bankFile, result.BankFile);
    }

    [Fact]
    public void Find_CaseInsensitive()
    {
        var index = new SoundsetIndex();
        var file = MakeEntry(@"game\sound\soundsets_greek.soundset.XMB");

        index.AddFromParsedFile(
            [new() { Name = "GreekMilitarySelect", Sounds = [] }],
            "greek", file, null);

        Assert.NotNull(index.Find("greekmilitaryselect"));
        Assert.NotNull(index.Find("GREEKMILITARYSELECT"));
    }

    [Fact]
    public void Find_ReturnsNullForMissing()
    {
        var index = new SoundsetIndex();
        Assert.Null(index.Find("NonExistent"));
    }

    [Fact]
    public void MultipleCultures()
    {
        var index = new SoundsetIndex();
        var greekFile = MakeEntry(@"game\sound\soundsets_greek.soundset.XMB");
        var greekBank = MakeEntry(@"game\sound\banks\Desktop\greek.bank");
        var norseFile = MakeEntry(@"game\sound\soundsets_norse.soundset.XMB");
        var norseBank = MakeEntry(@"game\sound\banks\Desktop\norse.bank");

        index.AddFromParsedFile(
            [new() { Name = "GreekMilitarySelect", Sounds = [] }],
            "greek", greekFile, greekBank);

        index.AddFromParsedFile(
            [new() { Name = "NorseVillagerSelect", Sounds = [] }],
            "norse", norseFile, norseBank);

        var greek = index.Find("GreekMilitarySelect");
        Assert.NotNull(greek);
        Assert.Equal("greek", greek.Culture);
        Assert.Equal(greekBank, greek.BankFile);

        var norse = index.Find("NorseVillagerSelect");
        Assert.NotNull(norse);
        Assert.Equal("norse", norse.Culture);
        Assert.Equal(norseBank, norse.BankFile);
    }

    [Fact]
    public void NullBankFile()
    {
        var index = new SoundsetIndex();
        var file = MakeEntry(@"game\sound\soundsets_shared.soundset.XMB");

        index.AddFromParsedFile(
            [new() { Name = "SharedUIClick", Sounds = [] }],
            "shared", file, null);

        var result = index.Find("SharedUIClick");
        Assert.NotNull(result);
        Assert.Null(result.BankFile);
    }

    [Fact]
    public void Count_TracksEntries()
    {
        var index = new SoundsetIndex();
        Assert.Equal(0, index.Count);

        var file = MakeEntry(@"game\sound\soundsets_greek.soundset.XMB");
        index.AddFromParsedFile(
            [
                new() { Name = "GreekMilitarySelect", Sounds = [] },
                new() { Name = "GreekMilitaryDeath", Sounds = [] },
            ],
            "greek", file, null);

        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void Clear_RemovesAll()
    {
        var index = new SoundsetIndex();
        var file = MakeEntry(@"game\sound\soundsets_greek.soundset.XMB");
        index.AddFromParsedFile(
            [new() { Name = "GreekMilitarySelect", Sounds = [] }],
            "greek", file, null);

        index.Clear();
        Assert.Equal(0, index.Count);
        Assert.Null(index.Find("GreekMilitarySelect"));
    }

    [Theory]
    [InlineData("soundsets_greek.soundset.XMB", "greek")]
    [InlineData("soundsets_egyptian.soundset", "egyptian")]
    [InlineData("soundsets_norse.soundset.XMB", "norse")]
    [InlineData("soundsets_shared.soundset.XMB", "shared")]
    [InlineData("proto.xml.XMB", null)]
    [InlineData("soundsets_.soundset.XMB", null)]
    public void ExtractCulture(string fileName, string? expected)
    {
        Assert.Equal(expected, SoundsetIndex.ExtractCulture(fileName));
    }
}
