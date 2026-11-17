using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfMeshIrradianceLawTests {
    [Fact]
    public void ACanonicalPackKeepsTheStaticSceneAndItsDiagnosticMultisetIncludingDuplicates() {
        var input = Frame() with { ConvergesLighting = true };

        var (rows, writer) = SdfMeshRows.Create();
        var source = input.MeshDraws.Concat(second: input.MeshDraws.Take(count: 1)).ToArray();

        void Publish(bool reverse) {
            writer.StartOver();
            var run = writer.Allocate(count: source.Length);

            for (var i = 0; (i < source.Length); i++) { writer.Set(run, i, source[(reverse ? ((source.Length - 1) - i) : i)], new(Host: i)); }
            writer.Publish();
        }
        SdfFrame Current() => input with { MeshRows = rows, MeshDrawsRevision = rows.Revision, MeshDrawChanges = rows.Changes, MeshDrawChangesSerial = rows.Serial };
        var volume = Volume();

        Publish(reverse: false);
        _ = volume.Stage(frame: Current(), tier: SdfIndirectTier.Medium, write: static _ => { }, bakePending: false);
        using var first = volume.DiagnosticScene(frame: Current());

        Assert.NotNull(@object: first);
        var builds = volume.Counters.Read(kind: SdfMeshIrradiance.Scenes);

        Publish(reverse: true);
        _ = volume.Stage(frame: Current(), tier: SdfIndirectTier.Medium, write: static _ => { }, bakePending: false);
        using var second = volume.DiagnosticScene(frame: Current());

        Assert.Same(actual: second, expected: first);
        Assert.Equal(builds, volume.Counters.Read(kind: SdfMeshIrradiance.Scenes));
        writer.StartOver();
    }
}
