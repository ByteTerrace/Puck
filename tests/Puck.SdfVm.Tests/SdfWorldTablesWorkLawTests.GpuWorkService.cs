using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Abstractions.Waiting;
using Puck.Hosting.Jobs;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

// The background GPU work's invariants beyond a slot's ownership: a job's completion is taken up inside the host's wait,
// what a job reads is held until its fence, what it holds is reserved before it is allocated, and a measurement no job
// could have made charges its estimate.
public sealed partial class SdfWorldTablesWorkLawTests {
    private const long Mebibyte = (1L << 20);

    [Fact]
    public void ACompletedJobIsAnsweredAndTheNextTakenInsideTheHostsWaitWithNoPump() {
        using var rig = new Rig(holdFences: true);
        rig.Render();
        var client = new BackgroundClient(rig.Gpu.Services);
        var work = rig.Engine.GpuWork;
        work.Add(client);
        try {
            work.Pump(rig.Engine);
            Assert.Equal(1, work.InFlight);

            var first = (FakeGpuDevice.Fence)rig.Gpu.LastSubmittedFence!;

            // The submission asked its fence to wake the host's wait.
            Assert.NotNull(first.CompletionSignal);
            // A job offered after the pump: only the wait can take it before the next frame.
            client.Remaining = 1;

            var time = TimeProvider.System;
            var completer = new Thread(start: () => {
                Thread.Sleep(millisecondsTimeout: 30);
                first.Completed = true;
            }) { IsBackground = true };

            completer.Start();
            HostWaits.Pace(deadlineTimestamp: (time.GetTimestamp() + (time.TimestampFrequency / 4L)), precisionWaiter: null, time: time);
            completer.Join();
            // The wait's first step took the offered job, and the fence's signal, set while the thread slept, woke it
            // to answer the first.
            Assert.Equal(0, client.Remaining);
            _ = Assert.Single(client.Answers);
            Assert.Equal(1, work.InFlight);
            Assert.Contains("service-jobs:1,", work.Describe());
        } finally { client.ReleaseAnswers(); }
    }
    [Fact]
    public void AFailureInsideTheHostsWaitIsKeptForTheNextPumpNotThrownIntoTheWait() {
        using var rig = new Rig(holdFences: true);
        rig.Render();
        var client = new BackgroundClient(rig.Gpu.Services);
        var work = rig.Engine.GpuWork;
        work.Add(client);
        work.Pump(rig.Engine);
        rig.Gpu.OnCall = key => {
            if (key is "IGpuSubmissionFence.IsSignaled" or "IGpuSubmissionFence.Wait") { throw new DeviceLostException("injected loss"); }
        };

        var time = TimeProvider.System;

        // The wait is outside the frame's recovery: the loss must not escape it.
        HostWaits.Pace(deadlineTimestamp: (time.GetTimestamp() + (time.TimestampFrequency / 100L)), precisionWaiter: null, time: time);
        _ = Assert.Throws<DeviceLostException>(() => work.Pump(rig.Engine));
        work.Dispose();
        rig.Gpu.OnCall = null;
        Assert.Equal(1, client.Abandoned);
    }
    [Fact]
    public void AJobsLeaseHoldsWhatItReadsUntilItsFenceWhateverTheQueueOrderSays() {
        using var rig = new Rig(holdFences: true);
        rig.Render();
        var client = new BackgroundClient(rig.Gpu.Services);
        var work = rig.Engine.GpuWork;
        work.Add(client);
        try {
            Assert.Equal(ulong.MaxValue, work.OldestLease());
            work.Pump(rig.Engine);

            // The job leased the generation it was taken at. Its fence is pinned: no later submission's completion
            // implies its own, so only the lease protects what it reads.
            var fence = (FakeGpuDevice.Fence)rig.Gpu.LastSubmittedFence!;
            var leased = rig.Engine.BackgroundGeneration;
            var retired = new Tracker();

            fence.Pinned = true;
            Assert.Equal(leased, work.OldestLease());
            // Replaced after the job was taken: the job may still read it.
            rig.Engine.RetireBackgroundResource(retired);

            // The following upload writes the other ring slot. Its predecessor completes, and nothing is released.
            rig.Render();
            Assert.False(retired.Disposed);
            Assert.True(fence.Armed);
            // The next upload rewrites the slot the job leased. The retired object's own upload has completed, which
            // without the lease would release it; the lease holds it, and the upload waits the lease out before it
            // rewrites the slot.
            rig.Render();
            Assert.False(retired.Disposed);
            Assert.False(fence.Armed);
            Assert.Contains("lease-waits:1,", work.Describe());
            Assert.Equal(ulong.MaxValue, work.OldestLease());
            // With the lease over, the retirement door releases it.
            rig.Render();
            Assert.True(retired.Disposed);
            work.Pump(rig.Engine);
            _ = Assert.Single(client.Answers);
        } finally { client.ReleaseAnswers(); }
    }
    [Fact]
    public void WhatAJobHoldsIsReservedBeforeItIsAllocatedAndAJobWithNoRoomWaitsForOneThatCompletes() {
        using var rig = new Rig(holdFences: true);
        rig.Render();

        var account = new MemoryAccount(availablePhysicalBytes: static () => null, installedPhysicalBytes: (64L << 30));
        var segments = GpuMemorySegments.FromDirectX(profile: default);

        account.Record(reading: GpuMemoryReading.Read(
            estimated: false,
            readings: [
                new GpuSegmentReading(AvailableForReservation: null, Budget: ((ulong)(40L * Mebibyte)), CurrentReservation: null, Segment: segments[GpuMemorySegments.DirectXLocal], Usage: null),
                new GpuSegmentReading(AvailableForReservation: null, Budget: ((ulong)(64L << 30)), CurrentReservation: null, Segment: segments[GpuMemorySegments.DirectXNonLocal], Usage: null),
            ],
            segments: segments,
            serial: 1UL
        ));

        var limit = account.ReadAdmission(pool: PhysicalMemory.Device).Limit!.Value;
        var selector = new SdfResidencySelector(account: account, pool: PhysicalMemory.Device);
        // Each job holds a device-local buffer of which two fit the pool and three do not.
        var scratch = (((limit / 5L) * 2L) & ~(Mebibyte - 1L));
        var client = new HoldingClient(scratchBytes: ((ulong)scratch)) { Remaining = 3 };
        var work = new SdfGpuWork(device: rig.Gpu, name: "law", selector: selector);

        work.Add(client);
        work.Pump(rig.Engine);
        // Two jobs fly with their buffers reserved; the third had no room and went back to its client.
        Assert.Equal(2, work.InFlight);
        Assert.Equal((1, 1), (client.Abandoned, client.Remaining));
        Assert.Equal((2L * scratch), account.ReadAdmission(pool: PhysicalMemory.Device).Promised);
        Assert.True(condition: (account.ReadAdmission(pool: PhysicalMemory.Device).Promised <= limit));
        // The readbacks are host-visible: reserved in the host's pool, a granule each.
        Assert.Equal((2L * ((long)SdfGpuWork.Granule(bytes: 4UL))), account.ReadAdmission(pool: PhysicalMemory.Host).Promised);
        Assert.Contains("deferred:1,", work.Describe());
        // It is not offered again while nothing has completed: no job is taken and handed back every step.
        work.Pump(rig.Engine);
        Assert.Equal((1, 1), (client.Abandoned, client.Remaining));

        // A job completes and gives its slot back: the waiting job takes that slot's buffer, which is already reserved.
        foreach (var fence in rig.Gpu.SubmittedFences) { fence.Completed = true; }
        work.Pump(rig.Engine);
        Assert.Equal(2, client.Answers.Count);
        client.Answers[0].Release();
        client.Answers[1].Release();
        work.Pump(rig.Engine);
        Assert.Equal((1, 0, 1), (client.Abandoned, client.Remaining, work.InFlight));
        Assert.Equal((2L * scratch), account.ReadAdmission(pool: PhysicalMemory.Device).Promised);

        foreach (var fence in rig.Gpu.SubmittedFences) { fence.Completed = true; }
        work.Pump(rig.Engine);
        client.Answers[2].Release();
        work.Dispose();
        // Disposal gives every buffer and its reservation back.
        Assert.Equal((0L, 0L), (account.ReadAdmission(pool: PhysicalMemory.Device).Promised, account.ReadAdmission(pool: PhysicalMemory.Host).Promised));
    }
    [Fact]
    public void AServiceWithNothingInFlightReservesItsJobAsItsFloorSoItsWorkAlwaysProgresses() {
        using var rig = new Rig(holdFences: true);
        rig.Render();

        var account = new MemoryAccount(availablePhysicalBytes: static () => null, installedPhysicalBytes: (64L << 30));
        var segments = GpuMemorySegments.FromDirectX(profile: default);

        account.Record(reading: GpuMemoryReading.Read(
            estimated: false,
            readings: [
                new GpuSegmentReading(AvailableForReservation: null, Budget: ((ulong)(8L * Mebibyte)), CurrentReservation: null, Segment: segments[GpuMemorySegments.DirectXLocal], Usage: null),
                new GpuSegmentReading(AvailableForReservation: null, Budget: ((ulong)(64L << 30)), CurrentReservation: null, Segment: segments[GpuMemorySegments.DirectXNonLocal], Usage: null),
            ],
            segments: segments,
            serial: 1UL
        ));

        var selector = new SdfResidencySelector(account: account, pool: PhysicalMemory.Device);
        // One job's buffer is larger than the whole pool.
        var client = new HoldingClient(scratchBytes: ((ulong)(64L * Mebibyte))) { Remaining = 2 };
        using var work = new SdfGpuWork(device: rig.Gpu, name: "law", selector: selector);

        work.Add(client);
        work.Pump(rig.Engine);
        // The first job runs although the pool has no room for it; the second, with one in flight, waits.
        Assert.Equal(1, work.InFlight);
        Assert.Equal((1, 1), (client.Abandoned, client.Remaining));
        foreach (var fence in rig.Gpu.SubmittedFences) { fence.Completed = true; }
        work.Pump(rig.Engine);
        client.Answers[0].Release();
        work.Pump(rig.Engine);
        Assert.Equal((1, 0, 1), (client.Abandoned, client.Remaining, work.InFlight));
        foreach (var fence in rig.Gpu.SubmittedFences) { fence.Completed = true; }
        work.Pump(rig.Engine);
        client.Answers[1].Release();
    }
    [Fact]
    public void ATimestampPairReadEndBeforeStartChargesTheJobsEstimateAndIsCounted() {
        using var rig = new Rig(holdFences: true);
        rig.Render();
        var client = new BackgroundClient(rig.Gpu.Services);
        var work = rig.Engine.GpuWork;
        var budget = rig.Engine.Grant.Selector.Work;
        work.Add(client);
        // The queue's timestamps come back reversed: the end reads before the start.
        rig.Gpu.WriteReadback = (name, destination) => {
            if (destination.Length == (2 * sizeof(ulong))) {
                BitConverter.TryWriteBytes(destination: destination, value: 1000UL);
                BitConverter.TryWriteBytes(destination: destination[sizeof(ulong)..], value: 10UL);
            }
        };
        try {
            work.Pump(rig.Engine);
            foreach (var fence in rig.Gpu.SubmittedFences) { fence.Completed = true; }
            work.Pump(rig.Engine);

            var answer = Assert.Single(client.Answers);

            // The wrapped difference is seconds of GPU time the job cannot have run in the instant it was in flight:
            // it is charged what it was estimated at most, the budget counts it, and the credit stands where the
            // estimate left it instead of at a debt that holds all later work.
            Assert.True(condition: (answer.GpuSeconds is >= 0.0 and <= 0.001), userMessage: $"the job was measured at {answer.GpuSeconds} s");
            Assert.Contains("clamped:1,", budget.Describe());
            Assert.True(condition: (budget.GpuCredit > -SdfGpuWork.FrameShareSeconds), userMessage: $"the credit fell to {budget.GpuCredit}");
        } finally {
            rig.Gpu.WriteReadback = null;
            client.ReleaseAnswers();
        }
    }

