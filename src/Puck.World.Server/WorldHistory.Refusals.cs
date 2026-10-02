using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldHistory {
    // The documents the current seek, diff, or replay-edit read and proved against their recorded hashes before
    // anything moved, by the recorded entry that names them. The re-simulation installs these rather than reading the
    // file again, so a file that changes mid-operation can never refuse from inside a step.
    private readonly Dictionary<WorldReplayEntry.Rebuild, WorldDefinition> m_verifiedRebuilds = new(comparer: ReferenceEqualityComparer.Instance);

    private string? UncapturableLiveState() {
        if (m_server.AnyAddonEverPumped || (m_server.Addons is { MountedCount: > 0 })) {
            return "addon guest state cannot be restored by a history keyframe";
        }
        if (m_server.AnyScreenOpEverApplied) {
            return "screen or machine operations changed state outside the checkpoint inventory";
        }
        if (m_server.AnyMachineEverPumped && (m_server.Machines is not IWorldMachineCheckpointHost)) {
            return "a stepped machine has no checkpoint support";
        }
        if (m_server.GrantTable.LiveSessionPrincipals().Count != 0) {
            return "live session input and grants are not captured by authority checkpoints";
        }
        return m_server.Persistence.ReplayTimelineResetRefusal();
    }
    private static bool ChangesAddons(WorldMutation mutation) => mutation switch {
        WorldMutation.UpsertAddon or WorldMutation.RemoveAddon => true,
        WorldMutation.Batch batch => batch.Mutations.Any(predicate: ChangesAddons),
        _ => false,
    };
    private string? RecordedEntryRefusal(WorldReplayEntry entry) {
        if (Unrewindable(entry: entry) is { } external) {
            return external;
        }
        if (entry is WorldReplayEntry.ScreenOp) {
            return "a screen operation changes state outside the checkpoint inventory";
        }
        if ((entry is WorldReplayEntry.Mutation mutation) && ChangesAddons(mutation: mutation.Value)) {
            return "an addon edit requires guest state that history cannot restore";
        }
        if (entry is not WorldReplayEntry.Rebuild { Kind: WorldRebuildKind.Load or WorldRebuildKind.Reload } rebuild) {
            return null;
        }
        if (rebuild.PathHint is not { } path) {
            return "the recorded rebuild has no content source";
        }
        if (!WorldDefinitionLoader.TryLoadFileForAdmission(
            admission: out var admission,
            catalog: m_server.Machines.ValidationCatalog,
            contentHash: out var contentHash,
            documents: m_server.RebuildDocuments,
            instanceIdentity: m_server.InstanceIdentity,
            path: path,
            proveNeighbours: false,
            reason: out var reason
        )) {
            return $"the recorded rebuild's content cannot be read from '{path}': {reason}";
        }
        if (!string.Equals(a: contentHash, b: rebuild.ContentHash, comparisonType: StringComparison.Ordinal)) {
            return $"the recorded rebuild's content changed at '{path}': expected {rebuild.ContentHash}, found {contentHash}";
        }
        if (admission!.Definition.Addons.Any(predicate: static addon => addon.Enabled)) {
            return "the recorded rebuild mounts addon guests whose state history cannot restore";
        }

        m_verifiedRebuilds[rebuild] = admission.Definition;

        return null;
    }
    private string? RecordedSpanRefusal(ulong from, ulong to) {
        m_verifiedRebuilds.Clear();

        foreach (var (tick, entry) in EntriesBetween(from: from, to: to)) {
            if (RecordedEntryRefusal(entry: entry) is { } reason) {
                return $"at tick {tick} {reason}";
            }
        }
        return null;
    }
    // How a re-simulated rebuild re-applies: from the document the preflight verified, its pin holding by
    // construction; a reset reads the restored base, which no file can change. A load or reload the preflight did not
    // verify is a host defect, never a fresh read: the span's preflight is what makes every refusal precede the move.
    private (WorldDefinition? Verified, string? Pin) VerifiedRebuild(WorldReplayEntry.Rebuild rebuild) {
        if (rebuild.Kind == WorldRebuildKind.Reset) {
            return (null, null);
        }

        if (!m_verifiedRebuilds.TryGetValue(key: rebuild, value: out var verified)) {
            throw new InvalidOperationException(message: $"the recorded {rebuild.Kind} of '{rebuild.PathHint}' re-applied without its span's preflight — a host defect");
        }

        return (verified, rebuild.ContentHash);
    }
}
