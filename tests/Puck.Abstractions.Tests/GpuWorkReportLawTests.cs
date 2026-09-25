using System.Buffers;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="GpuWorkReport"/>: the exact lines every work readout prints, and that writing them into a
/// builder with room for the text allocates nothing.
/// </summary>
public sealed class GpuWorkReportLawTests {
    private const string Columns = " dispatches={0} dispatches.indirect=0 draws=0 render-passes=0 command-buffers=0 barriers.image=0 barriers.memory=0 barriers.buffer=0 binds.pipeline=0 binds.descriptor-set=0 push-constants={1} descriptor-writes=0 uploads.host-visible=0 clears=0";

    [Fact]
    public void ASampleWritesItsSubmissionEveryPassAndTheOutsideRow() {
        var rig = Rig.Create();
        var text = new StringBuilder();

        _ = GpuWorkReport.AppendCompleted(
            builder: text,
            sample: new GpuWorkSample(),
            source: rig.Ledger
        );
        _ = GpuWorkReport.AppendLifetime(builder: text, source: rig.Ledger);

        Assert.Equal(
            expected: string.Concat(
                "work submission=1 revision=7\n",
                $"work alpha executed:{string.Format(arg0: 2, arg1: 0, format: Columns)}\n",
                "work beta skipped\n",
                "work gamma not-reached\n",
                $"work outside:{string.Format(arg0: 0, arg1: 8, format: Columns)}\n",
                "work lifetime: created.pipelines=0 created.shader-modules=1 created.images=0 created.buffers=0 created.descriptor-pools=0 created.descriptor-sets=0\n"
            ),
            actual: text.ToString()
        );
    }
    [Fact]
    public void NothingCompletedWritesUnavailable() {
        var ledger = new GpuWorkLedger(
            framesInFlight: 1,
            name: "gpu.test"
        );
        var text = new StringBuilder();

        _ = GpuWorkReport.AppendCompleted(builder: text, sample: new GpuWorkSample(), source: ledger);
        _ = GpuWorkReport.AppendSample(builder: text, sample: new GpuWorkSample());

        Assert.Equal(expected: "work unavailable\nwork unavailable\n", actual: text.ToString());
    }
    [Fact]
    public void WritingASampleAndTheLifetimeAllocatesNothing() {
        var rig = Rig.Create();
        var sample = new GpuWorkSample();
        var text = new StringBuilder(capacity: 4096);

        void Write() {
            _ = text.Clear();
            _ = GpuWorkReport.AppendCompleted(builder: text, sample: sample, source: rig.Ledger);
            _ = GpuWorkReport.AppendLifetime(builder: text, source: rig.Ledger);
        }

        Write();

        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: Write));
        Assert.Contains(expectedSubstring: "work alpha executed:", actualString: text.ToString());
    }
    [Fact]
    public void ANodeWritesItsLinesAndItsJson() {
        var rig = Rig.Create();
        var node = new GpuWorkNode(
            Lifetime: rig.Ledger,
            Name: "world",
            Work: rig.Ledger
        );
        var text = GpuWorkReport.AppendNode(
            builder: new StringBuilder(),
            node: node,
            sample: new GpuWorkSample()
        ).ToString();

        Assert.StartsWith(
            actualString: text,
            expectedStartString: "node world work submission=1 revision=7\nwork alpha executed:"
        );
        Assert.EndsWith(
            actualString: text,
            expectedEndString: "work lifetime: created.pipelines=0 created.shader-modules=1 created.images=0 created.buffers=0 created.descriptor-pools=0 created.descriptor-sets=0\n"
        );

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(bufferWriter: buffer)) {
            GpuWorkReport.WriteNode(
                node: node,
                sample: new GpuWorkSample(),
                writer: writer
            );
        }

        const string Zeros = "\"gpu.draws\":0,\"gpu.render-passes\":0,\"gpu.command-buffers\":0,\"gpu.barriers.image\":0,\"gpu.barriers.memory\":0,\"gpu.barriers.buffer\":0,\"gpu.binds.pipeline\":0,\"gpu.binds.descriptor-set\":0";

        Assert.Equal(
            expected: string.Concat(
                "{\"name\":\"world\",\"sample\":{\"submission\":1,\"revision\":7,\"passes\":[",
                $"{{\"label\":\"alpha\",\"state\":\"executed\",\"counts\":{{\"gpu.dispatches\":2,\"gpu.dispatches.indirect\":0,{Zeros},\"gpu.push-constants\":0,\"gpu.descriptor-writes\":0,\"gpu.uploads.host-visible\":0,\"gpu.clears\":0}}}},",
                "{\"label\":\"beta\",\"state\":\"skipped\"},{\"label\":\"gamma\",\"state\":\"not-reached\"}],",
                $"\"outside\":{{\"gpu.dispatches\":0,\"gpu.dispatches.indirect\":0,{Zeros},\"gpu.push-constants\":8,\"gpu.descriptor-writes\":0,\"gpu.uploads.host-visible\":0,\"gpu.clears\":0}}}},",
                "\"lifetime\":{\"gpu.created.pipelines\":0,\"gpu.created.shader-modules\":1,\"gpu.created.images\":0,\"gpu.created.buffers\":0,\"gpu.created.descriptor-pools\":0,\"gpu.created.descriptor-sets\":0}}"
            ),
            actual: Encoding.UTF8.GetString(bytes: buffer.WrittenSpan)
        );
    }

    private sealed record Rig(GpuWorkLedger Ledger) {
        // One completed submission under three passes: alpha executed with two dispatches, beta skipped, gamma not
        // reached, and eight push-constant bytes outside every pass.
        public static Rig Create() {
            var gpu = new FakeGpu();
            var ledger = new GpuWorkLedger(
            framesInFlight: 1,
            name: "gpu.test"
        );
            var services = GpuWorkCounting.Wrap(ledger: ledger, services: ((IGpuComputeServices)gpu));

            ledger.Configure(passLabels: ["alpha", "beta", "gamma"], revision: 7L);
            services.ShaderModuleFactory.Create(bytecode: ReadOnlyMemory<byte>.Empty, stage: GpuShaderStage.Compute).Dispose();
            services.Recorder.PushConstants(bindPoint: GpuBindPoint.Compute, commandBufferHandle: 2, data: new byte[8], offset: 0, pipelineLayoutHandle: 3, stageFlags: GpuShaderStage.Compute);
            ledger.EnterPass(pass: 0);
            services.Recorder.Dispatch(commandBufferHandle: 2, groupCountX: 1, groupCountY: 1, groupCountZ: 1);
            services.Recorder.Dispatch(commandBufferHandle: 2, groupCountX: 1, groupCountY: 1, groupCountZ: 1);
            ledger.LeavePass();
            ledger.SkipPass(pass: 1);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: []);

            return new Rig(Ledger: ledger);
        }
    }
}
