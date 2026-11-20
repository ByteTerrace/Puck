using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // Installs a reload whose candidate changes nothing, only state values, or only what presentation reads, keeping the
    // arena, every compiled product, the solid field, the population and the authority as they stand. The source revision
    // moves to the next one and the accepted patch enters the undo journal. The candidate
    // was validated off the tick as its kind needs when its preparation still stands; otherwise it is validated here.
    private bool ApplyReloadEdit(WorldRebuildRequest request, Principal principal, int connectionId, long correlationId, string verb, WorldInstallKind kind, string changed,
        WorldDefinition candidate, WorldDefinition next, string contentHash, IReadOnlyList<string> conflicts, WorldReloadPreparation? prepared, byte[]? journalPatch) {
        var previous = m_definition;
        string detail;

        if (kind == WorldInstallKind.Nothing) {
            detail = ", nothing the session holds changed";
        } else {
            IReadOnlyList<string> rows;

            if ((prepared is not null) && ReferenceEquals(objA: prepared.Candidate, objB: candidate)) {
                rows = prepared.Rows;
            } else {
                _ = ReloadInstallKindOf(candidate: candidate, live: previous, rows: out rows);
            }
            if (!(prepared is { Validated: true } && ReferenceEquals(objA: prepared.Candidate, objB: candidate))) {
                var validated = ((kind == WorldInstallKind.Value)
                    ? WorldDefinitionValidator.TryValidateTouchedStateRows(definition: candidate, reason: out var validationReason, rowNames: rows)
                    : WorldDefinitionValidator.TryValidateChange(after: candidate, before: previous, compilation: out _, reason: out validationReason));

                if (!validated) {
                    RejectRebuild(connectionId: connectionId, correlationId: correlationId, reason: validationReason, verb: verb);
                    return false;
                }
            }
            WorldDocumentInputs? envelopeInputs = null;
            var plan = ((kind == WorldInstallKind.Presentation) ? PlanProducts(previous: previous, candidate: candidate).ForPresentation() : (WorldProductPlan?)null);

            if (plan is { Envelope.IsEmpty: false }) {
                if (!TryFitEnvelope(candidate: candidate, inputs: out var fitted, reason: out var capacityReason)) {
                    RejectRebuild(connectionId: connectionId, correlationId: correlationId, reason: capacityReason, verb: verb);
                    return false;
                }
                envelopeInputs = fitted;
            }
            if ((kind == WorldInstallKind.Value) && !Host.Arena.TryValidateLoad(rows: Host.ReplacedRows(definition: candidate, previous: previous), reason: out var loadReason)) {
                RejectRebuild(connectionId: connectionId, correlationId: correlationId, reason: $"the state section does not load into the arena: {loadReason}", verb: verb);
                return false;
            }
            if (!Host.GrantTable.TryPrepareDocument(actor: principal, candidate: candidate, current: previous, grantsWrite: false, prepared: out var authority, reason: out var authorityReason)) {
                RejectRebuild(connectionId: connectionId, correlationId: correlationId, reason: authorityReason, verb: verb);
                return false;
            }

            authority!.Install();
            if (kind == WorldInstallKind.Value) {
                InstallRuntimeStateValue(definition: candidate, rows: rows);
                detail = $", state rows re-seeded {rows.Count}, state kept";
            } else {
                InstallPresentation(definition: candidate);
                AdoptProductInputs(addons: null, envelope: envelopeInputs, machines: null);
                NarrateProducts(plan: plan!.Value, population: false);
                detail = ", presentation install, state kept";
            }
        }

        var history = AcceptReload(request, next, contentHash, journalPatch);

        var message = $"{verb} applied — {history}{detail}{changed}{DescribeConflicts(conflicts: conflicts)}";

        if ((request.Delta is { } appliedDelta) &&
            WorldSubmissionCodec.TryEncodeRebuild(request with { Definition = null }, out var payload, out _)) {
            message += $", delta={appliedDelta.Patch.Length} bytes payload={payload.Length} bytes";
        }
        if (Host.Output.HasNarrationSink) {
            Host.Output.Narrate(channel: "world.definition", text: $"[world.definition: {message}]");
        }
        Host.EchoTap?.Invoke(obj: new WorldEditEcho(
            Message: message,
            Rejected: false,
            Kind: WorldEditEchoKind.Rebuild,
            ConnectionId: connectionId,
            CorrelationId: correlationId,
            RebuildOrigin: request.Origin
        ));
        return true;
    }

    private string AcceptReload(WorldRebuildRequest request, WorldDefinition next, string contentHash, byte[]? patch, WorldDrawPositions? drawPositions = null) {
        m_revision = new WorldSourceRevision(ContentHash: contentHash, Definition: next);
        if (request.Origin is WorldRebuildOrigin.Store) {
            // A deployment changes the hosted release's base. A save by the local author changes the session.
            AdoptBase(m_definition, new WorldBaseOrigin(Kind: WorldBaseOriginKind.Reload, Source: request.Origin));
            m_journal.Clear();
            return $"base is {m_baseOrigin}, journal cleared";
        }
        if (patch is { Length: > 2 }) {
            var time = Host.DeliveryTime;
            m_journal.Add(new WorldJournalEntry(time.Tick, time.EngineTick, patch, drawPositions));
        }
        return $"journal entries={m_journal.Count}";
    }

    // The parts of the document a reload changed, for its verdict.
    private static string DescribeChanged(WorldDocumentInputs changed) => (changed.IsEmpty ? string.Empty : (", changed " + changed));
    // The rows and values a reload's source won from the session's own edits, for its verdict.
    private static string DescribeConflicts(IReadOnlyList<string> conflicts) => DescribeList(items: conflicts, lead: ", the source won over the session's edits at ");
    private static string DescribeList(IReadOnlyList<string> items, string lead) => ((items.Count == 0) ? string.Empty : (lead + string.Join(separator: ", ", values: items)));
}
