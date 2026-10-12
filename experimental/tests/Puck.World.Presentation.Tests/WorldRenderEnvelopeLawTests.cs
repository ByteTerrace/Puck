using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Puck.World.Client.Sdf;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>Laws for the render-capacity registrations shared by the world continuum and session-screen views.</summary>
/// <param name="scenes">The boot probes the headroom laws share, built at most once for this class.</param>
/// <param name="output">The test's output, where the shipped-world law prints its measured figures.</param>
[Collection(SceneProbeCollection.Name)]
public sealed partial class WorldRenderEnvelopeLawTests(WorldRenderEnvelopeLawTests.Scenes scenes, ITestOutputHelper output) : IClassFixture<WorldRenderEnvelopeLawTests.Scenes> {
    /// <summary>Measures the shipped overworld's worst-case COMPOSED boot probe — the same four emitters
    /// <c>WorldFramePresenter</c> hands <see cref="Puck.SdfVm.SdfCompositionFrameSource"/> (the scene, the SDF
    /// document reservation, every adjacency band's reservation, the field bricks), each under the composition's
    /// own probe context and material-scope rule — against the interpreter's hard
    /// <see cref="SdfProgramBuilder.MaxInstances"/> ceiling, and requires real headroom for statics, screens, and
    /// avatars beyond the stamp pool's own worst case (<see cref="WorldPlacementPolicy.MaxStampRegistrations"/> x
    /// <see cref="WorldPlacementPolicy.MaxShapesPerStamp"/>). The scene emitter alone under-counts: the adjacency
    /// emitter reserves placement stamps and rigs per band, so the figure that governs the constant is the composed one.</summary>
    [Fact]
    public void ShippedWorldBootProbeInstancesFitTheEngineCeilingWithHeadroom() {
        var definition = AuthoredGameFixtures.Nexus;

        var (client, scene) = SceneEmitter(definition: definition);
        var adjacencies = new WorldAdjacencySceneEmitter(
            client,
            new NoNeighbours()
        );
        // The presenter's emitter list, in its order: [scene, sdf documents, adjacencies, fields].
        ISdfSceneEmitter[] emitters = [scene, new WorldSdfDocumentEmitter(), adjacencies, new WorldFieldEmitter(client: client)];
        var bootBuilder = new SdfProgramBuilder();
        var sceneOnlyInstances = -1;
        var slotBase = 0;

        foreach (var emitter in emitters) {
            var context = new SdfEmitContext(
                true,
                0,
                Vector3.Zero,
                Vector3.Zero,
                slotBase
            );

            if (emitter.OwnsMaterialScope) {
                using var scope = bootBuilder.BeginMaterialScope();

                emitter.Emit(
                    builder: bootBuilder,
                    context: context
                );
            } else {
                emitter.Emit(
                    builder: bootBuilder,
                    context: context
                );
            }

            // The scene emitter leads the list at slot base zero into an empty builder, so the instances it leaves
            // are exactly the ones it composes alone.
            if (ReferenceEquals(
                objA: emitter,
                objB: scene
            )) {
                sceneOnlyInstances = bootBuilder.InstanceCount;
            }

            slotBase += Math.Max(
                val1: 0,
                val2: emitter.DynamicSlotCount
            );
        }

        var boot = bootBuilder.Build(buildInstanceGrid: false);
        var stampPoolWorstCase = (WorldPlacementPolicy.MaxStampRegistrations * WorldPlacementPolicy.MaxShapesPerStamp);
        var headroom = (SdfProgramBuilder.MaxInstances - boot.Instances.Count);

        output.WriteLine(message: (((string)$"world.budget probe: composed boot instances={boot.Instances.Count} (scene emitter alone {sceneOnlyInstances}, adjacency bands {WorldAdjacencyBands.ProjectionCapacity(definition: definition)}) words={boot.Words.Length} ") +
            $"ceiling={SdfProgramBuilder.MaxInstances} headroom={headroom} stampPoolWorstCase={stampPoolWorstCase}"));

        var figures = (((string)$"composed boot probe {boot.Instances.Count} instance(s) (scene emitter alone {sceneOnlyInstances}, {WorldAdjacencyBands.ProjectionCapacity(definition: definition)} adjacency band(s)), stamp pool worst case {stampPoolWorstCase}, ") +
            $"headroom {headroom} under the {SdfProgramBuilder.MaxInstances}-instance ceiling at MaxShapesPerStamp {WorldPlacementPolicy.MaxShapesPerStamp}");

        Assert.True(
            condition: (boot.Instances.Count <= SdfProgramBuilder.MaxInstances),
            userMessage: $"{figures}: the composed probe exceeds the ceiling."
        );
        Assert.True(
            condition: (headroom >= 4096),
            userMessage: $"{figures}: falls below the 4096-instance floor left for statics, screens, and avatars."
        );
        // The composed figure is the one WorldPlacementPolicy.MaxShapesPerStamp is measured against; the scene
        // emitter alone under-counts by the adjacency/field reservations, so a law reading only it would admit a
        // constant the real boot refuses.
        Assert.True(
            condition: (boot.Instances.Count > sceneOnlyInstances),
            userMessage: $"{figures}: the composed probe reserves nothing beyond the scene emitter, so the composed measure is not discriminating."
        );
    }
}
