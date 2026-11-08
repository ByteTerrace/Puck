using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// One world of a disclosure pair inside a test worker: arms the variant's operator steps and watches each settle,
/// admits the observed sessions, arms the observed seat's authored and drawn console lines beside the world's own
/// schedule rows, drives the sessions' drawn actions tick by tick, and records into a
/// <see cref="WorldDisclosureTranscript"/> what each observed principal receives as it arrives: the seat's console
/// answers as their handlers returned them, the seat's edit echoes, and everything the sessions record themselves. At
/// the export it writes the transcript, invalid when a step was refused, answered late or never.
/// </summary>
/// <remarks>Every member runs on the host pump. Nothing here reads the world's state to decide an input: the inputs are
/// the fixture's, and the connection's operation ids are seeded, so both worlds of a pair receive identical
/// envelopes.</remarks>
internal sealed class WorldDisclosureRun : IDisposable {
    private readonly WorldDisclosureTranscript m_transcript;
    private readonly WorldDisclosureLeg m_leg;
    private readonly WorldTestRun m_run;
    private readonly WorldServer m_server;
    private readonly WorldTickScript m_script;
    private readonly LoopbackTransport m_transport;
    private readonly List<WorldDisclosureSession> m_sessions = [];
    private readonly Dictionary<WorldTickScript.Line, string> m_observed = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly Dictionary<WorldTickScript.Line, (WorldDisclosureStep Step, ulong? Answered)> m_steps = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ulong, List<(WorldDisclosureSession Session, WorldDisclosureInput Action)>> m_actions = [];
    private WorldDisclosureEchoTap? m_echoes;

    private WorldDisclosureRun(WorldDisclosureLeg leg, WorldTestRun run, WorldServer server, WorldTickScript script, LoopbackTransport transport) {
        m_leg = leg;
        m_run = run;
        m_server = server;
        m_script = script;
        m_transport = transport;
        m_transcript = new WorldDisclosureTranscript(clock: () => script.CompletedTick);
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
        var transport = services.GetRequiredService<LoopbackTransport>();
        var run = new WorldDisclosureRun(leg: leg, run: services.GetRequiredService<WorldTestRun>(), server: server, script: script, transport: transport);
        var slots = (leg.Fuzz.Slots ?? new Dictionary<string, IReadOnlyList<string>>());
        var words = (leg.Fuzz.Words ?? []);

        transport.OperationIds = WorldOperationIds.Seeded(seed: leg.Fuzz.Seed);
        script.Answered += run.Answered;
        foreach (var step in leg.Steps) {
            if (script.Arm(armed: out var line, line: step.Run, tick: step.At) is { } refusal) {
                throw new InvalidOperationException(message: $"variant '{leg.Variant}' step at {step.At} could not arm: {refusal}");
            }
            run.m_steps[line!] = (step, null);
        }
        foreach (var authority in leg.Sessions) {
            run.m_sessions.Add(item: WorldDisclosureSession.Observe(seed: leg.Fuzz.Seed, server: server, sourceAuthority: authority, transcript: run.m_transcript));
        }
        _ = PrincipalTokens.TryParse(principal: out var observer, token: leg.Observer);
        run.m_echoes = WorldDisclosureEchoTap.Attach(observed: new Dictionary<Principal, string> { [observer] = leg.Observer }, server: server, transcript: run.m_transcript);
        foreach (var (principal, line) in run.m_run.Lines) {
            if (string.Equals(a: principal, b: leg.Observer, comparisonType: StringComparison.Ordinal)) { run.m_observed[line] = leg.Observer; }
        }
        var verbs = (leg.Fuzz.Verbs ?? [.. registry.Definitions
            .Where(predicate: command => command.Audience.Admits(principal: observer))
            .Select(selector: static command => command.Name)
            .Where(predicate: verb => !WorldDisclosureFixture.Excludes(exclude: leg.Fuzz.Exclude, verb: verb))
            .Order(comparer: StringComparer.Ordinal)])
            .Select(selector: verb => new WorldDisclosureVerb(Forms: registry.Usage(name: verb), Name: verb))
            .ToArray();
        var authored = leg.Script.Select(selector: static step => new WorldDisclosureInput(Text: step.Run, Tick: step.At, Words: [step.Run]));
        var drawn = ((verbs.Length == 0) ? [] : WorldDisclosureFuzzer.Draw(count: leg.Fuzz.Lines, from: leg.From, seed: leg.Fuzz.Seed, slots: slots, until: leg.Until, verbs: verbs, words: words));

        foreach (var input in authored.Concat(second: drawn)) {
            if (script.ArmSeat(line: out var line, router: router, slot: observer.Index, text: input.Text, tick: input.Tick) is { } refusal) {
                throw new InvalidOperationException(message: $"the observer's line '{input.Text}' at {input.Tick} could not arm: {refusal}");
            }
            run.m_observed[line] = leg.Observer;
        }
        for (var index = 0; (index < run.m_sessions.Count); index++) {
            // Each session draws its own sequence, so adding a session never changes another's inputs.
            foreach (var input in WorldDisclosureFuzzer.Draw(count: leg.Fuzz.Actions, from: leg.From, seed: (leg.Fuzz.Seed + (ulong)(index + 1)), slots: slots,
                until: leg.Until, verbs: WorldDisclosureSession.Actions, words: words)) {
                if (!run.m_actions.TryGetValue(key: input.Tick, value: out var due)) { run.m_actions[input.Tick] = due = []; }
                due.Add(item: (run.m_sessions[index], input));
            }
        }
        run.m_run.Ticked += run.Act;
        return run;
    }

