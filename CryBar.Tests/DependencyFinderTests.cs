using CryBar;

namespace CryBar.Tests;

public class DependencyFinderTests
{
    static FileIndexEntry MakeEntry(string fullPath) => new()
    {
        FullRelativePath = fullPath,
        FileName = System.IO.Path.GetFileName(fullPath.Replace('\\', '/')),
        Source = FileIndexSource.BarEntry,
    };

    [Fact]
    public void Proto_ParsesPathsAndGroupsByUnit()
    {
        var content = """
            <proto>
                <unit name="Hoplite">
                    <displaynameid>STR_UNIT_HOPLITE_NAME</displaynameid>
                    <icon>resources\greek\player_color\units\hoplite_icon.png</icon>
                    <animfile>greek\units\infantry\hoplite\hoplite.xml</animfile>
                    <soundsetfile>greek\vo\hoplite\hoplite.xml</soundsetfile>
                </unit>
                <unit name="ProjectileArrow">
                    <animfile>vfx\projectiles\arrow\arrow.xml</animfile>
                </unit>
            </proto>
            """;

        var result = DependencyFinder.FindDependencies(content, @"game\data\gameplay\proto.xml.XMB");

        // Should have 2 entity groups
        Assert.Equal(2, result.Groups.Count);

        var hoplite = result.Groups.First(g => g.EntityName == "Hoplite");
        Assert.Equal("unit", hoplite.EntityType);

        // Hoplite should have: 1 icon path, 1 animfile path, 1 soundsetfile path, 3 STR_ keys
        var paths = hoplite.References.Where(r => r.Type == DependencyRefType.FilePath).ToList();
        var strKeys = hoplite.References.Where(r => r.Type == DependencyRefType.StringKey).ToList();

        Assert.Equal(3, paths.Count);
        Assert.Single(strKeys); // STR_UNIT_HOPLITE_NAME
        Assert.Contains(paths, r => r.RawValue.Contains("hoplite_icon.png"));
        Assert.Contains(paths, r => r.SourceTag == "animfile");
        Assert.Contains(paths, r => r.SourceTag == "soundsetfile");

        var arrow = result.Groups.First(g => g.EntityName == "ProjectileArrow");
        Assert.Single(arrow.References, r => r.Type == DependencyRefType.FilePath);
    }

    [Fact]
    public void Powers_ParsesIncludePaths()
    {
        var content = """
            <powers>
                <include>abilities\greek.abilities</include>
                <include>god_powers\greek.godpowers</include>
            </powers>
            """;

        var result = DependencyFinder.FindDependencies(content, @"game\data\gameplay\powers.xml.XMB");

        var paths = result.AllReferences.Where(r => r.Type == DependencyRefType.FilePath).ToList();
        Assert.Equal(2, paths.Count);
        Assert.Contains(paths, r => r.RawValue.Contains(@"abilities\greek.abilities"));
        Assert.Contains(paths, r => r.RawValue.Contains(@"god_powers\greek.godpowers"));
    }

    [Fact]
    public void GodPowers_ParsesSoundsetNames()
    {
        var content = """
            <powers>
                <power name="AOTGBolt" type="Bolt">
                    <icon>greek\static_color\god_powers\bolt_icon.png</icon>
                    <soundset type="StartSound" listenertype="IfOnScreenAll">LightningStrike</soundset>
                    <soundset type="StartSound" listenertype="AllExceptCaster">GodPowerStart</soundset>
                </power>
            </powers>
            """;

        var result = DependencyFinder.FindDependencies(content, @"game\data\gameplay\god_powers\aotg.godpowers.XMB");

        var soundsets = result.AllReferences.Where(r => r.Type == DependencyRefType.SoundsetName).ToList();
        Assert.Equal(2, soundsets.Count);
        Assert.Contains(soundsets, r => r.RawValue == "LightningStrike");
        Assert.Contains(soundsets, r => r.RawValue == "GodPowerStart");

        var paths = result.AllReferences.Where(r => r.Type == DependencyRefType.FilePath).ToList();
        Assert.Single(paths);
    }

