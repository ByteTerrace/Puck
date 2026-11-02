using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// One world of a disclosure pair inside a test worker: arms the variant's operator steps, admits the observed sessions,
/// arms the observed seat's drawn console lines beside the world's own schedule rows, drives the sessions' drawn actions
/// tick by tick, and at the export writes everything the observed principals received as a
/// <see cref="WorldDisclosureTranscript"/>.
/// </summary>
/// <remarks>Every member runs on the host pump. Nothing here reads the world's state to decide an input: the inputs are
/// the fixture's, so both worlds of a pair receive the same ones.</remarks>
internal sealed class WorldDisclosureRun : IDisposable {
    private readonly WorldDisclosureTranscript m_transcript = new();
    private readonly WorldDisclosureLeg m_leg;
    private readonly WorldTestRun m_run;
    private readonly List<WorldDisclosureSession> m_sessions = [];
    private readonly List<WorldTickScript.Line> m_lines = [];
    private readonly Dictionary<ulong, List<(WorldDisclosureSession Session, string Action)>> m_actions = [];

    private WorldDisclosureRun(WorldDisclosureLeg leg, WorldTestRun run) {
        m_leg = leg;
        m_run = run;
    }

    /// <summary>Arms one world of a pair; call after the test run has been reset for the leg.</summary>
    /// <param name="leg">The pair's leg.</param>
    /// <param name="services">The booted host's services.</param>
    /// <returns>The armed run.</returns>
    /// <exception cref="InvalidOperationException">A step or drawn line could not be armed.</exception>
    public static WorldDisclosureRun Start(WorldDisclosureLeg leg, IServiceProvider services) {
        var script = services.GetRequiredService<WorldTickScript>();
        var registry = services.GetRequiredService<Func<CommandRegistry>>()();
        var router = services.GetRequiredService<Func<InputRouter>>();
        var server = services.GetRequiredService<WorldServer>();
        var run = new WorldDisclosureRun(leg: leg, run: services.GetRequiredService<WorldTestRun>());

        foreach (var step in leg.Steps) {
            if (script.Arm(line: step.Run, tick: step.At) is { } refusal) { throw new InvalidOperationException(message: $"variant '{leg.Variant}' step at {step.At} could not arm: {refusal}"); }
        }
        foreach (var authority in leg.Sessions) {
            run.m_sessions.Add(item: WorldDisclosureSession.Observe(seed: leg.Fuzz.Seed, server: server, sourceAuthority: authority, transcript: run.m_transcript));
        }
        _ = PrincipalTokens.TryParse(principal: out var observer, token: leg.Observer);
        var verbs = (leg.Fuzz.Verbs ?? [.. registry.Definitions
            .Where(predicate: static command => (command.Audience == CommandAudience.Anyone))
            .Select(selector: static command => command.Name)
            .Where(predicate: verb => !WorldDisclosureFixture.Excludes(exclude: leg.Fuzz.Exclude, verb: verb))
            .Order(comparer: StringComparer.Ordinal)]);

        var authored = leg.Script.Select(selector: static step => new WorldDisclosureInput(Text: step.Run, Tick: step.At));
        var drawn = WorldDisclosureFuzzer.Draw(count: leg.Fuzz.Lines, from: leg.From, seed: leg.Fuzz.Seed, until: leg.Until, verbs: verbs, words: (leg.Fuzz.Words ?? []));

        foreach (var input in authored.Concat(second: drawn)) {
            if (script.ArmSeat(line: out var line, router: router, slot: observer.Index, text: input.Text, tick: input.Tick) is { } refusal) {
                throw new InvalidOperationException(message: $"the observer's line '{input.Text}' at {input.Tick} could not arm: {refusal}");
            }
            run.m_lines.Add(item: line);
        }
        for (var index = 0; (index < run.m_sessions.Count); index++) {
            // Each session draws its own sequence, so adding a session never changes another's inputs.
            foreach (var input in WorldDisclosureFuzzer.Draw(count: leg.Fuzz.Actions, from: leg.From, seed: (leg.Fuzz.Seed + (ulong)(index + 1)), until: leg.Until,
                verbs: WorldDisclosureSession.Actions, words: (leg.Fuzz.Words ?? []))) {
                if (!run.m_actions.TryGetValue(key: input.Tick, value: out var due)) { run.m_actions[input.Tick] = due = []; }
                due.Add(item: (run.m_sessions[index], input.Text));
            }
        }
        run.m_run.Ticked += run.Act;
        return run;
    }

    /// <summary>Ends the observed sessions and writes the transcript beside the run's exports.</summary>
    /// <param name="directory">The run's export directory.</param>
    public void Write(string directory) {
        var observed = m_run.Lines
            .Where(predicate: line => string.Equals(a: line.Principal, b: m_leg.Observer, comparisonType: StringComparison.Ordinal))
            .Select(selector: static line => line.Line)
            .Concat(second: m_lines)
            .OrderBy(keySelector: static line => line.Tick);

        foreach (var line in observed) {
            var verb = line.Text.AsSpan().Trim();
            var end = verb.IndexOf(value: ' ');

            m_transcript.Record(bytes: $"{line.Text} -> {line.Outcome}: {line.Answer}", door: ((end < 0) ? verb : verb[..end]).ToString(),
                principal: m_leg.Observer, tick: line.Tick);
        }
        Dispose();
        _ = Directory.CreateDirectory(path: directory);
        File.WriteAllBytes(bytes: m_transcript.Serialize(), path: Path.Combine(path1: directory, path2: WorldDisclosureTranscript.FileName));
    }
    /// <summary>Ends the observed sessions and stops driving them.</summary>
    public void Dispose() {
        m_run.Ticked -= Act;
        foreach (var session in m_sessions) { session.Dispose(); }
        m_sessions.Clear();
    }

    private void Act(ulong tick) {
        if (m_actions.Remove(key: tick, value: out var due)) {
            foreach (var (session, action) in due) { session.Act(action: action); }
        }
    }
}
