using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>One scheduled command as it landed, wire-shaped to the <c>puck.world.schedule-manifest.v1</c> contract.</summary>
internal sealed record WorldScheduleManifestEntry(
    ulong Tick,
    string Principal,
    string Command,
    string Outcome,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail
);
/// <summary>One local edit verdict the run produced, as the server's own echo reported it.</summary>
/// <remarks>Carries no tick. An echo reaches this type when the server narrates it, which is not the tick coordinate
/// the edit applied on: the manifest records only facts a rerun of the same document reproduces exactly, so
/// <c>puck test</c> can compare two legs' manifests byte for byte.</remarks>
internal sealed record WorldScheduleManifestEcho(bool Rejected, string Message);
/// <summary>The <c>schedule.json</c> document a scheduled run writes beside its state export.</summary>
/// <param name="Schema">The manifest's schema id.</param>
/// <param name="World">The world document's file name.</param>
/// <param name="AuthoredExportTick">The tick the document's own <c>schedule</c> section derives.</param>
/// <param name="ExportTick">The tick the export beside this manifest was actually taken at. Below
/// <paramref name="AuthoredExportTick"/> only for a run that ended early.</param>
/// <param name="Truncated">Whether the run ended before it reached <paramref name="AuthoredExportTick"/>, so the
/// export beside this manifest is what the world reached rather than what it was asked for.</param>
/// <param name="Export">The state export's file name.</param>
/// <param name="Submissions">One entry per DECLARED row, in declaration order, whether or not its tick was ever
/// reached; a row the run never published records <see cref="WorldScheduleSection.OutcomeUnreached"/>.</param>
/// <param name="Echoes">Every local edit verdict the run recorded.</param>
internal sealed record WorldScheduleManifest(
    string Schema,
    string World,
    ulong AuthoredExportTick,
    ulong ExportTick,
    bool Truncated,
    string Export,
    IReadOnlyList<WorldScheduleManifestEntry> Submissions,
    IReadOnlyList<WorldScheduleManifestEcho> Echoes
);
/// <summary>
/// Submits the <c>schedule</c> section's commands at their authored ticks and writes the world's canonical state
/// export at the section's derived export tick. The authority tick-complete hook is the same one
/// <see cref="WorldCaptureScheduler"/> rides, so a scheduled command is submitted at a fixed point in the host
/// step and the same document produces the same submissions on every run.
/// </summary>
/// <remarks>
/// <para>A command line goes through <see cref="CommandRegistry.SubmitSession"/> under a session this type opened
/// for the row's authored principal, which is the door a live stdin line uses. An <c>Immediate</c>-routed verb
/// therefore answers synchronously and its verdict is recorded at once; a <c>Simulation</c>-routed verb is admitted
/// to the tick lane and answers when that tick applies, which reaches this type as an
/// <see cref="ICommandObserver"/> notification and upgrades the recorded outcome. Correlation is by (text,
/// principal) against the rows still awaiting an answer, so a stdin line that is character-for-character identical
/// to a still-pending scheduled row under the same principal could claim its answer.</para>
/// <para>A refusal is recorded and exported, never thrown: a test world often schedules a command precisely to
/// prove the world refuses it.</para>
/// </remarks>
internal sealed class WorldScheduleRunner : ICommandObserver {
    private sealed class Landed {
        public string Command = string.Empty;
        public string? Detail;
        public string Outcome = WorldScheduleSection.OutcomePending;
        public bool Pending;
        public CommandPrincipal Principal;
        public string PrincipalLabel = string.Empty;
        public ulong Tick;
    }

    /// <summary>The bound on recorded edit echoes, so a long run cannot grow the manifest without limit.</summary>
    public const int MaxEchoes = 256;

    // Relaxed escaping so an authored command line reads back as the author wrote it: a transform payload is all
    // quotes, and escaping every one of them makes the manifest unreadable for its one human audience.
    private static readonly JsonSerializerOptions ManifestSerializerOptions = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string m_directory;
    private readonly List<WorldScheduleManifestEcho> m_echoes = [];
    private readonly ulong m_exportTick;
    private readonly List<Landed> m_landed = [];
    private readonly Func<CommandRegistry> m_registry;
    private readonly Func<InputRouter> m_router;
    private readonly WorldTickSchedule<Landed> m_schedule = new();
    private readonly WorldServer m_server;
    private readonly Dictionary<string, TextCommandSession> m_sessions = new(comparer: StringComparer.Ordinal);
    private readonly Func<TextCommandSource> m_source;
    private readonly string m_worldFile;

    private bool m_exported;

