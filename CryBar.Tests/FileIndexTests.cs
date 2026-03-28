using System.IO;

using CryBar;
using CryBar.Dependencies;
using CryBar.Indexing;

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

    [Fact]
    public void Find_Material_DoesNotMatchTmmOrFbximport()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"intermediate\modelcache\armory_a_age2.tmm"));
        index.Add(MakeEntry(@"intermediate\modelcache\armory_a_age2.tmm.data"));
        index.Add(MakeEntry(@"game\art\armory_a_age2.material.XMB"));
        index.Add(MakeEntry(@"game\art\armory_a_age2.fbximport"));

        // Searching for ".material" must NOT return ".tmm" or ".fbximport"
        var results = index.Find("armory_a_age2.material");
        Assert.Single(results);
        Assert.Equal("armory_a_age2.material.XMB", results[0].FileName);
    }

    [Fact]
    public void Find_MaterialXmb_ExactMatch()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"intermediate\modelcache\armory_a_age2.tmm"));
        index.Add(MakeEntry(@"game\art\armory_a_age2.material.XMB"));

        var results = index.Find("armory_a_age2.material.XMB");
        Assert.Single(results);
        Assert.Equal("armory_a_age2.material.XMB", results[0].FileName);
    }

    [Fact]
    public void Find_ExtensionlessQuery_MatchesKnownExtensions()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\art\armory_a_age2.tmm"));
        index.Add(MakeEntry(@"game\art\armory_a_age2.ddt"));

        // Extensionless query matches files with known extensions (.tmm, .ddt)
        var results = index.Find("armory_a_age2");
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Find_ExtensionlessQuery_FallsBackToPrefix()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\art\armory_a_age2.fbximport"));
        index.Add(MakeEntry(@"game\art\armory_a_age2.composite"));

        // No known ext matches -> fallback to prefix matching
        var results = index.Find("armory_a_age2");
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Find_CompoundExtension_DoesNotMatchDifferentFile()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\art\armory_a_age2.tmm"));
        index.Add(MakeEntry(@"game\art\armory_a_age2.fbximport"));

        // Query with specific compound extension should not match other files with same stem
        var results = index.Find("armory_a_age2.tmm.material");
        Assert.Empty(results); // no .tmm.material or .tmm.material.XMB exists
    }

    [Fact]
    public void Find_KnownExtStripping_MatchesFileWithoutXmb()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\data\proto.xml"));

        // Query with .xmb should find entry without .xmb (known extension stripping)
        var results = index.Find("proto.xml.xmb");
        Assert.Single(results);
        Assert.Equal("proto.xml", results[0].FileName);
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

        // No directory segments - should match by stem alone
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

    // --- Root directory scenario tests ---

    [Fact]
    public void Scenario_GameRoot_MaterialsFound()
    {
        var index = new FileIndex();

        // All BARs are indexed when root = game\
        index.Add(MakeEntry(@"intermediate\modelcache\armory_a_age2.tmm", "modelcache.bar"));
        index.Add(MakeEntry(@"intermediate\modelcache\armory_a_age2.tmm.data", "modelcache_data.bar"));
        index.Add(MakeEntry(@"game\art\armory_a_age2.material.XMB", "art.bar"));
        index.Add(MakeEntry(@"game\art\textures\armory_a_age2_basecolor.ddt", "art_tex.bar"));

        // Material resolution succeeds
        var materialResults = index.Find("armory_a_age2.material.XMB");
        Assert.Single(materialResults);
        var textureResults = index.Find("armory_a_age2_basecolor.ddt");
        Assert.Single(textureResults);
    }

    [Fact]
    public void Scenario_SubdirectoryRoot_MaterialsMissing()
    {
        var index = new FileIndex();

        // Only modelcache entries exist (art BARs were never scanned)
        index.Add(MakeEntry(@"intermediate\modelcache\armory_a_age2.tmm", "modelcache.bar"));
        index.Add(MakeEntry(@"intermediate\modelcache\armory_a_age2.tmm.data", "modelcache_data.bar"));

        // Material resolution FAILS - proves the bug
        var materialResults = index.Find("armory_a_age2.material.XMB");
        Assert.Empty(materialResults);
        var textureResults = index.Find("armory_a_age2_basecolor.ddt");
        Assert.Empty(textureResults);
    }

    [Fact]
    public void Scenario_SubdirectoryRoot_WithSupplemental_MaterialsFound()
    {
        var index = new FileIndex();

        // Modelcache entries (from normal scan)
        index.Add(MakeEntry(@"intermediate\modelcache\armory_a_age2.tmm", "modelcache.bar"));
        index.Add(MakeEntry(@"intermediate\modelcache\armory_a_age2.tmm.data", "modelcache_data.bar"));
        // Supplemental entries (from parent dir BAR scan)
        index.Add(MakeEntry(@"game\art\armory_a_age2.material.XMB", @"C:\game\art\ArtGreek.bar"));
        index.Add(MakeEntry(@"game\art\textures\armory_a_age2_basecolor.ddt", @"C:\game\art\ArtTextures.bar"));

        // Now material resolution succeeds
        var materialResults = index.Find("armory_a_age2.material.XMB");
        Assert.Single(materialResults);
        var textureResults = index.Find("armory_a_age2_basecolor.ddt");
        Assert.Single(textureResults);
    }

    [Fact]
    public void Scenario_ParentRoot_MaterialsFound()
    {
        var index = new FileIndex();

        // When root is parent of game, root files include game\ in their relative path
        // BAR entries still use bar.RootPath (e.g., "game", "intermediate")
        index.Add(MakeEntry(@"intermediate\modelcache\armory_a_age2.tmm", "modelcache.bar"));
        index.Add(MakeEntry(@"intermediate\modelcache\armory_a_age2.tmm.data", "modelcache_data.bar"));
        index.Add(MakeEntry(@"game\art\armory_a_age2.material.XMB", "art.bar"));
        index.Add(MakeEntry(@"game\art\textures\armory_a_age2_basecolor.ddt", "art_tex.bar"));

        // Works identically to scenario 1
        var materialResults = index.Find("armory_a_age2.material.XMB");
        Assert.Single(materialResults);
        var textureResults = index.Find("armory_a_age2_basecolor.ddt");
        Assert.Single(textureResults);
    }

    [Fact]
    public void Scenario_SubdirectoryRoot_CompanionDataInSameBar()
    {
        var index = new FileIndex();

        index.Add(MakeEntry(@"intermediate\modelcache\armory_a_age2.tmm", "modelcache.bar"));
        index.Add(MakeEntry(@"intermediate\modelcache\armory_a_age2.tmm.data", "modelcache.bar"));

        // Companion data can be found by filename
        var dataResults = index.Find("armory_a_age2.tmm.data");
        Assert.Single(dataResults);
    }

    [Fact]
    public void Scenario_SubdirectoryRoot_FindByPartialPath_MaterialMissing()
    {
        var index = new FileIndex();

        // Only modelcache entries
        index.Add(MakeEntry(@"intermediate\modelcache\armory_a_age2.tmm", "modelcache.bar"));

        // FindByPartialPath for a path that references art content
        var results = index.FindByPartialPath(@"art\armory_a_age2.material");
        Assert.Empty(results);
    }

    // --- IsExternal property tests ---

    [Fact]
    public void IsExternal_DefaultsFalse()
    {
        var entry = MakeEntry(@"game\art\test.ddt");
        Assert.False(entry.IsExternal);
    }

    [Fact]
    public void IsExternal_CanBeSetTrue()
    {
        var entry = new FileIndexEntry
        {
            FullRelativePath = @"game\art\test.ddt",
            FileName = "test.ddt",
            Source = FileIndexSource.BarEntry,
            IsExternal = true,
        };
        Assert.True(entry.IsExternal);
    }

    // --- DependencyReference computed property tests ---

    [Fact]
    public void DependencyReference_AllResolvedExternal()
    {
        var reference = new DependencyReference
        {
            RawValue = "test.material.XMB",
            Type = DependencyRefType.FilePath,
        };
        reference.Resolved.Add(new FileIndexEntry
        {
            FullRelativePath = @"game\art\test.material.XMB",
            FileName = "test.material.XMB",
            Source = FileIndexSource.BarEntry,
            BarFilePath = @"C:\other\Art.bar",
            IsExternal = true,
        });
        Assert.True(reference.AllResolvedExternal);
        Assert.True(reference.AnyResolvedExternal);
        Assert.NotNull(reference.ExternalTooltip);
        Assert.Contains(@"C:\other\Art.bar", reference.ExternalTooltip);
    }

    [Fact]
    public void DependencyReference_MixedExternalAndLocal()
    {
        var reference = new DependencyReference
        {
            RawValue = "test.material.XMB",
            Type = DependencyRefType.FilePath,
        };
        reference.Resolved.Add(new FileIndexEntry
        {
            FullRelativePath = @"game\art\test.material.XMB",
            FileName = "test.material.XMB",
            Source = FileIndexSource.BarEntry,
            BarFilePath = @"C:\game\Art.bar",
            IsExternal = false,
        });
        reference.Resolved.Add(new FileIndexEntry
        {
            FullRelativePath = @"game\art\test.material.XMB",
            FileName = "test.material.XMB",
            Source = FileIndexSource.BarEntry,
            BarFilePath = @"C:\other\Art.bar",
            IsExternal = true,
        });
        Assert.False(reference.AllResolvedExternal);
        Assert.True(reference.AnyResolvedExternal);
        Assert.NotNull(reference.ExternalTooltip);
    }

    [Fact]
    public void DependencyReference_NoResolvedEntries_NotExternal()
    {
        var reference = new DependencyReference
        {
            RawValue = "missing.ddt",
            Type = DependencyRefType.FilePath,
        };
        Assert.False(reference.AllResolvedExternal);
        Assert.False(reference.AnyResolvedExternal);
        Assert.Null(reference.ExternalTooltip);
    }

    [Fact]
    public void DependencyReference_AllLocal_NotExternal()
    {
        var reference = new DependencyReference
        {
            RawValue = "test.ddt",
            Type = DependencyRefType.FilePath,
        };
        reference.Resolved.Add(new FileIndexEntry
        {
            FullRelativePath = @"game\art\test.ddt",
            FileName = "test.ddt",
            Source = FileIndexSource.BarEntry,
            BarFilePath = @"C:\game\Art.bar",
            IsExternal = false,
        });
        Assert.False(reference.AllResolvedExternal);
        Assert.False(reference.AnyResolvedExternal);
        Assert.Null(reference.ExternalTooltip);
    }
}
