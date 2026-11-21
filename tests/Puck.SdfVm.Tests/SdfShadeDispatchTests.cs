using System.Buffers.Binary;
using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfShadeDispatchTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryPaddedGroupIsCoveredOnceAndEveryJobFits(bool glass) {
        var shade = SdfShadeProgram.Compile(new("clock", [], [new(SdfShadePhase.Surface, "roughness", new("input", Name: "clock"))]));
        var plan = SdfShadeDispatch.Plan(8193, 4321, [shade, shade], SdfShadeProgram.MaxLights, glass);
        Assert.Equal([shade.Identity], plan.Identities);
        uint end = 0;
        foreach (var job in plan.Jobs) {
            Assert.Equal(end, job.FirstGroup);
            Assert.InRange(job.Groups, 1u, 65535u);
            Assert.Equal(job.Groups * 64UL * shade.PixelWork(SdfShadeProgram.MaxLights) * (glass ? 2UL : 1UL), job.Work);
            Assert.InRange(job.Work, 1UL, SdfShadeProgram.MaxWork);
            end += job.Groups;
        }
        Assert.Equal(((8193u + 7) / 8) * ((4321u + 7) / 8), end);
    }

    // A bin's count exists only on the GPU, so each bin records enough jobs to hold every tile of the extent, and each job
    // of a bin that may run a program fits the bound; tiles with no program, and an extent with no program, run only the
    // engine's fixed stage and are one dispatch within the group-count limit.
    [Fact]
    public void EveryBinHoldsTheExtentAndEveryAuthoredJobFits() {
        var shade = SdfShadeProgram.Compile(new("clock", [], [new(SdfShadePhase.Surface, "roughness", new("input", Name: "clock"))]));
        var tiles = ((1920u + 7) / 8) * ((1080u + 7) / 8);
        var plan = SdfShadeDispatch.PlanBins(1920, 1080, [shade], SdfShadeProgram.MaxLights);

        Assert.True(plan.Binned);
        Assert.Empty(plan.Jobs);
        Assert.Equal([shade.Identity], plan.Identities);
        Assert.Equal(2, plan.Bins.Count);
        Assert.Equal(1u, plan.Bins[0].Jobs);
        Assert.Equal(65535u, plan.Bins[0].Width);
        Assert.Equal(64UL * shade.PixelWork(SdfShadeProgram.MaxLights), plan.Bins[1].TileWork);
        Assert.InRange(plan.Bins[1].Width * plan.Bins[1].TileWork, 1UL, SdfShadeProgram.MaxWork);
        foreach (var bin in plan.Bins) {
            Assert.InRange(bin.Jobs, 1u, tiles);
            Assert.True((ulong)bin.Jobs * bin.Width >= tiles);
            Assert.True((ulong)(bin.Jobs - 1u) * bin.Width < tiles);
        }

        var job = Assert.Single(SdfShadeDispatch.Plan(1920, 1080, [], SdfShadeProgram.MaxLights, glass: false).Jobs);

        Assert.Equal(0u, job.FirstGroup);
        Assert.Equal(tiles, job.Groups);
    }

    [Fact]
    public void SelectedProgramsAloneSupplyCadenceReads() {
        SdfShadeProgram Read(string name) => SdfShadeProgram.Compile(new(name, [], [new(SdfShadePhase.Surface, "roughness", new("input", Name: name))]));
        var appearance = new SdfAppearance([new(System.Numerics.Vector3.One)]);
        Assert.Equal(SdfShadeReads.Clock, appearance.ResolveShadeReads(true, Read("clock")));
        Assert.Equal(SdfShadeReads.World2, appearance.ResolveShadeReads(true, Read("lanes.world2")));
        Assert.Equal(SdfShadeReads.None, appearance.ResolveShadeReads(true, Read("material.roughness")));
        Assert.Equal(SdfShadeReads.None, appearance.ResolveShadeReads(false, Read("clock")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CrossingLightRangeDoesNotReplanAndSteadyDispatchAllocatesNothing(bool glass, bool binned) {
        var surface = SdfShadeProgram.Compile(new("surface", [], [new(SdfShadePhase.Surface, "roughness", new("input", Name: "clock"))]));
        var rim = SdfShadeProgram.Compile(new("rim", [], [new(SdfShadePhase.Surface, "specular", new("input", Name: "material.roughness"))]));
        var fallback = SdfShadeProgram.Compile(new("fallback", [], [new(SdfShadePhase.Surface, "roughness", new("input", Name: "lanes.world2"))]));
        var builder = new SdfProgramBuilder();
        var m0 = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One) { Shade = surface });
        var m1 = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One) { Shade = rim });

        builder.Sphere(radius: 1f, material: m0);
        builder.Sphere(radius: 0.5f, material: m1);
        var program = builder.Build();

        // 32 point lights spaced along +Z so moving the camera crosses light ranges and changes visible light count.
        var lights = new SdfLights { Count = 32 };

        for (var index = 0; (index < 32); index++) {
            lights.Set(index: index, light: new SdfLight(
                Color: Vector3.One,
                Direction: new Vector3(x: 0f, y: 0f, z: (index * 5f)),
                DynamicSlot: SdfProgram.NoDynamicTransformSlot,
                Kind: SdfLightKind.Point,
                Param: 6f,
                Shadows: false,
                Weight: 10f
            ));
        }

        SdfViewSnapshot ViewAt(float z) => new(
            Camera: CameraSnapshot.LookAt(
                fieldOfViewRadians: 1f,
                position: new Vector3(x: 0f, y: 0f, z: z),
                target: new Vector3(x: 0f, y: 0f, z: (z + 10f)),
                viewportHeight: 1080,
                viewportWidth: 1920
            ),
            Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)
        );

        var views = new SdfViewSnapshot[] { ViewAt(z: -20f) };
        var frame = new SdfFrame(Program: program, ProgramChanged: false, Time: 0f, Views: views) {
            FarDistance = 40f,
            Lights = lights,
            ShadeDefault = fallback,
            ShadePrograms = true,
        };

        var camNear = views[0].Camera;
        var maskNear = SdfLightSelection.Select(camera: in camNear, far: frame.FarDistance, lights: lights);

        views[0] = ViewAt(z: 50f);
        var camMid = views[0].Camera;
        var maskMid = SdfLightSelection.Select(camera: in camMid, far: frame.FarDistance, lights: lights);

        views[0] = ViewAt(z: 180f);
        var camFar = views[0].Camera;
        var maskFar = SdfLightSelection.Select(camera: in camFar, far: frame.FarDistance, lights: lights);

        // Prove the test actually crosses light ranges with distinct visible counts.
        Assert.NotEqual(expected: UInt128.PopCount(value: maskNear), actual: UInt128.PopCount(value: maskMid));
        Assert.NotEqual(expected: UInt128.PopCount(value: maskMid), actual: UInt128.PopCount(value: maskFar));

        views[0] = ViewAt(z: -20f);
        var initial = SdfShadeDispatch.For(previous: null, frame: frame, width: 1920u, height: 1080u, screens: 2u, glass: glass, tier: SdfShadeTier.Interpreted, binned: binned);
        var selectionsBefore = SdfCadenceWork.ProcessWork.Read(kind: SdfCadenceWork.LightSelections);

        views[0] = ViewAt(z: 50f);
        var mid = SdfShadeDispatch.For(previous: initial, frame: frame, width: 1920u, height: 1080u, screens: 2u, glass: glass, tier: SdfShadeTier.Interpreted, binned: binned);

        views[0] = ViewAt(z: 180f);
        var far = SdfShadeDispatch.For(previous: mid, frame: frame, width: 1920u, height: 1080u, screens: 2u, glass: glass, tier: SdfShadeTier.Interpreted, binned: binned);

        Assert.Same(expected: initial, actual: mid);
        Assert.Same(expected: initial, actual: far);
        Assert.Equal(expected: selectionsBefore, actual: SdfCadenceWork.ProcessWork.Read(kind: SdfCadenceWork.LightSelections));

        var positions = new[] { ViewAt(z: -20f), ViewAt(z: 50f), ViewAt(z: 180f) };
        var step = 0;
        var current = initial;

        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: () => {
            views[0] = positions[step % positions.Length];
            step++;
            current = SdfShadeDispatch.For(previous: current, frame: frame, width: 1920u, height: 1080u, screens: 2u, glass: glass, tier: SdfShadeTier.Interpreted, binned: binned);
        }));
        Assert.Same(expected: initial, actual: current);
    }

    [Fact]
    public void ViewLightMaskIsSelectedOncePerViewAcrossPassBlocks() {
        const int SplitViews = 4;
        const int PassesPerView = 12;

        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.Sphere(radius: 1f, material: material);
        var program = builder.Build();
        var gpu = new FakeGpuDevice();
        var ledger = new GpuWorkLedger(framesInFlight: SdfWorldTables.FrameRingSize, name: "gpu.sdf-tables.light-mask");
        var cache = new GpuPassPipelineCache();

        using var regionCopy = SdfTestPipelines.RegionCopy(device: gpu, ledger: ledger);
        using var meshRaster = SdfTestPipelines.MeshRaster(device: gpu, ledger: ledger);
        using var shadowRaster = SdfTestPipelines.ShadowRaster(device: gpu, ledger: ledger);
        using var glassRaster = SdfTestPipelines.GlassRaster(device: gpu, ledger: ledger);
        using var occlusion = SdfTestPipelines.Occlusion(device: gpu, ledger: ledger);
        using var pipelines = SdfTestPipelines.Build(device: gpu, kernels: SdfTestPipelines.Kernels(), cache: cache);
        using var tables = new SdfWorldTables(
            device: gpu,
            options: new SdfWorldTablesOptions(Program: program, WorkLedger: ledger),
            pipelines: pipelines,
            meshRaster: meshRaster,
            shadowEntryRaster: shadowRaster,
            shadowRaster: shadowRaster,
            shadowExitRaster: shadowRaster,
            shadowGlassRaster: shadowRaster,
            glassRaster: glassRaster,
            occlusion: occlusion,
            regionCopy: regionCopy.Compute!
        );

        var lights = new SdfLights { Count = 24 };

        for (var index = 0; (index < 24); index++) {
            lights.Set(index: index, light: new SdfLight(
                Color: Vector3.One,
                Direction: new Vector3(x: (index * 8f), y: 0f, z: 0f),
                DynamicSlot: SdfProgram.NoDynamicTransformSlot,
                Kind: SdfLightKind.Point,
                Param: 10f,
                Shadows: false,
                Weight: 10f
            ));
        }

        var views = new SdfViewSnapshot[SplitViews];

        for (var view = 0; (view < SplitViews); view++) {
            var x = (view * 40f);

            views[view] = new SdfViewSnapshot(
                Camera: CameraSnapshot.LookAt(
                    fieldOfViewRadians: 1f,
                    position: new Vector3(x: x, y: 0f, z: -10f),
                    target: new Vector3(x: x, y: 0f, z: 0f),
                    viewportHeight: 540,
                    viewportWidth: 960
                ),
                Region: new NormalizedRect(Height: 0.5f, Width: 0.5f, X: 0f, Y: 0f)
            );
        }

        var frame = new SdfFrame(Program: program, ProgramChanged: false, Time: 0f, Views: views) {
            FarDistance = 40f,
            Lights = lights,
        };
        var block = new byte[SdfFrameBlock.SizeBytes];
        var maskOffset = checked((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: SdfWorldPackage.LightMask));
        var before = SdfCadenceWork.ProcessWork.Read(kind: SdfCadenceWork.LightSelections);

        for (var view = 0; (view < SplitViews); view++) {
            var camera = views[view].Camera;
            var expected = SdfLightSelection.Select(camera: in camera, far: frame.FarDistance, lights: lights);

            for (var pass = 0; (pass < PassesPerView); pass++) {
                SdfFrameBlock.Write(
                    block: block,
                    frame: frame,
                    height: 540u,
                    lightMask: tables.LightMaskOf(frame: frame, view: view),
                    tables: tables.PassValues,
                    view: view,
                    width: 960u
                );

                var written = UInt128.Zero;

                for (var word = 0; (word < 4); word++) {
                    written |= (((UInt128)BinaryPrimitives.ReadUInt32LittleEndian(source: block.AsSpan(start: (maskOffset + (word * sizeof(uint)))))) << (32 * word));
                }
                Assert.Equal(expected: expected, actual: written);
            }
        }

        // 48 pass blocks across 4 views computed light selection once per view (4 total).
        Assert.Equal(expected: SplitViews, actual: (SdfCadenceWork.ProcessWork.Read(kind: SdfCadenceWork.LightSelections) - before));

        // Invalidating view inputs recomputes once on the next frame.
        tables.UpdateTablesSignature();
        var afterReset = SdfCadenceWork.ProcessWork.Read(kind: SdfCadenceWork.LightSelections);

        for (var pass = 0; (pass < PassesPerView); pass++) {
            _ = tables.LightMaskOf(frame: frame, view: 0);
        }
        Assert.Equal(expected: 1L, actual: (SdfCadenceWork.ProcessWork.Read(kind: SdfCadenceWork.LightSelections) - afterReset));
    }
}