    public WorldScheduleRunner(WorldServer server, WorldDefinitionSource definitionSource, Func<TextCommandSource> source, Func<CommandRegistry> registry, Func<InputRouter> router) {
        ArgumentNullException.ThrowIfNull(argument: server);
        ArgumentNullException.ThrowIfNull(argument: definitionSource);
        ArgumentNullException.ThrowIfNull(argument: registry);
        ArgumentNullException.ThrowIfNull(argument: router);
        ArgumentNullException.ThrowIfNull(argument: source);

        m_registry = registry;
        m_router = router;
        m_server = server;
        m_source = source;
        m_worldFile = Path.GetFileName(path: definitionSource.SourcePath);

        var schedule = server.Definition.Schedule;

        m_directory = ((schedule is { } declared) && WorldScheduleRoot.IsArmed
            ? WorldScheduleRoot.Resolve(authored: declared.Directory)
            : string.Empty
        );
        m_exportTick = (schedule?.ExportTick ?? 0UL);

        if (schedule is not { Rows: { } rows }) {
            return;
        }

        if (!WorldScheduleRoot.IsArmed) {
            Console.Error.WriteLine(value: $"[schedule] {m_worldFile} authors a schedule ({rows.Count} row(s), export tick {m_exportTick}) and this boot did not arm it — no row is submitted and no export is written. Pass --schedule-dir <directory> to run it.");

            return;
        }

        // Every declared row gets its manifest entry HERE, not at submission, so the manifest is total over the
        // document's rows: a row whose tick the run never reached records itself as unreached rather than going
        // missing, which is the difference between a dropped step and a silent one.
        foreach (var row in rows) {
            if (row is null) {
                continue;
            }

            var landed = new Landed {
                Command = row.Command,
                Outcome = WorldScheduleSection.OutcomeUnreached,
                PrincipalLabel = row.Principal,
                Tick = row.Tick,
            };

            m_landed.Add(item: landed);

            m_schedule.Add(
                row: landed,
                tick: row.Tick
            );
        }
    }

    /// <summary>Gets the tick the state export is written at, or <c>0</c> when no schedule is authored.</summary>
    public ulong ExportTick => m_exportTick;
    /// <summary>Gets a value indicating whether this run submits the document's schedule: the document authors one
    /// and the boot armed it with <c>--schedule-dir</c>.</summary>
    public bool IsArmed => ((m_server.Definition.Schedule is not null) && WorldScheduleRoot.IsArmed);

