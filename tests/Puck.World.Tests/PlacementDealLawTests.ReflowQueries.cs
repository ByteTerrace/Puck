using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class PlacementDealLawTests {
    [Fact]
    public async Task TypedReflowQueriesReviewAndCancelTheSameDetachedCommitArtifact() {
        using var fixture = Fixtures.FreshServer(ReflowDocument());
        fixture.Step();
        OverlapChildren(fixture);
        var before = fixture.DefinitionBytes();
        var transport = new LoopbackTransport(fixture.Server);
        QueryAnswer Query(WorldQuery query) {
            var answer = default(QueryAnswer);
            transport.Query(query, WorldPrincipal.Console, result => answer = result);
            return answer;
        }
        Assert.False(Query(new WorldQuery.ReflowPreview(new WorldPlacementReflowRequest(TemplateId))).Refused);
        Assert.False(fixture.Server.TryTakeReviewedReflow(WorldPrincipal.Console, out _, out _));
        QueryAnswer status = default;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (status.Payload is null) {
            status = Query(new WorldQuery.ReflowStatus());
            Assert.False(status.Refused, status.Text);
            if (status.Payload is null) { await Task.Delay(10, timeout.Token); }
        }
        var proposal = Assert.IsType<WorldPlacementProposal>(status.Payload);
        Assert.Equal(5, proposal.Cost);
        Assert.Equal(before, fixture.DefinitionBytes());
        Assert.False(Query(new WorldQuery.ReflowCancel()).Refused);
        Assert.False(fixture.Server.TryTakeReviewedReflow(WorldPrincipal.Console, out _, out _));
        Assert.Equal(before, fixture.DefinitionBytes());
    }
}
