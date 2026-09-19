using System.Text.Json.Serialization;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// One tick-scheduled command: the exact simulation tick the host submits it at, the seat it acts as, and one
/// command line in the console/ingress vocabulary the host already accepts. The line is handed to the parser a
/// live stdin line reaches, under a session bound to <paramref name="Principal"/>, so admission, grants, phase
/// guards, and refusals behave as they do for a live actor.
/// </summary>
/// <param name="Tick">The completed-tick coordinate — the same coordinate <see cref="WorldCaptureRow.Ticks"/> arms
/// at — this command is submitted at. Submission is what the coordinate pins: an <c>Immediate</c>-routed verb runs
/// inline in that submission; a <c>Simulation</c>-routed verb folds into the next tick's snapshot and applies
/// during <c>Tick + 1</c>, as a live console line does. Tick <c>0</c> is refused — no step has completed
/// there.</param>
/// <param name="Principal">The acting identity, spelled as the engine's own principal label
/// (<see cref="WorldPrincipal.Describe"/>): <c>seat1</c>..<c>seat4</c>, parsed by
/// <see cref="WorldPrincipal.TryParse"/> so the seat ceiling here is the one the wire codec enforces. Every other
/// label is refused, <c>console</c> included: the console principal is trusted at every gate
/// (<c>Puck.World.Server.WorldGrants.IsTrusted</c>), so a step acting as it proves nothing about authority. The
/// power a scheduled step needs is authored in the document's own <c>grants</c>.</param>
/// <param name="Command">The command line, verbatim: non-empty, one line, not a <c>#</c> comment, and opening with
/// a verb <see cref="WorldScheduleCommands"/> admits. Argument shapes are not checked here — this project holds no
/// command registry — so a malformed argument becomes a recorded refusal at its tick rather than a boot
/// failure.</param>
/// <param name="Expect">What the ingress must answer for this row. Any other recorded outcome fails the world by
/// name: a step whose outcome nobody declared is a step nobody measured.</param>
/// <param name="Refusal">Text the recorded refusal detail must contain, or <see langword="null"/> to accept any
/// refusal. Legitimate only beside <see cref="WorldScheduleExpectation.Refused"/>.</param>
public sealed record WorldScheduleRow(ulong Tick, string Principal, string Command, WorldScheduleExpectation Expect = WorldScheduleExpectation.Submitted, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Refusal = null);
/// <summary>What a <see cref="WorldScheduleRow"/> declares its ingress will answer.</summary>
[JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<WorldScheduleExpectation>))]
public enum WorldScheduleExpectation {
    /// <summary>The ingress took the line. The default: a step exists to land, and a refusal it did not declare is a
    /// step that did not run.</summary>
    Submitted,
    /// <summary>The ingress or the handler refused the line, which is the point of the step — a test whose claim is
    /// that the world says no.</summary>
    Refused,
}
/// <summary>
/// The closed verb vocabulary a <see cref="WorldScheduleRow.Command"/> may open with: the steps that reach the
/// simulation through an ordinary actor's door — state mutations, guarded transforms, body intents and poses, and
/// joining or leaving a seat.
/// </summary>
/// <remarks>
/// <para>The set is enumerated here rather than derived from <c>CommandRegistry.RoutesToSimulation</c>: that
/// predicate answers whether a verb's verdict arrives at apply time, and <c>world.grant</c>, <c>world.revoke</c>,
/// <c>world.load</c> and <c>world.reload</c> all satisfy it while reaching outside the simulation — the grant
/// table, the filesystem. KEEP IN SYNC with the live registry through
/// <c>tests/Puck.Cli.Tests</c>'s <c>ScheduledStepVocabularyLawTests</c>, which reads the running host's own
/// affordance manifest: every verb here must exist there and route to the simulation, and every
/// simulation-routed verb there must be here or in that law's exclusion table with a reason.</para>
/// <para>Excluded by construction: every verb that touches the process, the clock, or the filesystem
/// (<c>quit</c>, <c>world.rate</c>, <c>world.save</c>, <c>world.load</c>, the capture and screenshot verbs), and
/// every verb that changes who may act. A step holding <c>world.grant</c> could widen its own authority, which is
/// the authority the step exists to exercise.</para>
/// </remarks>
public static class WorldScheduleCommands {
    private static readonly HashSet<string> AdmittedVerbs = new(
        collection: [
            "body.carry",
            "body.control",
            "body.disengage",
            "body.engage",
            "body.fly",
            "body.impulse",
            "body.motion",
            "body.pose",
            "body.press",
            "body.release",
            "body.state-load",
            "body.stop",
            "player.identity",
            "player.join",
            "player.leave",
            "player.row.set",
            "player.state.cell.set",
            "player.state.cell.toggle",
            "world.row.add",
            "world.row.remove",
            "world.row.set",
            "world.row.step",
            "world.state.act",
            "world.state.cell.remove",
            "world.state.cell.set",
            "world.state.transform",
        ],
        comparer: StringComparer.Ordinal
    );