    private sealed class Tracker : IDisposable {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
    // A client whose every job holds one device-local buffer of the slot and copies four bytes of it back; a job handed
    // back is offered again.
    private sealed class HoldingClient(ulong scratchBytes) : ISdfGpuWorkClient {
        public int Remaining { get; set; }
        public int Abandoned { get; private set; }
        public List<SdfGpuWorkReadback> Answers { get; } = [];
        public GpuMemoryBytes OwnedBytes => default;
        public GpuQueueCategory Category => GpuQueueCategory.Bake;
        public bool TryTake(SdfWorldTables tables, int uploadBytes, [NotNullWhen(true)] out object? job, out double estimatedSeconds) {
            estimatedSeconds = 0.0;
            job = null;
            if (Remaining == 0) { return false; }
            Remaining--;
            job = new object();
            return true;
        }
        public void Record(object job, in SdfGpuWorkRecording recording) =>
            recording.CopyToReadback(recording.DeviceBuffer(part: "scratch", bytes: scratchBytes, usage: GpuBufferUsage.Storage, sizing: SdfGpuWorkSizing.Fit), 4);
        public void Complete(object job, SdfGpuWorkReadback readback) => Answers.Add(readback);
        public void Abandon(object job) { Abandoned++; Remaining++; }
        public void Dispose() { }
    }
}
