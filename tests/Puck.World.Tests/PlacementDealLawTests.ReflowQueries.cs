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
        var transport = new LoopbackTransport(server: fixture.Server);

        QueryAnswer Query(WorldQuery query) {
            var answer = default(QueryAnswer);

            transport.Query(
                query,
                WorldPrincipal.Console,
                result => answer = result
            );
            return answer;
        }
        Assert.False(condition: Query(query: new WorldQuery.ReflowPreview(Request: new WorldPlacementReflowRequest(TemplateId))).Refused);
        Assert.False(condition: fixture.Server.TryTakeReviewedReflow(
            principal: WorldPrincipal.Console,
            proposal: out _,
            reason: out _
        ));
        QueryAnswer status = default;
        using var timeout = new CancellationTokenSource(delay: TimeSpan.FromSeconds(seconds: 5));

        while (status.Payload is null) {
            status = Query(query: new WorldQuery.ReflowStatus());
            Assert.False(
                condition: status.Refused,
                userMessage: status.Text
            );
            if (status.Payload is null) { await Task.Delay(
                10,
                timeout.Token
            ); }
        }
        var proposal = Assert.IsType<WorldPlacementProposal>(@object: status.Payload);

        Assert.Equal(
            5,
            proposal.Cost
        );
        Assert.Equal(
            before,
            fixture.DefinitionBytes()
        );
        Assert.False(condition: Query(query: new WorldQuery.ReflowCancel()).Refused);
        Assert.False(condition: fixture.Server.TryTakeReviewedReflow(
            principal: WorldPrincipal.Console,
            proposal: out _,
            reason: out _
        ));
        Assert.Equal(
            before,
            fixture.DefinitionBytes()
        );
    }
}
