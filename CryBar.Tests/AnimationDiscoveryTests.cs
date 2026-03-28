using CryBar.Export;

namespace CryBar.Tests;

public class AnimationDiscoveryTests
{
    [Fact]
    public void FindAnimations_BasicAnimFile()
    {
        var xml = """
            <animfile validation="strict">
                <component>ModelComp<assetreference type="TMModel"><file>events\turkey\turkey</file></assetreference></component>
                <anim>Idle<assetreference type="TMAnimation"><file>events\turkey\anim\turkey_idle_a</file></assetreference></anim>
                <anim>Walk<assetreference type="TMAnimation"><file>events\turkey\anim\turkey_walk_a</file></assetreference></anim>
            </animfile>
            """;

        var results = AnimationDiscovery.FindAnimationsFromAnimXml(xml);

        Assert.Equal(2, results.Count);
        Assert.Equal("Idle", results[0].AnimName);
        Assert.Contains("turkey_idle_a", results[0].TmaPath);
        Assert.Equal("Walk", results[1].AnimName);
        Assert.Contains("turkey_walk_a", results[1].TmaPath);
    }

    [Fact]
    public void FindAnimations_SkipsNonTMAnimation()
    {
        var xml = """
            <animfile>
                <anim>ModelRef<assetreference type="TMModel"><file>some\model</file></assetreference></anim>
                <anim>Attack<assetreference type="TMAnimation"><file>some\attack</file></assetreference></anim>
            </animfile>
            """;

        var results = AnimationDiscovery.FindAnimationsFromAnimXml(xml);

        Assert.Single(results);
        Assert.Equal("Attack", results[0].AnimName);
    }

    [Fact]
    public void FindAnimations_EmptyAnimFile()
    {
        var xml = "<animfile></animfile>";
        var results = AnimationDiscovery.FindAnimationsFromAnimXml(xml);
        Assert.Empty(results);
    }

    [Fact]
    public void FindAnimations_MalformedXml_ReturnsPartial()
    {
        var xml = """
            <animfile>
                <anim>Idle<assetreference type="TMAnimation"><file>path/idle</file></assetreference></anim>
                <broken
            """;

        var results = AnimationDiscovery.FindAnimationsFromAnimXml(xml);
        Assert.Single(results);
        Assert.Equal("Idle", results[0].AnimName);
    }

    [Fact]
    public void FindModel_BasicAnimFile()
    {
        var xml = """
            <animfile>
                <component>ModelComp<assetreference type="TMModel"><file>greek\hoplite\hoplite</file></assetreference></component>
                <anim>Idle<assetreference type="TMAnimation"><file>greek\hoplite\idle</file></assetreference></anim>
            </animfile>
            """;

        var model = AnimationDiscovery.FindModelFromAnimXml(xml);
        Assert.NotNull(model);
        Assert.Contains("hoplite", model);
    }

    [Fact]
    public void FindModel_NoComponent_ReturnsNull()
    {
        var xml = """
            <animfile>
                <anim>Idle<assetreference type="TMAnimation"><file>path/idle</file></assetreference></anim>
            </animfile>
            """;

        Assert.Null(AnimationDiscovery.FindModelFromAnimXml(xml));
    }

    [Fact]
    public void FindAllModels_NestedLogicElements()
    {
        // Real animfile structure: TMModel refs nested inside logic/tech elements
        var xml = """
            <animfile>
                <component>ModelComp<logic type="Cinematic"><normal><logic type="Tech">
                    <none><assetreference type="TMModel"><file>greek\hoplite\hoplite_iron</file></assetreference></none>
                    <mediuminfantry><assetreference type="TMModel"><file>greek\hoplite\hoplite_bronze</file></assetreference></mediuminfantry>
                    <heavyinfantry><assetreference type="TMModel"><file>greek\hoplite\hoplite_silver</file></assetreference></heavyinfantry>
                </logic></normal></logic></component>
                <anim>Idle<assetreference type="TMAnimation"><file>greek\hoplite\idle</file></assetreference></anim>
            </animfile>
            """;

        var models = AnimationDiscovery.FindAllModelsFromAnimXml(xml);
        Assert.Equal(3, models.Count);
        Assert.Contains(models, m => m.Contains("hoplite_iron"));
        Assert.Contains(models, m => m.Contains("hoplite_bronze"));
        Assert.Contains(models, m => m.Contains("hoplite_silver"));
    }

    [Fact]
    public void FindAllModels_SkipsNonTMModel()
    {
        var xml = """
            <animfile>
                <component>ModelComp<assetreference type="TMModel"><file>model_path</file></assetreference></component>
                <anim>Idle<assetreference type="TMAnimation"><file>anim_path</file></assetreference></anim>
            </animfile>
            """;

        var models = AnimationDiscovery.FindAllModelsFromAnimXml(xml);
        Assert.Single(models);
        Assert.Equal("model_path", models[0]);
    }
}
