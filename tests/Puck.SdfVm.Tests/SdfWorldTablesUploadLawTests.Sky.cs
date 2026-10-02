using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Presentation;
using Puck.Shaders;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldTablesUploadLawTests {
    private static SdfSkySnapshot SkyStack(int count) {
        var common = new SdfSkyParameterTable<SdfSkyLayerData>("layers", count);
        var stars = new SdfSkyParameterTable<SdfSkyStarsData>("stars", count);
        var layers = new SdfSkyLayerInfo[count];
        var tag = SdfSkyKindBindings.All.Single(static kind => kind.Name == "stars").Tag;
        for (var index = 0; index < count; index++) {
            layers[index] = new("star" + index, "stars", SdfSkyLayerClass.Point, SdfSkyBlend.Add,
                SdfSkyVisibility.Both, QualityTier.High, 1f, true, null, 0d);
            common.Rows[index] = new() { InverseRotation = new(0f, 0f, 0f, 1f), Kind = tag,
                ParameterIndex = (uint)index, Enabled = 1u, Opacity = 1f, MinimumTier = (uint)QualityTier.High,
                Visibility = (uint)SdfSkyVisibility.Both, Blend = (uint)SdfSkyBlend.Add };
            stars.Rows[index] = new() { Density = 48f, Brightness = index + 1f, Seed = 0x80000001u + (uint)index };
        }
        return new(layers, QualityTier.High, [stars], common);
    }

    [Fact]
    public void Native_sky_tables_grow_through_the_existing_region_pool_and_preserve_integer_bytes() {
        using var rig = new Rig(slots: 1);
        var pools = rig.Gpu.PoolsCreated.Count;
        var environment = SdfLighting.Default();
        environment.Sky = SkyStack(2);
        rig.Render(0f, lighting: environment);
        Assert.Equal(environment.Sky.Table(0).Bytes.ToArray(), rig.Gpu.DeviceLocal(320, "skyStars"));
        Assert.Equal(environment.Sky.Common!.Bytes.ToArray(), rig.Gpu.DeviceLocal(160, "sky-layers"));
        var memory = rig.Engine.LightingMemory;
        Assert.True(memory.Gpu.DeviceLocal >= 320u + 160u);
        environment.Sky = SkyStack(5);
        rig.Render(0f, lighting: environment);
        Assert.Equal(environment.Sky.Table(0).Bytes.ToArray(), rig.Gpu.DeviceLocal(800, "skyStars"));
        Assert.Equal(pools, rig.Gpu.PoolsCreated.Count);
        for (var frame = 0; frame <= SdfWorldTables.FrameRingSize; frame++) { rig.Render(0f, lighting: environment); }
        Assert.Equal(0L, rig.Gpu.HostBytes());
        Assert.Equal(0, rig.Gpu.UploadCopies);
    }

    [Fact]
    public void Per_view_admission_changes_only_that_views_signature_and_reuses_unchanged_rows() {
        using var rig = new Rig(slots: 1);
        var environment = SdfLighting.Default();
        environment.Sky = SkyStack(2);
        var frame = Frame(Program(Vector3.One), 0f, Transforms(1)) with { Environment = environment };
        var view = frame.Views[0];
        frame = frame with { Views = [view, view with { SkyQuality = QualityTier.Low }] };
        void Render() { rig.Gpu.ResetTallies(); rig.Engine.Pack(frame); rig.Engine.SubmitUpload(); rig.Engine.UpdateTablesSignature(); }
        Render();
        Assert.Equal(new uint[] { 3, 3, 0, 0 }, MemoryMarshal.Cast<byte, uint>(rig.Gpu.DeviceLocal(16, "sky-admission")).ToArray());
        var first = rig.Engine.ViewSignature(frame, 0);
        var second = rig.Engine.ViewSignature(frame, 1);
        var rewrites = rig.Engine.SkyAdmissionRewrites;
        Render();
        Assert.Equal(rewrites, rig.Engine.SkyAdmissionRewrites);
        frame = frame with { Views = [view with { SkyInspection = SdfSkyInspection.None.WithMute("star0", true) }, frame.Views[1]] };
        Render();
        Assert.Equal(new uint[] { 0, 3, 0, 0 }, MemoryMarshal.Cast<byte, uint>(rig.Gpu.DeviceLocal(16, "sky-admission")).ToArray());
        Assert.NotEqual(first, rig.Engine.ViewSignature(frame, 0));
        Assert.Equal(second, rig.Engine.ViewSignature(frame, 1));
        Assert.Equal(rewrites + 2, rig.Engine.SkyAdmissionRewrites);
        var changed = environment.Sky.Layer(1) with { Visibility = SdfSkyVisibility.Lighting };
        environment.Sky.SetLayer(1, changed);
        Render();
        Assert.Equal(new uint[] { 0, 0, 0, 0 }, MemoryMarshal.Cast<byte, uint>(rig.Gpu.DeviceLocal(16, "sky-admission")).ToArray());
        rewrites = rig.Engine.SkyAdmissionRewrites;
        for (var index = 0; index <= SdfWorldTables.FrameRingSize; index++) { Render(); }
        Assert.Equal(rewrites, rig.Engine.SkyAdmissionRewrites);
        Assert.Equal(0L, rig.Gpu.HostBytes());
        Assert.Equal(0, rig.Gpu.UploadCopies);
    }
}
