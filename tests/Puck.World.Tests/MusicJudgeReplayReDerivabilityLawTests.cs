using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>MusicClock</c>/<c>MusicDirector</c>/<c>RhythmJudge</c>'s replay claim — neither is
/// captured directly in <c>WorldReplaySnapshot</c>, on the stated claim that both are purely RE-DERIVABLE from the
/// document plus tick 0, never taped. Proves it directly rather than assuming it: the IDENTICAL script against the
/// IDENTICAL document, run in TWO independent fresh <see cref="WorldServer"/> boots, must produce the
/// byte-identical <c>music.state</c>/<c>judge.state</c> stream at every checkpoint. A wall-clock read anywhere on
/// this path (the class the "no wall clock in simulation state" rule exists to catch) would make the two runs
/// diverge, since real time elapses differently across two sequential process-local runs.
/// </summary>
public sealed class MusicJudgeReplayReDerivabilityLawTests {
    [Fact]
    public void IdenticalScriptReDerivesTheIdenticalMusicAndJudgeStream() {
        var directoryA = Directory.CreateTempSubdirectory(prefix: "puck-replay-law-a-").FullName;
        var directoryB = Directory.CreateTempSubdirectory(prefix: "puck-replay-law-b-").FullName;

        try {
            var streamA = RunScriptAndCollect(assetDirectory: directoryA);
            var streamB = RunScriptAndCollect(assetDirectory: directoryB);

            Assert.Equal(actual: streamB, expected: streamA);
        } finally {
            Directory.Delete(path: directoryA, recursive: true);
            Directory.Delete(path: directoryB, recursive: true);
        }
    }

    /// <summary>Drives one fresh, independent boot through the identical script and collects its
    /// <c>music.state</c>/<c>judge.state</c> stream, in order. A fresh <paramref name="assetDirectory"/> per call —
    /// <see cref="ActionEffectJudgeLawTests.BuildJudgePressDocument"/> writes real Music/Judge asset files an
    /// absolute <c>Source</c> resolves against, so the SAME document content is authored twice, over two DIFFERENT
    /// directories, rather than two servers racing one shared file.</summary>
    private static IReadOnlyList<string> RunScriptAndCollect(string assetDirectory) {
        using var fixture = Fixtures.FreshServer(definition: ActionEffectJudgeLawTests.BuildJudgePressDocument(assetDirectory: assetDirectory));
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;
        var stream = new List<string>();

        for (var step = 0; (step < 9); step++) {
            fixture.Step();
        }

        stream.Add(item: fixture.Server.Answer(query: new WorldQuery.MusicState(Index: 1)).Text);

        body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: ActionEffectJudgeLawTests.JudgeOrdinal, value: Puck.Maths.FixedQ4816.One));
        fixture.Step();

        stream.Add(item: fixture.Server.Answer(query: new WorldQuery.JudgeState(Index: 1)).Text);

        for (var step = 0; (step < 10); step++) {
            fixture.Step();
        }

        body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: ActionEffectJudgeLawTests.JudgeOrdinal, value: Puck.Maths.FixedQ4816.Zero));
        fixture.Step();
        body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: ActionEffectJudgeLawTests.JudgeOrdinal, value: Puck.Maths.FixedQ4816.One));
        fixture.Step();

        stream.Add(item: fixture.Server.Answer(query: new WorldQuery.MusicState(Index: 1)).Text);
        stream.Add(item: fixture.Server.Answer(query: new WorldQuery.JudgeState(Index: 1)).Text);

        return stream;
    }
}