    /// <summary>Gets every admitted verb, in ordinal order.</summary>
    public static IReadOnlyList<string> Admitted => [.. AdmittedVerbs.Order(comparer: StringComparer.Ordinal)];

    /// <summary>Determines whether a verb token is one this section admits.</summary>
    /// <param name="verb">The line's leading token.</param>
    /// <returns><see langword="true"/> when the verb is admitted.</returns>
    public static bool IsAdmitted(string verb) => AdmittedVerbs.Contains(item: verb);
    /// <summary>Returns the line's leading verb token — everything up to the first run of whitespace.</summary>
    /// <param name="command">The command line.</param>
    /// <returns>The verb token, or the empty string for a line that is all whitespace.</returns>
    public static string LeadingVerb(string? command) {
        var line = (command ?? string.Empty).AsSpan().TrimStart();
        var end = line.IndexOfAny(values: " \t");

        return ((end < 0)
            ? line.ToString()
            : line[..end].ToString()
        );
    }
}
/// <summary>
/// The <c>schedule</c> document section — tick-scheduled commands submitted through the host's ordinary ingress,
/// plus the tick the host writes this world's canonical state export at. Optional, like <c>captures</c>: a
/// document declaring none is unchanged, hashes as it did before this section existed, and writes nothing.
/// Boot-authored only — no mutation kind targets it and no grant subject names it.
/// </summary>
/// <remarks>
/// <para>The section is inert unless the boot arms it with <c>--schedule-dir</c>: any other boot of a document
/// carrying one submits no row and writes no export, and says so once on start-up. A world document travels — the
/// silo activates published ones — so a section that can submit commands may only run where the operator asked
/// for it.</para>
/// <para>The section owns the export tick rather than leaving it to a console fence because a console line is
/// drained when the command pump next runs, so the tick it lands on varies with process start-up and an export
/// taken there would carry a different tick, and hash, on every run. A document-declared tick is what lets
/// <c>puck test</c> compare two runs' exports byte for byte.</para>
/// </remarks>
/// <param name="Directory">The output directory this world's schedule writes into — the state export
/// (<see cref="ExportFileName"/>) and the submission manifest (<see cref="ManifestFileName"/>). Relative to the
/// process's current directory unless rooted, and only ever written when <c>--schedule-dir</c> armed the run, whose
/// path replaces this one; two runs of the same document therefore target sibling directories without two document
/// copies.</param>
/// <param name="SettleTicks">How many ticks the world keeps stepping past the last scheduled command's tick before
/// the export is written: the settle margin a <c>Simulation</c>-routed command's apply, the mutation drain, and
/// the rules reading its result need. At least <c>1</c> — a zero margin would export at the same tick the last
/// command was submitted, before it could have applied — and at most
/// <see cref="WorldScheduleCapacity.MaxSettleTicks"/>.</param>
/// <param name="Rows">The scheduled commands, ascending by tick. Rows may share a tick; they are then submitted in
/// declaration order, which is the whole ordering contract. Capacity
/// <see cref="WorldScheduleCapacity.MaxRows"/>. May be empty: a world whose expectations are decided by its rules
/// alone still wants the export.</param>
public sealed record WorldScheduleSection(string Directory, int SettleTicks, IReadOnlyList<WorldScheduleRow> Rows) {
    /// <summary>The canonical state export's file name inside <see cref="Directory"/> — a
    /// <c>puck.world.state-export.v1</c> document (<c>Puck.World.Server.WorldStateExport</c>).</summary>
    public const string ExportFileName = "state-export.json";
    /// <summary>The submission manifest's file name inside <see cref="Directory"/>.</summary>
    public const string ManifestFileName = "schedule.json";
    /// <summary>The submission manifest's schema id.</summary>
    public const string ManifestSchemaId = "puck.world.schedule-manifest.v1";
    /// <summary>The manifest outcome of a row still waiting for the tick lane to apply it when the export was
    /// written.</summary>
    public const string OutcomePending = "pending";
    /// <summary>The manifest outcome of a row whose handler THREW rather than judging the line. A host failure, never
    /// a refusal: nothing about the world was measured, so it carries its own exit code.</summary>
    public const string OutcomeFaulted = "faulted";
    /// <summary>The manifest outcome of a row the ingress or a handler refused.</summary>
    public const string OutcomeRefused = "refused";
    /// <summary>The manifest outcome of a row the ingress took. A verb whose whole effect is to submit a mutation
    /// reports this as soon as that submission is queued; the mutation's own verdict is an entry in the manifest's
    /// <c>echoes</c> instead.</summary>
    public const string OutcomeSubmitted = "submitted";
    /// <summary>The manifest outcome of a declared row whose tick the run never reached, so nothing was ever
    /// submitted for it. The manifest carries one entry per declared row, so this is what a dropped step reads as
    /// rather than an absent entry.</summary>
    public const string OutcomeUnreached = "unreached";
    /// <summary>The manifest outcome of a row whose authored principal has no ingress in the boot that ran
    /// it.</summary>
    public const string OutcomeUnroutable = "unroutable";