    [Fact]
    public void AnimFile_ParsesAssetReferences()
    {
        var content = """
            <animfile validation="strict">
                <component>ModelComp<assetreference type="TMModel"><file>events\thanksgiving\units\turkey\turkey</file></assetreference></component>
                <anim>Idle<assetreference type="TMAnimation"><file>events\thanksgiving\units\turkey\anim\turkey_idle_a</file></assetreference></anim>
            </animfile>
            """;

        var result = DependencyFinder.FindDependencies(content, @"game\art\events\thanksgiving\units\turkey\turkey.xml.XMB");

        var paths = result.AllReferences.Where(r => r.Type == DependencyRefType.FilePath).ToList();
        Assert.Equal(2, paths.Count);
        Assert.Contains(paths, r => r.RawValue.Contains(@"turkey\turkey"));
        Assert.Contains(paths, r => r.RawValue.Contains("turkey_idle_a"));
    }

    [Fact]
    public void Tactics_ParsesImpactEffects()
    {
        var content = """
            <tactics>
                <action>
                    <name stringid="STR_ACTION_HAND">HandAttack</name>
                    <impacteffect>effects\impacts\hack</impacteffect>
                </action>
            </tactics>
            """;

        var result = DependencyFinder.FindDependencies(content, @"game\data\gameplay\tactics\hun_dun.tactics.XMB");

        var paths = result.AllReferences.Where(r => r.Type == DependencyRefType.FilePath).ToList();
        Assert.Single(paths);
        Assert.Equal(@"effects\impacts\hack", paths[0].RawValue);

        var strKeys = result.AllReferences.Where(r => r.Type == DependencyRefType.StringKey).ToList();
        Assert.Single(strKeys);
        Assert.Equal("STR_ACTION_HAND", strKeys[0].RawValue);
    }

    [Fact]
    public void CompositeJson_ParsesModelRefs()
    {
        var content = """
            {
                "entities": [{
                    "type": "model",
                    "model_ref": "atlantean\\units\\naval\\transport_ship_atlantean\\transport_ship_atlantean"
                }, {
                    "type": "model",
                    "model_ref": "shared\\units\\attachments\\rope\\ship_rope_03"
                }]
            }
            """;

        var result = DependencyFinder.FindDependencies(content,
            @"game\art\atlantean\units\naval\transport_ship_atlantean\transport_ship_atlantean_props.composite");

        var paths = result.AllReferences.Where(r => r.Type == DependencyRefType.FilePath).ToList();
        Assert.Equal(2, paths.Count);
        Assert.Contains(paths, r => r.RawValue.Contains(@"transport_ship_atlantean\transport_ship_atlantean"));
        Assert.Contains(paths, r => r.RawValue.Contains(@"ship_rope_03"));
    }

    [Fact]
    public void Resolution_MatchesPartialPaths()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\art\vfx\projectiles\arrow\arrow.xml.XMB"));
        index.Add(MakeEntry(@"game\art\greek\units\infantry\hoplite\hoplite.xml.XMB"));
        index.Add(MakeEntry(@"game\sound\greek\vo\hoplite\hoplite.xml.XMB"));
        index.Add(MakeEntry(@"game\ui_myth_4k\resources\greek\player_color\units\hoplite_icon.png"));
        index.Add(MakeEntry(@"game\ui_myth\resources\greek\player_color\units\hoplite_icon.png"));

        var content = """
            <proto>
                <unit name="Hoplite">
                    <icon>resources\greek\player_color\units\hoplite_icon.png</icon>
                    <animfile>greek\units\infantry\hoplite\hoplite.xml</animfile>
                    <soundsetfile>greek\vo\hoplite\hoplite.xml</soundsetfile>
                </unit>
                <unit name="ProjectileArrow">
                    <animfile>vfx\projectiles\arrow\arrow.xml</animfile>
                </unit>
            </proto>
            """;

        var result = DependencyFinder.FindDependencies(content, @"game\data\gameplay\proto.xml.XMB", index);

        // With 2+ <unit> elements, entity grouping kicks in
        var hoplite = result.Groups.First(g => g.EntityName == "Hoplite");
        var iconRef = hoplite.References.First(r => r.RawValue.Contains("hoplite_icon"));
        Assert.Equal(2, iconRef.Resolved.Count); // 4K + standard