    // A handler that THREW judged nothing: the line reached it and it broke, which is a host failure rather than a
    // verdict about the world, so it never folds into "refused".
    private static string Answered(in CommandResult result) => (result.Faulted
        ? WorldScheduleSection.OutcomeFaulted
        : WorldScheduleSection.OutcomeRefused
    );
    private void Export(ulong tick, bool truncated) {
        m_exported = true;

        if (m_directory.Length == 0) {
            Console.Error.WriteLine(value: $"[schedule] tick {tick}: schedule.directory did not resolve — nothing written.");

            return;
        }

        try {
            _ = Directory.CreateDirectory(path: m_directory);

            var exportPath = Path.Combine(
                path1: m_directory,
                path2: WorldScheduleSection.ExportFileName
            );

            File.WriteAllBytes(
                bytes: WorldStateExport.ToCanonicalJson(server: m_server),
                path: exportPath
            );
            File.WriteAllText(
                contents: JsonSerializer.Serialize(
                    options: ManifestSerializerOptions,
                    value: new WorldScheduleManifest(
                        AuthoredExportTick: m_exportTick,
                        Export: WorldScheduleSection.ExportFileName,
                        ExportTick: tick,
                        Truncated: truncated,
                        Echoes: m_echoes,
                        Schema: WorldScheduleSection.ManifestSchemaId,
                        Submissions: [.. m_landed.Select(selector: static landed => new WorldScheduleManifestEntry(
                            Command: landed.Command,
                            Detail: landed.Detail,
                            Outcome: landed.Outcome,
                            Principal: landed.PrincipalLabel,
                            Tick: landed.Tick
                        ))],
                        World: m_worldFile
                    )
                ),
                path: Path.Combine(
                    path1: m_directory,
                    path2: WorldScheduleSection.ManifestFileName
                )
            );
            Console.Error.WriteLine(value: $"[schedule] tick {tick}: export -> {exportPath}");
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or NotSupportedException)) {
            Console.Error.WriteLine(value: $"[schedule] tick {tick}: the export could not be written: {exception.Message}");
        }
    }
    private void Submit(Landed landed) {
        var tick = landed.Tick;

        if (!TryOpenSession(
            principal: landed.PrincipalLabel,
            reason: out var reason,
            session: out var session
        )) {
            landed.Detail = reason;
            landed.Outcome = WorldScheduleSection.OutcomeUnroutable;

            Console.Error.WriteLine(value: $"[schedule] tick {tick}: {landed.PrincipalLabel} has no ingress — {reason}");

            return;
        }

        landed.Principal = session!.Principal;

        // A Simulation-routed line's own answer arrives at apply time, through OnCommand; mark it pending BEFORE
        // submitting, since an Immediate line answers inside this call and a lane line could in principle apply
        // before control returns here.
        var registry = m_registry();

        landed.Pending = registry.RoutesToSimulation(line: landed.Command);
        landed.Outcome = WorldScheduleSection.OutcomePending;

        var result = registry.SubmitSession(
            line: landed.Command,
            session: session
        );

        if (result.IsError) {
            landed.Pending = false;
            landed.Detail = result.Output;
            landed.Outcome = Answered(result: result);

            Console.Error.WriteLine(value: $"[schedule] tick {tick}: {landed.PrincipalLabel} {landed.Outcome} — {result.Output}");

            return;
        }

        if (landed.Pending) {
            return;
        }

        landed.Detail = ((result.Output.Length == 0)
            ? null
            : result.Output
        );
        landed.Outcome = WorldScheduleSection.OutcomeSubmitted;
    }
    private bool TryOpenSession(string principal, out TextCommandSession? session, out string? reason) {
        if (m_sessions.TryGetValue(
            key: principal,
            value: out var existing
        )) {
            reason = null;
            session = existing;

            return true;
        }

        reason = null;
        session = null;

        // Seats only: the section refuses every other principal label at validation, so a label reaching here that
        // is not a seat is a validator that stopped agreeing with this door.
        if (!WorldPrincipal.TryParse(
            principal: out var parsed,
            token: principal
        ) || (parsed.Kind != PrincipalKind.Seat)) {
            reason = "the label resolves to no text ingress door";

            return false;
        }

        // A schedule names the tick a step is submitted at, so the step lands in the tick after it however far the
        // simulation is running behind the wall clock.
        session = m_source().CreateSeatSession(
            dueNextTick: true,
            router: m_router(),
            slot: parsed.Index
        );
        m_sessions[principal] = session;

        return true;
    }

    /// <summary>Records a truncated run when the run ended before the authored export tick: the export beside the
    /// manifest is what the world reached, and the manifest says so rather than presenting it as the export.</summary>
    public void Drain() {
        if (
            !IsArmed ||
            m_exported
        ) {
            return;
        }

        var reached = ((m_server.NextInputTick > 0UL)
            ? (m_server.NextInputTick - 1UL)
            : 0UL
        );

        Console.Error.WriteLine(value: $"[schedule] the run ended at tick {reached} before the export tick {m_exportTick} — recording a truncated run, not an export.");
        Export(
            tick: reached,
            truncated: true
        );
    }
    /// <summary>Records one local edit verdict the server echoed — the door a buffered mutation's accept or
    /// refuse arrives through, which no command result carries.</summary>
    /// <param name="rejected">Whether the edit was refused.</param>
    /// <param name="message">The server's own reason or confirmation.</param>
    /// <remarks>Recorded rather than attributed to a row: a verb registers its correlation id inside its own
    /// submission, so nothing here can say which scheduled row an echo answers without guessing. A test world
    /// scheduling a command the world must refuse reads the refusal here, beside the row that submitted it.</remarks>
    public void NoteEcho(bool rejected, string message) {
        if (
            !IsArmed ||
            (m_echoes.Count >= MaxEchoes)
        ) {
            return;
        }

        m_echoes.Add(item: new WorldScheduleManifestEcho(
            Message: message,
            Rejected: rejected
        ));
    }
    /// <inheritdoc/>
    public void OnCommand(in CommandActivation activation) {
        if (activation.Text is not { } text) {
            return;
        }

        for (var index = 0; (index < m_landed.Count); index++) {
            var landed = m_landed[index];

            if (
                !landed.Pending ||
                (landed.Principal != activation.Principal) ||
                !string.Equals(
                a: landed.Command,
                b: text,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                continue;
            }

            landed.Pending = false;
            landed.Detail = ((activation.Result.Output.Length == 0)
                ? null
                : activation.Result.Output
            );
            landed.Outcome = (activation.Result.IsError
                ? Answered(result: activation.Result)
                : WorldScheduleSection.OutcomeSubmitted
            );

            if (activation.Result.IsError) {
                Console.Error.WriteLine(value: $"[schedule] tick {landed.Tick}: {landed.PrincipalLabel} {landed.Outcome} — {activation.Result.Output}");
            }

            return;
        }
    }
    /// <summary>The authority tick-complete hook: submits every command scheduled at <paramref name="tick"/> and
    /// writes the export when the tick is the schedule's export tick.</summary>
    /// <param name="tick">The completed tick.</param>
    public void PublishTick(ulong tick) {
        // A tick published twice (a rewound authority timeline) submits nothing the second time: the row's entry is
        // already answered, and a second submission would be a step the document never wrote.
        _ = m_schedule.Publish(
            fire: static (runner, row) => runner.Submit(landed: row),
            state: this,
            tick: tick
        );

        if (
            IsArmed &&
            !m_exported &&
            (tick >= m_exportTick)
        ) {
            Export(
                tick: tick,
                truncated: false
            );
        }
    }
}