    /// <summary>Gets the exact tick the state export is written at: the last scheduled command's tick plus the
    /// settle margin, or the margin alone when nothing is scheduled.</summary>
    [JsonIgnore]
    public ulong ExportTick => (LastScheduledTick + ((ulong)Math.Max(
        val1: SettleTicks,
        val2: 1
    )));
    /// <summary>Gets the highest scheduled tick, or <c>0</c> when nothing is scheduled.</summary>
    [JsonIgnore]
    public ulong LastScheduledTick {
        get {
            var last = 0UL;
            var rows = (Rows ?? []);

            for (var index = 0; (index < rows.Count); index++) {
                if (rows[index] is { } row) {
                    last = Math.Max(
                        val1: last,
                        val2: row.Tick
                    );
                }
            }

            return last;
        }
    }
}
/// <summary>The <c>schedule</c> section's capacity ceilings and principal-label vocabulary.</summary>
public static class WorldScheduleCapacity {
    /// <summary>The longest admitted command line, in characters.</summary>
    public const int MaxCommandLength = 512;
    /// <summary>The largest admitted scheduled-command row count.</summary>
    public const int MaxRows = 256;
    /// <summary>The largest admitted settle margin, in ticks.</summary>
    public const int MaxSettleTicks = 4096;
    /// <summary>The <c>addon:</c> principal label's prefix, refused by this section.</summary>
    public const string PrincipalAddonPrefix = "addon:";
    /// <summary>The console principal's label, refused by this section.</summary>
    public const string PrincipalConsole = "console";
    /// <summary>The <c>peer:</c> principal label's prefix, refused by this section.</summary>
    public const string PrincipalPeerPrefix = "peer:";
    /// <summary>The seat principal label's prefix; the suffix is a 1-based seat number.</summary>
    public const string PrincipalSeatPrefix = "seat";

    /// <summary>Determines whether a principal label is one this section admits.</summary>
    /// <param name="principal">The authored label.</param>
    /// <param name="reason">When this method returns <see langword="false"/>, the refusal reason a validator
    /// message appends after the member path; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the label names a seat.</returns>
    /// <remarks>Parsing rides <see cref="WorldPrincipal.TryParse"/>, the grammar the wire codec and the grant door
    /// share, so the seat ceiling admitted here is the ceiling a submission is actually checked against; a
    /// second parse would admit a seat the wire then discards.</remarks>
    public static bool IsAdmittedPrincipal(string? principal, out string? reason) {
        if (string.IsNullOrWhiteSpace(value: principal)) {
            reason = $"is required — 'seat1'..'seat{WorldBodiesLimits.LocalSeatCount}'";

            return false;
        }

        if (!WorldPrincipal.TryParse(
            principal: out var parsed,
            token: principal
        )) {
            reason = $"is not a principal label — 'seat1'..'seat{WorldBodiesLimits.LocalSeatCount}', the seat a scheduled step acts as";

            return false;
        }

        if (parsed.Kind == PrincipalKind.Seat) {
            reason = null;

            return true;
        }

        reason = (parsed.Kind switch {
            PrincipalKind.Console => "names the console, which is trusted at every gate — a step acting as it would prove nothing about the authority the document granted; author the step's power in 'grants' and act as a seat",
            PrincipalKind.Peer => "names a peer — a peer principal carries a live admission generation no document can know",
            PrincipalKind.Addon => "names an addon — an addon principal has no text ingress door of its own, so a line submitted under it would be laundered through the console's",
            _ => $"names a {parsed.Kind} principal, which has no text ingress door — a scheduled step acts as 'seat1'..'seat{WorldBodiesLimits.LocalSeatCount}'",
        });

        return false;
    }
}
