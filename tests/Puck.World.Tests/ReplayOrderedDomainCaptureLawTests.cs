using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: every ordered-domain payload kind a client can submit reaches the tape. The four that used to be
/// structurally uncaptured — <c>Mutation</c>, <c>Undo</c>, <c>Composition</c>, <c>Query</c> — now ride the same
/// per-kind <c>WorldSubmissionCodec</c> leaf pattern <c>Grant</c>/<c>Designation</c>/<c>ScreenOp</c> already use, so
/// a <c>replay.verify</c> MATCH is no longer structurally blind to a document edit typed mid-session.
/// <para>Proved by round-tripping a real armed recording through the on-disk tape: each of the four is submitted
/// through the ordinary <see cref="LoopbackTransport"/> door, the tape is persisted, and the file is read back — so
/// a missing tap, a missing encode arm, or a missing decode arm each fail this law for a real reason. The persisted
/// bytes are what is asserted against, never the in-memory list.</para>
/// </summary>
public sealed class ReplayOrderedDomainCaptureLawTests {
    [Fact]
    public void MutationUndoCompositionAndQuerySurviveTheTapeRoundTrip() {
        Fixtures.SkipIfReplayDirectoryUnwritable();

        using var fixture = Fixtures.FreshServer();

        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(
            liveServer: fixture.Server,
            profiles: fixture.Server.Profiles,
            transport: transport,
            engines: [],
            addonHostFactory: static (_, _) => new NullAddonHost()
        );
        var name = $"ordered-domain-capture-{Guid.NewGuid():N}";

        Assert.True(
            condition: tape.TryBeginRecording(
                name: name,
                refusal: out var refusal
            ),
            userMessage: $"refused to arm: {refusal}"
        );

        transport.SubmitWorldMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: WorldPrincipal.Console,
            Row: new WorldStateRow(Name: CellName.Parse(candidate: "tape-probe"), Kind: CellKind.Int)
        ));
        transport.SubmitUndo(
            count: 1,
            principal: WorldPrincipal.Console
        );
        transport.SubmitComposition(
            composition: new WorldComposition.SetActiveLayout(Name: null),
            principal: WorldPrincipal.Console
        );
        transport.Query(
            query: new WorldQuery.PlayerWhere(Index: 1),
            completion: static _ => { }
        );

        // The bucket only rotates onto a tick after a completed step, so the step and the NoteTick that closes it
        // are both required before anything above can reach the persisted file at all.
        fixture.Step();
        tape.NoteTick();

        _ = tape.StopRecording();

        using var stream = File.OpenRead(path: WorldReplayTape.PathFor(name: name));

        var snapshot = WorldReplaySnapshot.Read(stream: stream);
        var kinds = snapshot.Ticks
            .SelectMany(selector: static tick => tick.Authority)
            .Select(selector: static entry => entry.GetType().Name)
            .ToHashSet(comparer: StringComparer.Ordinal);

        Assert.Contains(collection: kinds, expected: "Mutation");
        Assert.Contains(collection: kinds, expected: "Undo");
        Assert.Contains(collection: kinds, expected: "Composition");
        Assert.Contains(collection: kinds, expected: "Query");
    }
}
