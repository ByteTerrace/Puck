using System.Reflection;
using Puck.Commands;
using Puck.SdfVm;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>world.detail</c> echoes the live level choice, spells <c>auto</c> and the three levels, refuses
/// any other word, and submits a choice as the <c>detail</c> session lever carrying its number, without touching the
/// settings itself; the lever the server applies sets the settings.
/// </summary>
public sealed class WorldDetailLeverLawTests {
    private static (CommandRegistry Registry, WorldRenderSettings Settings, List<WorldSessionLever> Levers) Rig() {
        var settings = new WorldRenderSettings(defaults: Fixtures.BuildDocument().Render);
        var link = DispatchProxy.Create<IServerLink, WorldCullLeverLawTests.LeverRecorder>();
        var levers = ((WorldCullLeverLawTests.LeverRecorder)((object)link)).Levers;
        var module = new WorldRenderLeverCommandModule(link: link, renderProbe: null!, server: null!, settings: settings);

        return (new CommandRegistry(modules: [module]), settings, levers);
    }

    [InlineData(SdfMeshDetailLevel.Auto, "auto")]
    [InlineData(SdfMeshDetailLevel.Level0, "0")]
    [InlineData(SdfMeshDetailLevel.Level1, "1")]
    [InlineData(SdfMeshDetailLevel.Level2, "2")]
    [Theory]
    public void StatusEchoesTheLiveChoiceByItsWord(SdfMeshDetailLevel level, string word) {
        var (registry, settings, levers) = Rig();

        settings.Detail = level;

        Assert.Equal(expected: $"[world.detail: {word}]", actual: registry.Submit(line: "world.detail").Output);
        Assert.Equal(expected: $"[world.detail: {word}]", actual: registry.Submit(line: "world.detail status").Output);
        Assert.Empty(collection: levers);
    }
    [InlineData("auto", SdfMeshDetailLevel.Auto)]
    [InlineData("0", SdfMeshDetailLevel.Level0)]
    [InlineData("1", SdfMeshDetailLevel.Level1)]
    [InlineData("2", SdfMeshDetailLevel.Level2)]
    [Theory]
    public void AChoiceIsSubmittedAsTheDetailLever(string word, SdfMeshDetailLevel level) {
        var (registry, settings, levers) = Rig();
        var result = registry.Submit(line: $"world.detail {word}");
        var lever = Assert.Single(collection: levers);

        Assert.False(condition: result.IsError);
        Assert.Equal(expected: (WorldSessionLevers.Detail, ((double)level)), actual: (lever.Name, lever.A));
        Assert.Equal(expected: SdfMeshDetailLevel.Auto, actual: settings.Detail);
    }
    [InlineData("world.detail 3")]
    [InlineData("world.detail 1 2")]
    [Theory]
    public void AnythingElseIsRefusedNamingTheChoices(string line) {
        var (registry, _, levers) = Rig();
        var result = registry.Submit(line: line);

        Assert.True(condition: result.IsError);
        Assert.Contains(expectedSubstring: "auto|0|1|2|status]", actualString: result.Output);
        Assert.Empty(collection: levers);
    }
}
