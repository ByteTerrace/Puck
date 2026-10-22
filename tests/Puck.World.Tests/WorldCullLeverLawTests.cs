using System.Reflection;
using Puck.Commands;
using Puck.SdfVm;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>world.cull</c> echoes the live cull level, spells the three levels (<c>on</c> is
/// <c>occlusion</c>), refuses any other word, and submits a level as the <c>cull</c> session lever carrying the level's
/// number, without touching the settings itself: the server's grant check applies the lever.
/// </summary>
public sealed class WorldCullLeverLawTests {
    private static (CommandRegistry Registry, WorldRenderSettings Settings, List<WorldSessionLever> Levers) Rig() {
        var settings = new WorldRenderSettings(defaults: Fixtures.BuildDocument().Render);
        var link = DispatchProxy.Create<IServerLink, LeverRecorder>();
        var levers = ((LeverRecorder)((object)link)).Levers;
        // The command is wired to the link and the settings alone; the server and the render probe serve other verbs.
        var module = new WorldRenderLeverCommandModule(
            link: link,
            renderProbe: null!,
            server: null!,
            settings: settings
        );

        return (new CommandRegistry(modules: [module]), settings, levers);
    }

    [Fact]
    public void NoArgumentAndStatusEchoTheLiveLevel() {
        var (registry, settings, levers) = Rig();

        Assert.Equal(
            expected: "[world.cull: occlusion]",
            actual: registry.Submit(line: "world.cull").Output
        );

        settings.Cull = SdfMeshCullLevel.Frustum;

        Assert.Equal(
            expected: "[world.cull: frustum]",
            actual: registry.Submit(line: "world.cull status").Output
        );
        Assert.Empty(collection: levers);
    }
    [InlineData("on", SdfMeshCullLevel.Occlusion)]
    [InlineData("occlusion", SdfMeshCullLevel.Occlusion)]
    [InlineData("frustum", SdfMeshCullLevel.Frustum)]
    [InlineData("off", SdfMeshCullLevel.Off)]
    [Theory]
    public void ALevelIsSubmittedAsTheCullLever(string word, SdfMeshCullLevel level) {
        var (registry, settings, levers) = Rig();
        var result = registry.Submit(line: $"world.cull {word}");

        Assert.False(condition: result.IsError);

        var lever = Assert.Single(collection: levers);

        Assert.Equal(
            expected: (WorldSessionLevers.Cull, ((double)level)),
            actual: (lever.Name, lever.A)
        );
        // The echo reads the settings the server applies the lever to, which the recorder does not.
        Assert.Equal(
            expected: "[world.cull: occlusion]",
            actual: result.Output
        );
        Assert.Equal(
            expected: SdfMeshCullLevel.Occlusion,
            actual: settings.Cull
        );
    }
    [InlineData("world.cull sideways")]
    [InlineData("world.cull frustum off")]
    [Theory]
    public void AnythingElseIsRefusedNamingTheLevels(string line) {
        var (registry, _, levers) = Rig();
        var result = registry.Submit(line: line);

        Assert.True(condition: result.IsError);
        Assert.Contains(
            expectedSubstring: "occlusion|frustum|off|status]",
            actualString: result.Output
        );
        Assert.Empty(collection: levers);
    }

    public class LeverRecorder : DispatchProxy {
        public List<WorldSessionLever> Levers { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
            if (
                (targetMethod?.Name == nameof(IServerLink.SubmitEnvelope)) &&
                (args?[0] is WorldSubmissionPayload.Lever { Value: var lever })
            ) {
                Levers.Add(item: lever);
            }

            return ((targetMethod?.ReturnType == typeof(long))
                ? 0L
                : null);
        }
    }
}
