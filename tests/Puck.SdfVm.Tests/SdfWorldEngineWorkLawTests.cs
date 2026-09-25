using System.Numerics;
using System.Text;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.SignedDistance;
using Puck.Abstractions.Counting;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for the GPU work <see cref="SdfWorldEngine"/> counts, driven over <see cref="FakeGpuDevice"/> so the engine's
/// whole CPU path runs without a device: the pass order and the passes a cadence skip marks, the exact per-pass counts
/// of a rendered and of a cadence-skipped frame, what moves the revision, that submission identity keeps increasing
/// across an engine rebuild on the owner's ledger, and that a steady-state frame allocates nothing.
/// </summary>
public sealed class SdfWorldEngineWorkLawTests {
    private const uint Extent = 64;

    [Fact]
    public void ThePassOrderAndTheCadenceSkippedPassesArePinned() {
        Assert.Equal(
            expected: ["upload", "sky", "mask", "beam", "cull-args", "primary", "surface", "ambient", "views", "composite"],
            actual: SdfWorldEngine.PassLabels.ToArray()
        );
        Assert.Equal(
            expected: ["sky", "mask", "beam", "cull-args", "primary", "surface", "ambient", "views"],
            actual: SdfWorldEngine.CadenceSkippedPassLabels.ToArray()
        );
    }
    [Fact]
    public void ARenderedFrameCountsEveryPassExactly() {
        using var rig = new Rig(cadence: false);

        _ = rig.Engine.RenderFrame(frame: rig.Frame);

        Assert.Equal(
            expected: RenderedFrame,
            actual: rig.Report()
        );
    }
    [Fact]
    public void ACadenceSkippedFrameMarksItsSkippedPassesAndCountsTheRest() {
        using var rig = new Rig(cadence: true);

        _ = rig.Engine.RenderFrame(frame: rig.Frame);
        _ = rig.Engine.RenderFrame(frame: rig.Frame);

        Assert.Equal(
            expected: SkippedFrame,
            actual: rig.Report()
        );
    }
    [Fact]
    public void ProgramUploadsAndInstalledKernelReloadsMoveTheRevision() {
        using var rig = new Rig(cadence: false);
        var sample = new GpuWorkSample();

        _ = rig.Engine.RenderFrame(frame: rig.Frame);
        Assert.True(condition: rig.Engine.Work.TryReadCompleted(sample: sample));
        Assert.Equal(expected: 1L, actual: sample.Revision);

        rig.Engine.UploadProgram(program: rig.Frame.Program);
        Assert.False(condition: rig.Engine.Work.TryReadCompleted(sample: sample));
        _ = rig.Engine.RenderFrame(frame: rig.Frame);
        Assert.True(condition: rig.Engine.Work.TryReadCompleted(sample: sample));
        Assert.Equal(expected: 2L, actual: sample.Revision);

        // Unchanged bytecode installs nothing and keeps the revision; a changed kernel installs and moves it.
        Assert.Equal(expected: 0, actual: rig.Reload(kernels: SdfTestPipelines.Kernels(beam: 1)));
        _ = rig.Engine.RenderFrame(frame: rig.Frame);
        Assert.True(condition: rig.Engine.Work.TryReadCompleted(sample: sample));
        Assert.Equal(expected: 2L, actual: sample.Revision);

        Assert.Equal(expected: 1, actual: rig.Reload(kernels: SdfTestPipelines.Kernels(beam: 2)));
        Assert.False(condition: rig.Engine.Work.TryReadCompleted(sample: sample));
        _ = rig.Engine.RenderFrame(frame: rig.Frame);
        Assert.True(condition: rig.Engine.Work.TryReadCompleted(sample: sample));
        Assert.Equal(expected: 3L, actual: sample.Revision);
    }
    [Fact]
    public void SubmissionIdentityKeepsIncreasingAcrossAnEngineRebuildOnTheOwnersLedger() {
        var ledger = new GpuWorkLedger(
            framesInFlight: SdfWorldEngine.FrameRingSize,
            name: "gpu.test"
        );
        var sample = new GpuWorkSample();
        long before;

        using (var rig = new Rig(cadence: false, ledger: ledger)) {
            _ = rig.Engine.RenderFrame(frame: rig.Frame);
            Assert.True(condition: ledger.TryReadCompleted(sample: sample));
            before = sample.Submission;
        }

        // What an owner does when it drops an engine: nothing reads available until the rebuilt engine completes.
        ledger.Invalidate();
        Assert.False(condition: ledger.TryReadCompleted(sample: sample));

        using (var rig = new Rig(cadence: false, ledger: ledger)) {
            _ = rig.Engine.RenderFrame(frame: rig.Frame);
            Assert.True(condition: ledger.TryReadCompleted(sample: sample));
            Assert.True(
                condition: (sample.Submission > before),
                userMessage: $"The rebuilt engine's submission {sample.Submission} did not follow {before}."
            );
        }
    }
    [Fact]
    public void AFencedFramePublishesOnTheNextFrameAndASteadyStateFrameAllocatesNothing() {
        foreach (var cadence in ((ReadOnlySpan<bool>)[false, true])) {
            using var rig = new Rig(cadence: cadence);
            var sample = new GpuWorkSample();

            void Frame() {
                rig.Engine.SubmitFrame(frame: rig.Frame);
                _ = rig.Engine.Work.TryReadCompleted(sample: sample);
            }

            for (var warm = 0; (warm < 4); warm++) {
                Frame();
            }

            var submission = sample.Submission;

            Frame();
            Assert.Equal(expected: (submission + 1L), actual: sample.Submission);
            Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: Frame));
        }
    }

    // The first frame of a 64×64 single-view engine, and the cadence-skipped second frame. Submission 7 follows the six
    // ISA handshake submissions at construction. The first frame's upload copies all three tables (viewports, dynamic
    // transforms, the instance grid), each with its own set bind and 16-byte push; the second frame repeats the first's
    // inputs, so it owes no copy and binds nothing. The barrier ending a pass lands in the next pass, as the timing
    // marks bound them: mask carries the sky barrier, composite the views barrier and the output transition. Outside
    // every pass: the command buffer, the begin-of-frame image transitions (three on the first frame, one afterwards)
    // and cross-frame barrier, the per-frame descriptor rebinds, and the host-visible uploads — on each frame, mostly
    // the first write of that frame's ring slot's own tables (the 820 KB decal table among them), whose contents
    // start undefined.
    private const string RenderedFrame =
        "work submission=7 revision=1\nwork upload executed: dispatches=3 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=0 barriers.image=0 barriers.memory=0 barriers.buffer=0 binds.pipeline=1 binds.descriptor-set=3 push-constants=48 descriptor-writes=0 uploads.host-visible=0 clears=0\nwork sky executed: dispatches=1 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=0 barriers.image=0 barriers.memory=0 barriers.buffer=2 binds.pipeline=1 binds.descriptor-set=1 push-constants=36 descriptor-writes=0 uploads.host-visible=0 clears=0\nwork mask executed: dispatches=1 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=0 barriers.image=0 barriers.memory=1 barriers.buffer=1 binds.pipeline=1 binds.descriptor-set=1 push-constants=36 descriptor-writes=0 uploads.host-visible=0 clears=0\nwork beam executed: dispatches=1 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=0 barriers.image=0 barriers.memory=0 barriers.buffer=1 binds.pipeline=1 binds.descriptor-set=1 push-constants=36 descriptor-writes=0 uploads.host-visible=0 clears=0\nwork cull-args executed: dispatches=1 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=0 barriers.image=0 barriers.memory=0 barriers.buffer=1 binds.pipeline=1 binds.descriptor-set=1 push-constants=36 descriptor-writes=0 uploads.host-visible=0 clears=0\nwork primary executed: dispatches=0 dispatches.indirect=1 draws=0 render-passes=0 command-buffers=0 barriers.image=0 barriers.memory=0 barriers.buffer=2 binds.pipeline=1 binds.descriptor-set=1 push-constants=36 descriptor-writes=0 uploads.host-visible=0 clears=0\nwork surface executed: dispatches=0 dispatches.indirect=1 draws=0 render-passes=0 command-buffers=0 barriers.image=0 barriers.memory=0 barriers.buffer=1 binds.pipeline=1 binds.descriptor-set=1 push-constants=36 descriptor-writes=0 uploads.host-visible=0 clears=0\nwork ambient executed: dispatches=0 dispatches.indirect=1 draws=0 render-passes=0 command-buffers=0 barriers.image=0 barriers.memory=0 barriers.buffer=1 binds.pipeline=1 binds.descriptor-set=1 push-constants=36 descriptor-writes=0 uploads.host-visible=0 clears=0\nwork views executed: dispatches=0 dispatches.indirect=1 draws=0 render-passes=0 command-buffers=0 barriers.image=0 barriers.memory=0 barriers.buffer=1 binds.pipeline=1 binds.descriptor-set=1 push-constants=36 descriptor-writes=0 uploads.host-visible=0 clears=0\nwork composite executed: dispatches=0 dispatches.indirect=1 draws=0 render-passes=0 command-buffers=0 barriers.image=1 barriers.memory=1 barriers.buffer=0 binds.pipeline=1 binds.descriptor-set=1 push-constants=112 descriptor-writes=0 uploads.host-visible=0 clears=0\nwork outside: dispatches=0 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=1 barriers.image=3 barriers.memory=1 barriers.buffer=0 binds.pipeline=0 binds.descriptor-set=0 push-constants=0 descriptor-writes=43 uploads.host-visible=834848 clears=0\n";
    private const string SkippedFrame =
        "work submission=8 revision=1\nwork upload executed: dispatches=0 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=0 barriers.image=0 barriers.memory=0 barriers.buffer=0 binds.pipeline=0 binds.descriptor-set=0 push-constants=0 descriptor-writes=0 uploads.host-visible=0 clears=0\nwork sky skipped\nwork mask skipped\nwork beam skipped\nwork cull-args skipped\nwork primary skipped\nwork surface skipped\nwork ambient skipped\nwork views skipped\nwork composite executed: dispatches=0 dispatches.indirect=1 draws=0 render-passes=0 command-buffers=0 barriers.image=1 barriers.memory=0 barriers.buffer=0 binds.pipeline=1 binds.descriptor-set=1 push-constants=112 descriptor-writes=0 uploads.host-visible=0 clears=0\nwork outside: dispatches=0 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=1 barriers.image=1 barriers.memory=1 barriers.buffer=0 binds.pipeline=0 binds.descriptor-set=0 push-constants=0 descriptor-writes=43 uploads.host-visible=834000 clears=0\n";

    private sealed class Rig : IDisposable {
        public Rig(bool cadence, GpuWorkLedger? ledger = null) {
            var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version);
            var builder = new SdfProgramBuilder();

            builder.Sphere(
                material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
                radius: 1f
            );

            var program = builder.Build();
            var owned = (ledger ?? new GpuWorkLedger(
                framesInFlight: SdfWorldEngine.FrameRingSize,
                name: "gpu.sdf-engine"
            ));

            Pipelines = SdfTestPipelines.Build(
                device: gpu,
                kernels: SdfTestPipelines.Kernels(),
                ledger: owned
            );
            Engine = new SdfWorldEngine(
                device: gpu,
                height: Extent,
                options: new SdfWorldEngineOptions(
                    BrickPoolVoxelCapacity: 0,
                    Program: program,
                    ViewportCapacity: 1,
                    WorkLedger: owned
                ),
                pipelines: Pipelines,
                width: Extent
            );
            Frame = new SdfFrame(
                Program: program,
                ProgramChanged: false,
                Time: 0f,
                Views: [new SdfViewSnapshot(
                    Camera: CameraSnapshot.LookAt(
                        fieldOfViewRadians: 1f,
                        position: new Vector3(x: 0f, y: 0f, z: -5f),
                        target: Vector3.Zero,
                        viewportHeight: Extent,
                        viewportWidth: Extent
                    ),
                    Region: new NormalizedRect(
                        Height: 1f,
                        Width: 1f,
                        X: 0f,
                        Y: 0f
                    )
                )]
            ) {
                EnableCadenceGate = cadence,
            };
        }

        public SdfWorldEngine Engine { get; }
        public SdfFrame Frame { get; }
        public SdfWorldPipelines Pipelines { get; }

        public void Dispose() {
            Engine.Dispose();
            Pipelines.Dispose();
        }
        // Prepares a reload of the rig's pipelines and installs it, as a node does across two produced frames.
        public int Reload(SdfWorldKernels kernels) {
            using var reload = Pipelines.PrepareReload(
                cancellationToken: CancellationToken.None,
                kernels: kernels
            );

            return Engine.InstallReload(reload: reload);
        }
        public string Report() {
            var text = new StringBuilder();

            _ = GpuWorkReport.AppendCompleted(
                builder: text,
                sample: new GpuWorkSample(),
                source: Engine.Work
            );

            return text.ToString();
        }
    }
}