    /// <summary>Judges the variant's setup, ends the observed sessions and writes the transcript beside the run's
    /// exports.</summary>
    /// <param name="directory">The run's export directory.</param>
    public void Write(string directory) {
        foreach (var (step, answered) in m_steps.Values.OrderBy(keySelector: static entry => entry.Step.At)) {
            if (answered is not { } tick) {
                m_transcript.Invalidate(reason: string.Create(provider: CultureInfo.InvariantCulture, handler: $"variant step at {step.At} '{step.Run}' never answered before the export"));
            } else if ((step.At < m_leg.From) && (tick >= m_leg.From)) {
                // A step before the window is the setup its probes depend on: one still settling as they start tests a
                // world the fixture does not describe.
                m_transcript.Invalidate(reason: string.Create(provider: CultureInfo.InvariantCulture,
                    handler: $"variant step at {step.At} '{step.Run}' settled at tick {tick}, after the probes began at {m_leg.From}"));
            }
        }
        // A recipient's document edit revalidates the whole authority, so the write boundary's verdicts are a function
        // of disclosed rows only while the authority itself still validates whatever hidden members it holds. A variant
        // whose steps left it otherwise would make every such edit refuse for a reason hidden state decided, which is
        // the engine's defect, not the fixture's leak to judge.
        try {
            if (!WorldDefinitionValidator.TryValidateLocally(definition: m_server.ExportDefinition(), reason: out var invalid)) {
                m_transcript.Invalidate(reason: $"variant '{m_leg.Variant}' left a live document that no longer validates: {invalid}");
            }
        } catch (InvalidOperationException exception) {
            m_transcript.Invalidate(reason: $"variant '{m_leg.Variant}' left a live document that no longer validates: {exception.Message}");
        }
        Dispose();
        _ = Directory.CreateDirectory(path: directory);
        File.WriteAllBytes(bytes: m_transcript.Serialize(), path: Path.Combine(path1: directory, path2: WorldDisclosureTranscript.FileName));
    }
    /// <summary>Ends the observed sessions, stops driving them and hands the connection back its random operation ids.</summary>
    public void Dispose() {
        m_run.Ticked -= Act;
        m_script.Answered -= Answered;
        m_echoes?.Dispose();
        m_echoes = null;
        foreach (var session in m_sessions) { session.Dispose(); }
        m_sessions.Clear();
        m_transport.OperationIds = WorldOperationIds.Random;
    }

    private void Act(ulong tick) {
        if (m_actions.Remove(key: tick, value: out var due)) {
            foreach (var (session, action) in due) { session.Act(words: action.Words); }
        }
    }
    // A line answered: an observed one is recorded as its principal received it, a variant step settles or is refused.
    private void Answered(WorldTickScript.Line line, CommandResult result) {
        if (m_observed.TryGetValue(key: line, value: out var label)) {
            m_transcript.RecordResponse(line: line.Text, principal: label, result: in result);
            return;
        }
        if (!m_steps.TryGetValue(key: line, value: out var entry)) { return; }
        var tick = m_script.CompletedTick;

        m_steps[line] = (entry.Step, tick);
        if (result.IsError || result.Faulted) {
            m_transcript.Invalidate(reason: string.Create(provider: CultureInfo.InvariantCulture,
                handler: $"variant step at {entry.Step.At} '{entry.Step.Run}' was {(result.Faulted ? "faulted" : "refused")} at tick {tick}: {result.Output}"));
        } else {
            m_transcript.NoteSetup(note: string.Create(provider: CultureInfo.InvariantCulture, handler: $"step at {entry.Step.At} settled at tick {tick}: {entry.Step.Run}"));
        }
    }
}
