using CryBar;
using CryBar.Dependencies;
using CryBar.Indexing;
using CryBar.Sound;

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

        var paths = result.GetAllReferences().Where(r => r.Type == DependencyRefType.FilePath).ToList();
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

        var soundsets = result.GetAllReferences().Where(r => r.Type == DependencyRefType.SoundsetName).ToList();
        Assert.Equal(2, soundsets.Count);
        Assert.Contains(soundsets, r => r.RawValue == "LightningStrike");
        Assert.Contains(soundsets, r => r.RawValue == "GodPowerStart");

        var paths = result.GetAllReferences().Where(r => r.Type == DependencyRefType.FilePath).ToList();
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

        var paths = result.GetAllReferences().Where(r => r.Type == DependencyRefType.FilePath).ToList();
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

        var paths = result.GetAllReferences().Where(r => r.Type == DependencyRefType.FilePath).ToList();
        Assert.Single(paths);
        Assert.Equal(@"effects\impacts\hack", paths[0].RawValue);

        var strKeys = result.GetAllReferences().Where(r => r.Type == DependencyRefType.StringKey).ToList();
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

        var paths = result.GetAllReferences().Where(r => r.Type == DependencyRefType.FilePath).ToList();
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

        var soundsets = result.GetAllReferences().Where(r => r.Type == DependencyRefType.SoundsetName).ToList();
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

        var paths = result.GetAllReferences().Where(r => r.Type == DependencyRefType.FilePath).ToList();
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

        var strKeys = result.GetAllReferences().Where(r => r.Type == DependencyRefType.StringKey).ToList();
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
    public async Task Tmm_MaterialResolvesToMaterialXmb_NotTmmFile()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"intermediate\modelcache\greek\armory_a_age2.tmm"));
        index.Add(MakeEntry(@"intermediate\modelcache\greek\armory_a_age2.tmm.data"));
        index.Add(MakeEntry(@"game\art\greek\armory_a_age2.material.XMB"));
        index.Add(MakeEntry(@"game\art\greek\armory_a_age2.fbximport"));

        var result = await DependencyFinder.FindDependenciesForTmmAsync(
            @"intermediate\modelcache\greek\armory_a_age2.tmm", index);

        Assert.Equal(2, result.Groups[0].References.Count);

        var dataRef = result.Groups[0].References.First(r => r.SourceTag == "geometry");
        Assert.Single(dataRef.Resolved);
        Assert.Equal("armory_a_age2.tmm.data", dataRef.Resolved[0].FileName);

        // Material: .tmm is stripped -> searches for armory_a_age2.material.XMB
        var matRef = result.Groups[0].References.First(r => r.SourceTag == "material");
        Assert.Single(matRef.Resolved);
        Assert.Equal("armory_a_age2.material.XMB", matRef.Resolved[0].FileName);

        // No animfile ref - armory_a_age2.xml.XMB doesn't exist in index
        Assert.DoesNotContain(result.Groups[0].References, r => r.SourceTag == "animfile");
    }

    [Fact]
    public async Task Tmm_AnimfileNotShown_WhenNoReadDelegate()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"intermediate\modelcache\greek\hoplite.tmm"));

        var result = await DependencyFinder.FindDependenciesForTmmAsync(
            @"intermediate\modelcache\greek\hoplite.tmm", index);

        Assert.DoesNotContain(result.Groups[0].References, r => r.SourceTag == "animfile");
    }

    [Fact]
    public async Task Tmm_MaterialResolvesEmpty_WhenNoMaterialFileExists()
    {
        var index = new FileIndex();
        index.Add(MakeEntry(@"intermediate\modelcache\greek\armory_a_age2.tmm"));
        index.Add(MakeEntry(@"game\art\greek\armory_a_age2.fbximport"));

        var result = await DependencyFinder.FindDependenciesForTmmAsync(
            @"intermediate\modelcache\greek\armory_a_age2.tmm", index);

        var matRef = result.Groups[0].References.First(r => r.SourceTag == "material");
        Assert.Empty(matRef.Resolved); // should NOT resolve to .tmm or .fbximport
    }

    [Fact]
    public void ChildNameElement_GroupsByNameChildInsteadOfAttribute()
    {
        var content = """
            <ages>
                <age>
                    <name>ArchaicAge</name>
                    <displaynameid>STR_AGE_ARCHAIC</displaynameid>
                    <icon>resources\in_game\score_age_1.png</icon>
                    <smallicon>resources\postgame\timeline\Icon_Age1Small.png</smallicon>
                </age>
                <age>
                    <name>ClassicalAge</name>
                    <displaynameid>STR_AGE_CLASSICAL</displaynameid>
                    <icon>resources\in_game\score_age_2.png</icon>
                    <smallicon>resources\postgame\timeline\Icon_Age2Small.png</smallicon>
                </age>
            </ages>
            """;

        var result = DependencyFinder.FindDependencies(content, "game\\data\\ages.xml");
        Assert.Equal(2, result.Groups.Count);
        Assert.Equal("ArchaicAge", result.Groups[0].EntityName);
        Assert.Equal("ClassicalAge", result.Groups[1].EntityName);
    }

    [Fact]
    public void ChildNameElement_FavorStashItems_GroupsByNameChild()
    {
        var content = """
            <favorstashitems>
                <favorstashitem>
                    <name>ClaimFavor</name>
                    <titleid>STR_FAVOR_BONUS_CLAIM_FAVOR</titleid>
                    <icon>resources\ui\favor_icon.png</icon>
                </favorstashitem>
                <favorstashitem>
                    <name>SpendFavor</name>
                    <titleid>STR_FAVOR_BONUS_SPEND_FAVOR</titleid>
                    <icon>resources\ui\favor_spend.png</icon>
                </favorstashitem>
            </favorstashitems>
            """;

        var result = DependencyFinder.FindDependencies(content, "game\\data\\favorstash.xml");
        Assert.Equal(2, result.Groups.Count);
        Assert.Equal("ClaimFavor", result.Groups[0].EntityName);
        Assert.Equal("SpendFavor", result.Groups[1].EntityName);
    }

    [Fact]
    public void HttpUrls_NotParsedAsPaths()
    {
        var content = """
            <root xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                  xmlns:x="https://schemas.microsoft.com/winfx/2009/xaml">
                <icon>resources\in_game\icon.png</icon>
            </root>
            """;

        var result = DependencyFinder.FindDependencies(content, "test.xaml");

        var paths = result.GetAllReferences().Where(r => r.Type == DependencyRefType.FilePath).ToList();
        // Should only find the icon path, not the URI host/path fragments
        Assert.Single(paths);
        Assert.Contains("icon.png", paths[0].RawValue);
    }

    [Fact]
    public void XmlTags_NotParsedAsSingleSegmentFiles()
    {
        var content = """
            <Style>
                <Setter.Value></Setter.Value>
                <Grid.ColumnDefinitions></Grid.ColumnDefinitions>
                <icon>handattack.tactics</icon>
            </Style>
            """;

        var result = DependencyFinder.FindDependencies(content, "test.xaml");

        var paths = result.GetAllReferences().Where(r => r.Type == DependencyRefType.FilePath).ToList();
        // Should find handattack.tactics but NOT Setter.Value or Grid.ColumnDefinitions
        Assert.Single(paths);
        Assert.Equal("handattack.tactics", paths[0].RawValue);
    }

    [Fact]
    public void XmlContent_SingleSegmentFilesStillMatchInsideTags()
    {
        // Ensure the <> lookbehind/lookahead doesn't break matches inside element content
        var content = """
            <root>
                <reference>somefile.tactics</reference>
                <item>config.dat</item>
                <path attr="value.ext">another.material</path>
            </root>
            """;

        var result = DependencyFinder.FindDependencies(content, "test.xml");

        var paths = result.GetAllReferences().Where(r => r.Type == DependencyRefType.FilePath).ToList();
        Assert.Equal(4, paths.Count);
        Assert.Contains(paths, r => r.RawValue == "somefile.tactics");
        Assert.Contains(paths, r => r.RawValue == "config.dat");
        Assert.Contains(paths, r => r.RawValue == "value.ext");
        Assert.Contains(paths, r => r.RawValue == "another.material");
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

        var paths = result.GetAllReferences().Where(r => r.Type == DependencyRefType.FilePath).ToList();
        Assert.Single(paths);
        // The stem "turkey" matches, but the entry itself should be excluded
        Assert.Empty(paths[0].Resolved);
    }

    [Fact]
    public void ChildIdElement_GroupsByIdChild()
    {
        var content = """
            <?xml version="1.0" encoding="utf-8"?>
            <CinematicData>
                <Portrait>
                    <ID>STR_PORTRAIT_ATHE</ID>
                    <Icon>spc\player_color\units\athena_icon.png</Icon>
                    <TalkingHead>talking_heads/athena/athena_spc_neutral.png</TalkingHead>
                </Portrait>
                <Portrait>
                    <ID>STR_PORTRAIT_ARKA</ID>
                    <Icon>spc\player_color\units\arkantos_icon.png</Icon>
                    <TalkingHead>talking_heads/arkantos/arkantos_spc_neutral.png</TalkingHead>
                </Portrait>
                <Portrait>
                    <ID>STR_PORTRAIT_ARKAU</ID>
                    <Icon>spc\player_color\units\arkantos_uber_icon.png</Icon>
                    <TalkingHead>talking_heads/arkantos_uber/arkantos_uber_spc_neutral.png</TalkingHead>
                </Portrait>
            </CinematicData>
            """;

        var result = DependencyFinder.FindDependencies(content, "game\\data\\cinematic.xml");
        Assert.Equal(3, result.Groups.Count);
        Assert.Equal("STR_PORTRAIT_ATHE", result.Groups[0].EntityName);
        Assert.Equal("STR_PORTRAIT_ARKA", result.Groups[1].EntityName);
        Assert.Equal("STR_PORTRAIT_ARKAU", result.Groups[2].EntityName);
        Assert.Equal("portrait", result.Groups[0].EntityType);
    }

    [Fact]
    public void ChildIdElement_GroupsByIdChild_WithBom()
    {
        var content = "\uFEFF" + """
            <?xml version="1.0" encoding="utf-8"?>
            <CinematicData>
                <Portrait>
                    <ID>STR_PORTRAIT_ATHE</ID>
                    <Icon>spc\player_color\units\athena_icon.png</Icon>
                </Portrait>
                <Portrait>
                    <ID>STR_PORTRAIT_ARKA</ID>
                    <Icon>spc\player_color\units\arkantos_icon.png</Icon>
                </Portrait>
            </CinematicData>
            """;

        var result = DependencyFinder.FindDependencies(content, "game\\data\\cinematic.xml");
        Assert.Equal(2, result.Groups.Count);
        Assert.Equal("STR_PORTRAIT_ATHE", result.Groups[0].EntityName);
        Assert.Equal("STR_PORTRAIT_ARKA", result.Groups[1].EntityName);
    }

    [Fact]
    public void Comments_SkippedDuringGrouping()
    {
        var content = """
            <!-- This is a comment that should be ignored -->
            <ChatSets>
                <Chatset name="Shared">
                    <Tag name="ToAllyCompletedTownCenter" priority="Background">
                        <Sentence>
                            <String>A new city joins my empire.</String>
                            <StringID>STR_AI_CHAT_COMPLETE_TC_1</StringID>
                        </Sentence>
                        <Sentence>
                            <String>Aha! Another city sings my praises.</String>
                            <StringID>STR_AI_CHAT_COMPLETE_TC_2</StringID>
                        </Sentence>
                    </Tag>
                </Chatset>
            </ChatSets>
            """;

        var result = DependencyFinder.FindDependencies(content, "game\\data\\chat.xml");

        // Single <Chatset name="Shared"> should still be grouped (Rule 2)
        Assert.Contains(result.Groups, g => g.EntityName == "Shared");
        Assert.Contains(result.Groups, g => g.EntityType == "chatset");

        var shared = result.Groups.First(g => g.EntityName == "Shared");
        var strKeys = shared.References.Where(r => r.Type == DependencyRefType.StringKey).ToList();
        Assert.Equal(2, strKeys.Count);
    }

    [Fact]
    public void FilenameAttribute_GroupsByFilenameAttr()
    {
        var content = """
            <cinematicpreloaddata>
                <cinematic filename="game\campaign\fott\cinematics\fott01_a">
                    <preload filename="\lighting\luts\snow_lut2" preloadtype="0" />
                    <preload filename="\vfx\textures\emissive_crawling02" preloadtype="0" />
                </cinematic>
                <cinematic filename="game\campaign\fott\fott01">
                    <preload filename="\vfx\textures\decals\ground\ground_ice_normals" preloadtype="0" />
                </cinematic>
            </cinematicpreloaddata>
            """;

        var result = DependencyFinder.FindDependencies(content, "game\\data\\cinematicpreload.xml");
        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(@"game\campaign\fott\cinematics\fott01_a", result.Groups[0].EntityName);
        Assert.Equal(@"game\campaign\fott\fott01", result.Groups[1].EntityName);
        Assert.Equal("cinematic", result.Groups[0].EntityType);
    }

    [Fact]
    public void UniqueTagChildren_GroupByTagName()
    {
        var content = """
            <ringmenufindmenu>
                <greekbuildings>
                    <temple icon="resources\shared\static_color\buildings\temple_icon.png" stringid="STR_GRM_FIND_BUILDINGS_TEMPLE"></temple>
                    <dock icon="resources\shared\static_color\buildings\dock_icon.png" stringid="STR_GRM_FIND_BUILDINGS_DOCK"></dock>
                </greekbuildings>
                <greekunits>
                    <abstractscout icon="\resources\shared\static_color\abilities\auto_scout_ability_icon.png" stringid="STR_GRM_FIND_UNITS_SCOUT"></abstractscout>
                    <mythunit icon="resources\greek\player_color\units\cyclops_icon.png" stringid="STR_GRM_FIND_UNITS_MYTH"></mythunit>
                </greekunits>
                <egyptianbuildings>
                    <temple icon="resources\shared\static_color\buildings\temple_icon.png" stringid="STR_GRM_FIND_BUILDINGS_TEMPLE"></temple>
                    <armory icon="resources\shared\static_color\buildings\armory_icon.png" stringid="STR_GRM_FIND_BUILDINGS_ARMORY"></armory>
                </egyptianbuildings>
            </ringmenufindmenu>
            """;

        var result = DependencyFinder.FindDependencies(content, "game\\data\\ringmenu.xml");

        // 3 unique-tag children -> 3 groups named by tag
        Assert.Equal(3, result.Groups.Count);
        Assert.Equal("greekbuildings", result.Groups[0].EntityName);
        Assert.Equal("greekunits", result.Groups[1].EntityName);
        Assert.Equal("egyptianbuildings", result.Groups[2].EntityName);

        // Each group should have its own refs
        var greekBuildings = result.Groups.First(g => g.EntityName == "greekbuildings");
        var strKeys = greekBuildings.References.Where(r => r.Type == DependencyRefType.StringKey).ToList();
        Assert.Contains(strKeys, r => r.RawValue == "STR_GRM_FIND_BUILDINGS_TEMPLE");
        Assert.Contains(strKeys, r => r.RawValue == "STR_GRM_FIND_BUILDINGS_DOCK");
    }

    [Fact]
    public void SingleNamedChild_StillGrouped()
    {
        // A file with only one named direct child should still create a named group (Rule 2)
        var content = """
            <root>
                <category name="Primary">
                    <item>resources\icons\primary_icon.png</item>
                </category>
            </root>
            """;

        var result = DependencyFinder.FindDependencies(content, "game\\data\\test.xml");
        Assert.Contains(result.Groups, g => g.EntityName == "Primary");
    }
}