        var animRef = hoplite.References.First(r => r.SourceTag == "animfile");
        Assert.Single(animRef.Resolved);
        Assert.Contains("art", animRef.Resolved[0].FullRelativePath);

        var soundRef = hoplite.References.First(r => r.SourceTag == "soundsetfile");
        Assert.Single(soundRef.Resolved);
        Assert.Contains("sound", soundRef.Resolved[0].FullRelativePath);
    }

    [Fact]
    public void SoundsetFile_ParsesSoundsetNames()
    {
        var content = """
            <protounitsounddef>
                <soundtype name="Select">
                    <soundset name="GreekMilitarySelect"></soundset>
                </soundtype>
                <soundtype name="Death">
                    <soundset name="DeathMale"></soundset>
                </soundtype>
            </protounitsounddef>
            """;

        var result = DependencyFinder.FindDependencies(content, @"game\sound\greek\vo\hoplite\hoplite.xml.XMB");

        var soundsets = result.AllReferences.Where(r => r.Type == DependencyRefType.SoundsetName).ToList();
        Assert.Equal(2, soundsets.Count);
        Assert.Contains(soundsets, r => r.RawValue == "GreekMilitarySelect");
        Assert.Contains(soundsets, r => r.RawValue == "DeathMale");
    }

    [Fact]
    public void Xaml_ParsesImageSources()
    {
        var content = """
            <ResourceDictionary>
                <ImageBrush x:Key="ResourceWoodIcon" ImageSource="/resources/in_game/res_wood.png"/>
                <ImageBrush x:Key="ResourceGoldIcon" ImageSource="/resources/in_game/res_gold.png"/>
            </ResourceDictionary>
            """;

        var result = DependencyFinder.FindDependencies(content, @"game\ui_myth\SystemResources.xaml");

        var paths = result.AllReferences.Where(r => r.Type == DependencyRefType.FilePath).ToList();
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public void StrKeys_Detected()
    {
        var content = """
            <unit name="Hoplite">
                <displaynameid>STR_UNIT_HOPLITE_NAME</displaynameid>
                <rollovertextid>STR_UNIT_HOPLITE_LR</rollovertextid>
            </unit>
            """;

        var result = DependencyFinder.FindDependencies(content, "test.xml");

        var strKeys = result.AllReferences.Where(r => r.Type == DependencyRefType.StringKey).ToList();
        Assert.Equal(2, strKeys.Count);
        Assert.Contains(strKeys, r => r.RawValue == "STR_UNIT_HOPLITE_NAME");
        Assert.Contains(strKeys, r => r.RawValue == "STR_UNIT_HOPLITE_LR");
    }

    [Fact]
    public void ParticleSet_ParsesFilenames()
    {
        var content = """
            <particlesetdef>
                <particleset name="Impact_CrushUnarmoured_VFX">
                    <particle filename="impacts\crush\crush_unarmoured.pkfx"></particle>
                </particleset>
                <particleset name="Impact_CrushArmoured_VFX">
                    <particle filename="impacts\crush\crush_armoured.pkfx"></particle>
                </particleset>
            </particlesetdef>
            """;

        var result = DependencyFinder.FindDependencies(content, @"game\art\effects\particlesets.xml.XMB");

        // Should group by particleset entity
        Assert.Equal(2, result.Groups.Count);
        Assert.Equal("Impact_CrushUnarmoured_VFX", result.Groups[0].EntityName);
        Assert.Equal("Impact_CrushArmoured_VFX", result.Groups[1].EntityName);
    }

    [Fact]
    public void SelfExclusion_DoesNotMatchOwnPath()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"game\art\events\turkey\turkey.xml.XMB"));

        var content = """
            <animfile>
                <component>ModelComp<assetreference type="TMModel"><file>events\turkey\turkey</file></assetreference></component>
            </animfile>
            """;

        // The parsed path "events\turkey\turkey" should not resolve to the entry itself
        var result = DependencyFinder.FindDependencies(content,
            @"game\art\events\turkey\turkey.xml.XMB", index);

        var paths = result.AllReferences.Where(r => r.Type == DependencyRefType.FilePath).ToList();
        Assert.Single(paths);
        // The stem "turkey" matches, but the entry itself should be excluded
        Assert.Empty(paths[0].Resolved);
    }
}
