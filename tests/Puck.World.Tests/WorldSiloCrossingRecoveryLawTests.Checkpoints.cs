using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldSiloCrossingRecoveryLawTests {
    // The store pauses at its first root read, before it can publish the captured checkpoint. A crossing begins
    // while that upload is pending. The pump is free, its upload barrier waits, and recovery retains the arrival.
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task ADelayedCheckpointUploadKeepsThePumpFreeAndTheLaterArrivalRecoverable(bool cadence) {
        using var pair = await SiloPair.ArrangeAsync();
        var original = pair.NewHost();
        using var instances = original.Instances;

        await ActivateAsync(host: original, identity: pair.Source);
        await ActivateAsync(host: original, identity: pair.Destination);
        Assert.True(condition: instances.TryGet(instance: out var rowA, name: "rowa"));
        Assert.True(condition: rowA!.Server.Population.TryAdmitRemotePeerAt(
            admitted: out _,
            grantTemplates: [],
            identityDomain: "example.test",
            identitySubject: "traveler",
            refusal: out _,
            slot: Slot,
            source: IntentSource.Live
        ));
        var traveler = rowA.Server.Population.ResolveIncarnation(authority: rowA.Server.AuthorityIdentity, index: Slot)!.Value;
        var sourceCheckpoint = original.CheckpointNowAsync(pair.Source, TestContext.Current.CancellationToken);

        await PumpAsync(host: original, operation: sourceCheckpoint);
        Assert.True(condition: await sourceCheckpoint);

        using var held = pair.Store.HoldNextRootRead(world: "rowb");
        var capturedOn = new TaskCompletionSource<int>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? checkpoint = null;

        // A dedicated pump thread cannot be reused by a queued upload after capture returns.
        var pump = new Thread(start: () => {
            try {
                if (cadence) {
                    original.NoteMasterStep(stepTicks: WorldAuthorityCheckpointCadence.EngineTicks);
                } else {
                    checkpoint = original.CheckpointNowAsync(pair.Destination, TestContext.Current.CancellationToken);
                    original.DrainActivationMailbox();
                }
                capturedOn.TrySetResult(result: Environment.CurrentManagedThreadId);
            } catch (Exception error) { capturedOn.TrySetException(exception: error); }
        });

        pump.Start();
        var pumpThread = await capturedOn.Task.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
        var uploadThread = await held.Started.Task.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);

        // An async store may do synchronous work before yielding. That work must never run on the pump.
        Assert.NotEqual(actual: uploadThread, expected: pumpThread);
        var uploads = original.WaitForCheckpointUploadsAsync(ct: TestContext.Current.CancellationToken);

        original.DrainActivationMailbox();
        Assert.False(condition: uploads.IsCompleted);
        var departure = pair.Store.ObserveNextJournalWrite(world: "rowa");

        _ = instances.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: "rowb"),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: "rowa",
            sourceSlot: Slot
        );
        var crossing = Task.Run(action: () => instances.DrainPendingTransfers(), cancellationToken: TestContext.Current.CancellationToken);

        try {
            await departure.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(condition: crossing.IsCompleted);
            Assert.False(condition: uploads.IsCompleted);
        } finally {
            held.Dispose();
            await crossing.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
        await PumpAsync(host: original, operation: uploads);
        Assert.True(condition: await uploads);
        if (checkpoint is not null) { Assert.True(condition: await checkpoint); }

        var recovery = (await pair.Backend.LoadRecoveryAsync(pair.Destination, TestContext.Current.CancellationToken))!.Value;

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: recovery.Checkpoint!.Value.Encoded.Span,
            checkpoint: out var captured,
            reason: out var reason
        ), userMessage: reason);
        Assert.Empty(collection: captured!.Population.Entries);
        var entry = Assert.Single(collection: recovery.Journal.Entries);

        Assert.Equal(expected: WorldAuthorityJournalEntryKind.Crossing, actual: entry.Kind);
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecodeCrossingEntry(
            bytes: entry.Encoded.Span,
            defaults: rowA.Server.Definition.PlayerDefaults,
            entry: out var arrival,
            reason: out reason
        ), userMessage: reason);
        Assert.IsType<WorldCrossingRecord.Arrival>(@object: arrival.Record);

        var replacement = pair.NewHost();
        using var recoveredInstances = replacement.Instances;

        await ActivateAsync(host: replacement, identity: pair.Source);
        await ActivateAsync(host: replacement, identity: pair.Destination);
        recoveredInstances.DrainPendingTransfers();
        Assert.Equal(expected: 1, actual: CountTraveler(host: replacement, traveler: traveler));
        Assert.True(condition: recoveredInstances.TryGet(instance: out var recoveredSource, name: "rowa"));
        Assert.Empty(collection: recoveredInstances.CaptureRow(row: recoveredSource!).InDoubtTransfers);
        Assert.False(condition: recoveredSource!.Server.Population.IsActive(index: Slot));
    }
}
