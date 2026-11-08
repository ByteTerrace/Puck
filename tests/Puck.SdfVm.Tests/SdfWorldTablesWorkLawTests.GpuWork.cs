using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Gpu;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldTablesWorkLawTests {
    [Fact]
    public void BackgroundReadbackFromAnEarlierJobCannotReleaseAReusedSlot() {
        using var rig = new Rig();
        rig.Render();
        var client = new BackgroundClient(rig.Gpu.Services);
        rig.Engine.GpuWork.Add(client);
        try {
            rig.Engine.GpuWork.Pump(rig.Engine);
            rig.Engine.GpuWork.Pump(rig.Engine);
            var first = Assert.Single(client.Answers);
            first.Release();
            client.Remaining = 1;
            rig.Engine.GpuWork.Pump(rig.Engine);
            rig.Engine.GpuWork.Pump(rig.Engine);
            var second = client.Answers[1];
            Assert.Equal(first.Slot, second.Slot);
            Assert.Throws<InvalidOperationException>(first.Release);
            second.Read(new byte[4]);
        } finally { client.ReleaseAnswers(); }
    }

    [Theory]
    [InlineData("IGpuRecorder.BeginCommandBuffer")]
    [InlineData("IGpuQueueSubmitter.Submit(fence)")]
    public void BackgroundFailureReturnsEveryTakenJob(string call) {
        using var rig = new Rig();
        rig.Render();
        var client = new BackgroundClient(rig.Gpu.Services);
        rig.Engine.GpuWork.Add(client);
        rig.Gpu.OnCall = key => { if (key == call) { throw new InvalidOperationException("injected"); } };
        Assert.Throws<InvalidOperationException>(() => rig.Engine.GpuWork.Pump(rig.Engine));
        rig.Gpu.OnCall = null;
        rig.Engine.GpuWork.Dispose();
        Assert.Equal(1, client.Abandoned);
        Assert.Empty(client.Answers);
    }

    [Fact]
    public void BackgroundCompletionFailureReturnsTheJobAndReadback() {
        using var rig = new Rig();
        rig.Render();
        var client = new BackgroundClient(rig.Gpu.Services) { FailCompletion = true };
        rig.Engine.GpuWork.Add(client);
        rig.Engine.GpuWork.Pump(rig.Engine);
        try {
            Assert.Throws<InvalidOperationException>(() => rig.Engine.GpuWork.Pump(rig.Engine));
            Assert.Equal(1, client.Abandoned);
            Assert.Throws<InvalidOperationException>(() => client.Answers[0].Read(new byte[4]));
        } finally { client.ReleaseAnswers(); }
    }

    [Fact]
    public async Task BackgroundConcurrentReleasesHaveOneWinnerAndLateReleaseAfterDisposeIsRejected() {
        using var rig = new Rig();
        rig.Render();
        var client = new BackgroundClient(rig.Gpu.Services);
        rig.Engine.GpuWork.Add(client);
        rig.Engine.GpuWork.Pump(rig.Engine);
        rig.Engine.GpuWork.Pump(rig.Engine);
        var answer = Assert.Single(client.Answers);
        using var start = new ManualResetEventSlim(false);
        Task<bool> Release() => Task.Run(() => {
            start.Wait();
            try { answer.Release(); return true; } catch (InvalidOperationException) { return false; }
        });
        var first = Release();
        var second = Release();
        start.Set();
        var results = await Task.WhenAll(first, second);
        Assert.Equal(1, results.Count(static success => success));
        rig.Engine.GpuWork.Dispose();
        Assert.Throws<InvalidOperationException>(answer.Release);
        Assert.Throws<InvalidOperationException>(() => answer.Read(new byte[4]));
    }

    [Fact]
    public void BackgroundLostDeviceAbandonsInFlightJobsBeforeReleasingClientStorage() {
        using var rig = new Rig(holdFences: true, distinctBuffers: true);
        rig.Render();
        var client = new BackgroundClient(rig.Gpu.Services);
        rig.Engine.GpuWork.Add(client);
        rig.Engine.GpuWork.Pump(rig.Engine);
        rig.Gpu.OnCall = key => {
            if (key is "IGpuSubmissionFence.IsSignaled" or "IGpuSubmissionFence.Wait") { throw new DeviceLostException("injected loss"); }
        };
        Assert.Throws<DeviceLostException>(() => rig.Engine.GpuWork.Pump(rig.Engine));
        rig.Engine.GpuWork.Dispose();
        rig.Gpu.OnCall = null;
        Assert.Equal(1, client.Abandoned);
        Assert.True(client.Disposed);
        Assert.Empty(client.Answers);
    }

    [Fact]
    public void BackgroundSlotWaitsForBothTheFenceAndTheReader() {
        using var rig = new Rig(holdFences: true);
        rig.Render();
        var client = new BackgroundClient(rig.Gpu.Services) { Remaining = 5 };
        rig.Engine.GpuWork.Add(client);
        rig.Engine.GpuWork.Pump(rig.Engine);
        Assert.Equal(4, rig.Engine.GpuWork.InFlight);
        rig.Engine.GpuWork.Pump(rig.Engine);
        Assert.Empty(client.Answers);
        foreach (var fence in rig.Gpu.SubmittedFences) { fence.Completed = true; }
        try {
            rig.Engine.GpuWork.Pump(rig.Engine);
            Assert.Equal(4, client.Answers.Count);
            Assert.Equal(1, client.Remaining);
            client.Answers[0].Release();
            rig.Engine.GpuWork.Pump(rig.Engine);
            Assert.Equal(0, client.Remaining);
            Assert.Equal(1, rig.Engine.GpuWork.InFlight);
        } finally { client.ReleaseAnswers(); }
    }

    private sealed class BackgroundClient(GpuDeviceServices gpu) : ISdfGpuWorkClient {
        private readonly IGpuBuffer m_source = gpu.BufferFactory.CreateDeviceLocal(sizeBytes: 4, usage: GpuBufferUsage.Storage,
            name: new GpuObjectName(owner: "law", part: "source"));
        public int Remaining { get; set; } = 1;
        public int Abandoned { get; private set; }
        public bool FailCompletion { get; init; }
        public bool Disposed { get; private set; }
        public List<SdfGpuWorkReadback> Answers { get; } = [];
        public GpuMemoryBytes OwnedBytes => default;
        public GpuQueueCategory Category => GpuQueueCategory.Bake;
        public bool TryTake(SdfWorldTables tables, int uploadBytes, [NotNullWhen(true)] out object? job, out double estimatedSeconds) {
            estimatedSeconds = 0.001;
            job = null;
            if (Remaining == 0) { return false; }
            Remaining--;
            job = new object();
            return true;
        }
        public void Record(object job, in SdfGpuWorkRecording recording) => recording.CopyToReadback(m_source, 4);
        public void Complete(object job, SdfGpuWorkReadback readback) {
            Answers.Add(readback);
            if (FailCompletion) { throw new InvalidOperationException("injected completion"); }
        }
        public void Abandon(object job) { Assert.False(Disposed); Abandoned++; }
        public void Dispose() { Disposed = true; m_source.Dispose(); }
        public void ReleaseAnswers() {
            foreach (var answer in Answers) {
                try { answer.Release(); } catch (InvalidOperationException) { }
            }
        }
    }
}
