using System.Numerics;
using System.Text.Json;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a stopped instance answers every submission still pending, from any submitter, with a refusal that names
/// the stop, and refuses every submission after it the same way, so no submitter waits for a tick that never comes.
/// The red leg: while the instance runs, the same submission is buffered for the next tick and unanswered.
/// </summary>
public sealed class WorldInstanceStopLawTests {
    [Fact]
    public void AStoppedInstanceAnswersAPendingRowSetWithTheStop() {
        var row = WorldEditorPlacementLawTests.Build();
        var echoes = new WorldDeferredVerbEchoes();
        var crate = (WorldDefinitionRows.FindPlacement(id: "crate1", placements: row.Server.Definition.Placements)! with { Position = new Vector3(x: 9f, y: 3f, z: -1f) });
        WorldSubmissionResult? pending = null;
        WorldSubmissionResult? late = null;

        Assert.True(
            condition: WorldRowCommandModule.TryComposeRoutedSet(
                error: out var error,
                json: JsonSerializer.Serialize(value: crate, jsonTypeInfo: WorldJsonContext.Default.WorldPlacement),
                mutation: out var mutation,
                path: "placements",
                principal: Principal.Console
            ),
            userMessage: error
        );

        try {
            _ = row.Instance.Link.Submit(echoes: echoes, mutation: mutation!, observe: result => pending = result, verb: "world.row.set");

            // Red leg: buffered for the next tick, the submission is unanswered while the instance runs.
            Assert.Null(@object: pending);
        } finally {
            row.Dispose();
        }

        var answered = Assert.IsType<WorldSubmissionResult.Mutation>(@object: pending).Outcome;

        Assert.True(condition: answered.Refused);
        Assert.Equal(actual: answered.Code, expected: WorldServer.StoppedCode);
        Assert.Equal(actual: answered.Detail, expected: "instance 'boot' stopped");
        Assert.Equal(actual: ((Vector3)WorldDefinitionRows.FindPlacement(id: "crate1", placements: row.Server.Definition.Placements)!.Position).X, expected: 1.1f);

        // After the stop, a submission is refused at once, naming it.
        _ = row.Instance.Link.Submit(echoes: echoes, mutation: mutation!, observe: result => late = result, verb: "world.row.set");
        Assert.Equal(actual: Assert.IsType<WorldSubmissionResult.Refusal>(@object: late).Code, expected: WorldServer.StoppedCode);
    }
}
