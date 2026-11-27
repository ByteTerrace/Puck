using System.Reflection;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfMeshIrradianceLawTests {
    /// <summary>THE LAW: a scene the probe service could not bind is built again though the static draws it was built from
    /// stand unchanged, once the pending scene interval has passed: the rebuild reads what the region holds now, so the
    /// probe service's admission never cancels and lists a head on the same unbindable scene for good.</summary>
    [Fact]
    public void ASceneTheProbeServiceCouldNotBindIsBuiltAgainThoughItsDrawsStandUnchanged() {
        const BindingFlags Private = (BindingFlags.Instance | BindingFlags.NonPublic);
        var volume = Volume();
        var frame = Frame();
        var build = typeof(SdfMeshIrradiance).GetField(name: "m_build", bindingAttr: Private)!;
        var installed = typeof(SdfMeshIrradiance).GetField(name: "m_scene", bindingAttr: Private)!;
        var note = typeof(SdfMeshIrradiance).GetMethod(name: "NoteScenePlacement", bindingAttr: Private)!;

        StageUntilScenes(volume: volume, frame: frame, build: build, scenes: 1L);

        var first = installed.GetValue(obj: volume);

        Assert.NotNull(@object: first);
        // The probe service's admission found the installed scene's meshes unplaced past its admission wait.
        _ = note.Invoke(obj: volume, parameters: [first, false]);
        Assert.Equal(expected: 1L, actual: volume.Counters.Read(kind: SdfMeshIrradiance.SceneUnplaced));
        StageUntilScenes(volume: volume, frame: frame, build: build, scenes: 2L);
        Assert.NotSame(expected: first, actual: installed.GetValue(obj: volume));
    }
    // Stages the frame, a pending scene interval of the volume's clock at a time, until `scenes` scene builds have landed.
    private static void StageUntilScenes(SdfMeshIrradiance volume, SdfFrame frame, FieldInfo build, long scenes) =>
        TestLiveness.Until(reason: volume.Describe, step: () => {
            Step(volume: volume, span: TimeSpan.FromSeconds(value: 5d));
            _ = volume.Stage(bakePending: false, frame: frame, tier: SdfIndirectTier.Medium, write: static _ => { });
            return ((volume.Counters.Read(kind: SdfMeshIrradiance.Scenes) >= scenes) && (build.GetValue(obj: volume) is null or Task { IsCompleted: true }));
        });
}
