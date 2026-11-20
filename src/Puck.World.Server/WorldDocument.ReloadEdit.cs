using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // Installs a reload whose candidate changes nothing, only state values, or only what presentation reads, keeping the
    // arena, every compiled product, the solid field, the population and the authority as they stand. The source revision
    // moves to the next one and the base to the candidate, and the journal clears, as every reload does. The candidate
    // was validated off the tick as its kind needs when its preparation still stands; otherwise it is validated here.
    private bool ApplyReloadEdit(WorldRebuildRequest request, Principal principal, int connectionId, long correlationId, string verb, WorldInstallKind kind, string changed,
        WorldDefinition candidate, WorldDefinition next, string contentHash, IReadOnlyList<string> conflicts, WorldReloadPreparation? prepared) {
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
                    : WorldDefinitionValidator.TryValidateLocally(definition: candidate, reason: out validationReason));

                if (!validated) {
                    RejectRebuild(connectionId: connectionId, correlationId: correlationId, reason: validationReason, verb: verb);
                    return false;
                }
            }
            if ((kind == WorldInstallKind.Presentation) && !Host.Envelope.TryFit(candidate: candidate, reason: out var capacityReason)) {
                RejectRebuild(connectionId: connectionId, correlationId: correlationId, reason: capacityReason, verb: verb);
                return false;
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
                detail = ", presentation install, state kept";
            }
        }

        m_journal.Clear();
        m_base = m_definition;
        m_revision = new WorldSourceRevision(ContentHash: contentHash, Definition: next);
        m_baseOrigin = new WorldBaseOrigin(Kind: WorldBaseOriginKind.Reload, Source: request.Origin);

        var message = $"{verb} applied — base is {m_baseOrigin}, journal cleared{detail}{changed}{DescribeConflicts(conflicts: conflicts)}";

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

    // The root members a reload changed, for its verdict.
    private static string DescribeChanged(IReadOnlyList<string> changed) => DescribeList(items: changed, lead: ", changed ");
    // The rows and values a reload's source won from the session's own edits, for its verdict.
    private static string DescribeConflicts(IReadOnlyList<string> conflicts) => DescribeList(items: conflicts, lead: ", the source won over the session's edits at ");
    private static string DescribeList(IReadOnlyList<string> items, string lead) => ((items.Count == 0) ? string.Empty : (lead + string.Join(separator: ", ", values: items)));
}
