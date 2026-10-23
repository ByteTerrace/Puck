using Puck.Abstractions.Machines;

namespace Puck.World.Server;

public sealed partial class WorldMachineHost : IWorldMachineCheckpointHost {
    /// <inheritdoc/>
    public WorldMachineHostCheckpoint CaptureCheckpoint() {
        RequireCheckpointInventory();
        var instances = new List<WorldMachineCheckpoint>(capacity: m_instances.Count);

        foreach (var pair in m_instances.OrderBy(
            pair => pair.Key,
            StringComparer.Ordinal
        )) {
            var instance = pair.Value;

            if (instance.Lease.Runtime is not IMachineCheckpointRuntime runtime) {
                throw new InvalidOperationException(message: $"machine '{pair.Key}' ({instance.Declaration.Engine}) has no durable checkpoint capability");
            }
            instances.Add(item: new(
                pair.Key,
                instance.Declaration.Engine,
                instance.Lease.Generation,
                instance.Lease.CompletedSteps,
                runtime.CaptureCheckpoint()
            ));
        }

        var slots = new List<WorldMachineSlotCheckpoint>(capacity: m_slots.Count);

        foreach (var slot in m_slots.Values.OrderBy(keySelector: static slot => slot.Index)) {
            slots.Add(item: new WorldMachineSlotCheckpoint(
                Index: slot.Index,
                LinkName: slot.LinkName,
                SelectedEntry: slot.SelectedEntry
            ));
        }

        var links = new List<WorldMachineLinkCheckpoint>(capacity: m_links.Count);

        foreach (var entry in m_links.Values.OrderBy(keySelector: static entry => entry.Name, comparer: StringComparer.Ordinal)) {
            links.Add(item: new WorldMachineLinkCheckpoint(
                Declared: entry.Declared,
                DormantReason: entry.DormantReason,
                Members: [.. entry.Members],
                Name: entry.Name
            ));
        }

        return new(
            AnyEverPumped: AnyEverPumped,
            Instances: instances,
            Links: links,
            NextGeneration: m_nextInstanceGeneration,
            Revision: m_instanceRevision,
            Slots: slots
        );
    }
    /// <inheritdoc/>
    public void RestoreCheckpoint(WorldMachineHostCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(checkpoint);
        RequireCheckpointInventory();
        if (
            (checkpoint.Instances is null) ||
            (checkpoint.Instances.Count != m_instances.Count) ||
            (checkpoint.NextGeneration == 0) ||
            checkpoint.Instances.Any(predicate: row => ((row.Generation == 0) || (row.Generation >= checkpoint.NextGeneration) || (row.CompletedSteps < 0) || (row.RuntimeState is not { Length: > 0 }))) ||
            (checkpoint.Instances.Select(selector: row => row.Generation).Distinct().Count() != checkpoint.Instances.Count) ||
            (checkpoint.Instances.Select(selector: row => row.Name).Distinct(comparer: StringComparer.Ordinal).Count() != checkpoint.Instances.Count)
        ) {
            throw new InvalidDataException(message: "machine checkpoint has invalid inventory or generations");
        }
        foreach (var row in checkpoint.Instances) {
            if (
                !m_instances.TryGetValue(
                key: row.Name,
                value: out var instance
            ) ||
                (instance.Declaration.Engine != row.Engine) ||
                (instance.Lease.Runtime is not IMachineCheckpointRuntime)
            ) {
                throw new InvalidDataException(message: $"machine checkpoint '{row.Name}' does not match the declared runtime");
            }
        }
        if (
            (checkpoint.Slots is null) ||
            (checkpoint.Links is null) ||
            (checkpoint.Slots.Count != m_slots.Count) ||
            checkpoint.Slots.Any(predicate: row => !m_slots.ContainsKey(key: row.Index)) ||
            (checkpoint.Slots.Select(selector: static row => row.Index).Distinct().Count() != checkpoint.Slots.Count)
        ) {
            throw new InvalidDataException(message: $"machine checkpoint names screens [{string.Join(separator: ", ", values: (checkpoint.Slots ?? []).Select(selector: static row => row.Index))}] where this host declares [{string.Join(separator: ", ", values: m_slots.Keys.Order())}]");
        }
        foreach (var row in checkpoint.Slots) {
            var slot = m_slots[row.Index];
            var entries = (slot.Magazine?.Entries.Count ?? 0);

            if (
                (row.SelectedEntry < 0) ||
                (row.SelectedEntry >= Math.Max(val1: 1, val2: entries))
            ) {
                throw new InvalidDataException(message: $"machine checkpoint selects entry {row.SelectedEntry} on screen {row.Index}, whose magazine holds {entries}");
            }
            if (
                (row.LinkName is { } linkName) &&
                !checkpoint.Links.Any(predicate: link => (string.Equals(a: link.Name, b: linkName, comparisonType: StringComparison.Ordinal) && link.Members.Contains(value: row.Index)))
            ) {
                throw new InvalidDataException(message: $"machine checkpoint puts screen {row.Index} in link '{linkName}', which it does not record with that member");
            }
        }
        if (
            (checkpoint.Links.Select(selector: static link => link.Name).Distinct(comparer: StringComparer.Ordinal).Count() != checkpoint.Links.Count) ||
            checkpoint.Links.Any(predicate: link => ((link.Members.Count < 2) || link.Members.Any(predicate: member => !m_slots.ContainsKey(key: member)) || (link.Members.Distinct().Count() != link.Members.Count)))
        ) {
            throw new InvalidDataException(message: "machine checkpoint holds a link named twice, with fewer than two members, a repeated member, or an undeclared screen");
        }
        foreach (var row in checkpoint.Instances) {
            var instance = m_instances[row.Name];

            ((IMachineCheckpointRuntime)instance.Lease.Runtime).RestoreCheckpoint(checkpoint: row.RuntimeState);
            instance.Lease.Generation = row.Generation;
            instance.Lease.CompletedSteps = row.CompletedSteps;
        }
        // Every link this host holds is torn down first, so a link the checkpoint does not record is gone and a recorded
        // one takes its recorded members; recorded links are dormant, because a coupled one refused capture.
        foreach (var name in m_links.Keys.ToList()) {
            TeardownLink(name: name);
        }
        foreach (var link in checkpoint.Links) {
            m_links[link.Name] = new LinkEntry {
                Declared = link.Declared,
                DormantReason = link.DormantReason,
                Link = null,
                Members = [.. link.Members],
                Name = link.Name,
            };
        }
        foreach (var row in checkpoint.Slots) {
            var slot = m_slots[row.Index];

            slot.SelectedEntry = row.SelectedEntry;
            slot.LinkName = row.LinkName;
        }
        m_instanceRevision = checkpoint.Revision;
        m_nextInstanceGeneration = checkpoint.NextGeneration;
        AnyEverPumped = checkpoint.AnyEverPumped;
    }

    private void RequireCheckpointInventory() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        foreach (var entry in m_links.Values) {
            if (entry.Link is not null) {
                throw new InvalidOperationException(message: $"link '{entry.Name}' couples screens {string.Join(separator: "+", values: entry.Members)} live, and a world checkpoint cannot yet preserve a coupled machine link; screen.unlink {entry.Name} first");
            }
        }
    }
}
