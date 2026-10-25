using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed partial class AdmissionArrivalLawTests {
    [Fact]
    public void TransferLeasesRefuseExcessiveDurationAndExpiryRecoversCapacity() {
        using var fixture = Fixtures.FreshServer(definition: Document(admission: [Arrivals(domain: SourceAuthority)]));
        var escrow = new WorldTransferEscrow(fixture.Server);
        var request = Request(identity: null, transferId: 19);
        var maximumSteps = checked((((ulong)WorldTransferLimits.LeaseSeconds) * ((ulong)request.SourceRateHz)));

        Assert.Contains("lease duration", escrow.Reserve(request: request with { DeadlineSourceTick = (maximumSteps + 1) }).Reason);
        Assert.Equal(0, escrow.Counts.ActiveTransactions);
        var accepted = escrow.Reserve(request: request with { DeadlineSourceTick = maximumSteps });

        Assert.True(condition: accepted.Accepted, userMessage: accepted.Reason);
        escrow.ReclaimExpired(tick: accepted.DeadlineDestinationTick);
        Assert.Equal(0, escrow.Counts.ActiveTransactions);
        Assert.Equal(0, escrow.Counts.MobilityLeases);
        Assert.True(condition: escrow.Reserve(request: request with { TransferId = 20 }).Accepted);
    }
    [Fact]
    public void FullCommitHistoryPreservesRetriesAndAcknowledgementRecoversCapacity() {
        using var fixture = Fixtures.FreshServer(definition: Document(admission: [Arrivals(domain: SourceAuthority)]));
        var escrow = new WorldTransferEscrow(fixture.Server);
        var saved = escrow.Capture();
        var committed = Enumerable.Range(count: WorldTransferLimits.Transactions, start: 1).Select(selector: index =>
            new WorldTransferCommittedCheckpoint(new WorldTransferKey(SourceAuthority: SourceAuthority, TransferId: ((ulong)index)), [], [], [])).ToArray();

        escrow.Restore(checkpoint: saved with { Committed = committed });
        var request = Request(identity: null, transferId: (((ulong)WorldTransferLimits.Transactions) + 1));

        Assert.Contains("history capacity", escrow.Reserve(request: request).Reason);
        Assert.Equal(WorldTransferStatus.Committed, escrow.Status(sourceAuthority: SourceAuthority, transferId: 1));
        escrow.Acknowledge(sourceAuthority: SourceAuthority, transferId: 1);
        Assert.True(condition: escrow.Reserve(request: request).Accepted);
        Assert.Equal(WorldTransferLimits.Transactions, escrow.Counts.ActiveTransactions);
        escrow.Abort(sourceAuthority: SourceAuthority, transferId: request.TransferId);
        Assert.True(condition: escrow.Reserve(request: request with { TransferId = (request.TransferId + 1) }).Accepted);
    }
    [Fact]
    public void FullMobilityHistoryPreservesEpochsAndRetirementRecoversCapacity() {
        using var fixture = Fixtures.FreshServer(definition: Document(admission: [Arrivals(domain: SourceAuthority)]));
        var escrow = new WorldTransferEscrow(fixture.Server);
        var saved = escrow.Capture();
        var admissions = Enumerable.Range(count: WorldTransferLimits.MobilityAdmissions, start: 0).Select(selector: index => (
            SourceAuthority,
            new WorldEntityAddress(Authority: "old/world", Generation: 1, Index: index),
            1UL,
            Principal.Console)).ToArray();

        escrow.Restore(checkpoint: saved with { MobilityAdmissions = admissions });
        var request = Request(identity: null, transferId: 19);

        Assert.Contains("admission capacity", escrow.Reserve(request: request).Reason);
        Assert.Equal(WorldTransferLimits.MobilityAdmissions, escrow.Counts.MobilityCredentials);
        escrow.RetireMobility(mobility: new WorldMobilityIdentity(admissions[0].Item2, 1, admissions[0].Item2));
        Assert.True(condition: escrow.Reserve(request: request).Accepted);
        Assert.Equal(1, escrow.Counts.MobilityLeases);
        escrow.Abort(sourceAuthority: SourceAuthority, transferId: request.TransferId);
        Assert.True(condition: escrow.Reserve(request: request with { TransferId = 20 }).Accepted);
    }
}
