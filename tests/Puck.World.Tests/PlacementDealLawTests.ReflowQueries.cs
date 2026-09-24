using Puck.Commands;
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
                Principal.Console,
                result => answer = result
            );
            return answer;
        }
        Assert.False(condition: Query(query: new WorldQuery.ReflowPreview(Request: new WorldPlacementReflowRequest(TemplateId))).Refused);
        Assert.False(condition: fixture.Server.TryTakeReviewedReflow(
            principal: Principal.Console,
            proposal: out _,
            reason: out _
        ));
        await fixture.Server.ReflowPreviewCompletion(principal: Principal.Console).WaitAsync(cancellationToken: TestContext.Current.CancellationToken);

        var status = Query(query: new WorldQuery.ReflowStatus());

        Assert.False(
            condition: status.Refused,
            userMessage: status.Text
        );
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
            principal: Principal.Console,
            proposal: out _,
            reason: out _
        ));
        Assert.Equal(
            before,
            fixture.DefinitionBytes()
        );
    }
}
