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

            instances.Add(item: instance.Lease switch {
                MachineLease.Running { Runtime: IMachineCheckpointRuntime runtime } running => new(
                    pair.Key,
                    instance.Declaration.Engine,
                    WorldMachineCheckpointKind.Running,
                    running.Generation,
                    running.CompletedSteps,
                    runtime.CaptureCheckpoint()
                ),
                MachineLease.Running => throw new InvalidOperationException(message: $"machine '{pair.Key}' ({instance.Declaration.Engine}) has no durable checkpoint capability"),
                MachineLease.Refused refused => new(
                    pair.Key,
                    instance.Declaration.Engine,
                    WorldMachineCheckpointKind.Refused,
                    refused.Generation,
                    0L,
                    []
                ),
                _ => throw new InvalidOperationException(message: $"machine '{pair.Key}' holds an unknown lease"),
            });
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
        CheckpointRestore.Validate(
            checkpoint: checkpoint,
            host: this,
            instances: m_instances,
            screenEntries: m_slots.ToDictionary(
                keySelector: static pair => pair.Key,
                elementSelector: static pair => (pair.Value.Magazine?.Entries.Count ?? 0)
            )
        ).Apply();
    }
    /// <inheritdoc/>
    public void ValidateCheckpoint(WorldMachineHostCheckpoint checkpoint, IWorldMachinePreparedPlan plan) {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(plan);
        if (
            (plan is not PreparedMachinePlan prepared) ||
            !ReferenceEquals(
            objA: prepared.Instances.Owner,
            objB: this
        )
        ) {
            throw new ArgumentException(message: "The machine plan was not prepared by this host.", paramName: nameof(plan));
        }
        RequireCheckpointInventory();
        _ = CheckpointRestore.Validate(
            checkpoint: checkpoint,
            host: this,
            instances: prepared.Instances.Candidate,
            screenEntries: prepared.CandidateScreens.ToDictionary(
                keySelector: static screen => screen.Index,
                elementSelector: static screen => (screen.Magazine?.Entries.Count ?? 0)
            )
        );
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

    // A restore applies completely or changes nothing. This plan enforces it: its constructor is private, so the only
    // plan Apply ever sees is one Validate built after checking every row's name, engine and kind, every running row's
    // image with its runtime (TryValidateCheckpoint, the same checks behind the same accepted steps as the restore),
    // and every slot and link, all before anything moves. Apply makes no check that can refuse. The guarantee stops at
    // a runtime whose RestoreCheckpoint throws on an image its TryValidateCheckpoint accepted, which is a defect in that
    // runtime: the runtimes before it in name order are then already restored.
    private sealed class CheckpointRestore {
        private readonly WorldMachineHost m_host;
        private readonly WorldMachineHostCheckpoint m_checkpoint;
        private readonly List<(MachineLease Lease, WorldMachineCheckpoint Row)> m_rows;

        private CheckpointRestore(WorldMachineHost host, WorldMachineHostCheckpoint checkpoint, List<(MachineLease Lease, WorldMachineCheckpoint Row)> rows) {
            m_host = host;
            m_checkpoint = checkpoint;
            m_rows = rows;
        }

        // instances and screenEntries describe the host the restore runs against: its live machines and screens, or
        // the ones a prepared plan would commit (screenEntries maps a screen index to its magazine's entry count).
        public static CheckpointRestore Validate(WorldMachineHost host, WorldMachineHostCheckpoint checkpoint, IReadOnlyDictionary<string, MachineInstance> instances, IReadOnlyDictionary<int, int> screenEntries) {
            var hostSlots = screenEntries;

            if (
                (checkpoint.Instances is null) ||
                (checkpoint.Instances.Count != instances.Count) ||
                (checkpoint.NextGeneration == 0) ||
                checkpoint.Instances.Any(predicate: row => ((row.Generation == 0) || (row.Generation >= checkpoint.NextGeneration) || !row.IsWellFormed)) ||
                (checkpoint.Instances.Select(selector: row => row.Generation).Distinct().Count() != checkpoint.Instances.Count) ||
                (checkpoint.Instances.Select(selector: row => row.Name).Distinct(comparer: StringComparer.Ordinal).Count() != checkpoint.Instances.Count)
            ) {
                throw new InvalidDataException(message: "machine checkpoint has invalid inventory or generations");
            }

            var rows = new List<(MachineLease Lease, WorldMachineCheckpoint Row)>(capacity: checkpoint.Instances.Count);

            foreach (var row in checkpoint.Instances) {
                if (
                    !instances.TryGetValue(
                    key: row.Name,
                    value: out var instance
                ) ||
                    (instance.Declaration.Engine != row.Engine)
                ) {
                    throw new InvalidDataException(message: $"machine checkpoint '{row.Name}' does not match the declared runtime");
                }
                if ((instance.Lease.Kind != row.Kind) && !AdoptsPowerOn(lease: instance.Lease, row: row)) {
                    throw new InvalidDataException(message: $"machine checkpoint '{row.Name}' records a {row.Kind} machine where this host's is {instance.Lease.Kind}{((instance.Lease.Fault is { } fault) ? $" ({fault})" : string.Empty)}");
                }
                if ((instance.Lease is MachineLease.Running running) && (row.Kind == WorldMachineCheckpointKind.Running)) {
                    if (running.Runtime is not IMachineCheckpointRuntime runtime) {
                        throw new InvalidDataException(message: $"machine checkpoint '{row.Name}' names a runtime with no durable checkpoint capability");
                    }
                    if (!runtime.TryValidateCheckpoint(
                        checkpoint: row.RuntimeState,
                        reason: out var reason
                    )) {
                        throw new InvalidDataException(message: $"machine checkpoint '{row.Name}' carries a runtime image its machine refuses: {reason}");
                    }
                }
                rows.Add(item: (instance.Lease, row));
            }
            if (
                (checkpoint.Slots is null) ||
                (checkpoint.Links is null) ||
                (checkpoint.Slots.Count != hostSlots.Count) ||
                checkpoint.Slots.Any(predicate: row => !hostSlots.ContainsKey(key: row.Index)) ||
                (checkpoint.Slots.Select(selector: static row => row.Index).Distinct().Count() != checkpoint.Slots.Count)
            ) {
                throw new InvalidDataException(message: $"machine checkpoint names screens [{string.Join(separator: ", ", values: (checkpoint.Slots ?? []).Select(selector: static row => row.Index))}] where this host declares [{string.Join(separator: ", ", values: hostSlots.Keys.Order())}]");
            }
            foreach (var row in checkpoint.Slots) {
                var entries = hostSlots[row.Index];

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
                checkpoint.Links.Any(predicate: link => ((link.Members.Count < 2) || link.Members.Any(predicate: member => !hostSlots.ContainsKey(key: member)) || (link.Members.Distinct().Count() != link.Members.Count)))
            ) {
                throw new InvalidDataException(message: "machine checkpoint holds a link named twice, with fewer than two members, a repeated member, or an undeclared screen");
            }
            return new(
                checkpoint: checkpoint,
                host: host,
                rows: rows
            );
        }

        // A row recorded while its machine was refused carries no runtime image. When this host now prepares that
        // machine, the restore keeps the runtime at its power-on image, as a world booting it would, so content that
        // was missing when the checkpoint was taken never refuses the whole recovery. Only a runtime that has neither
        // stepped nor queued a step is at that image; one that ran on is refused as any other mismatch. A running row
        // on a host that now refuses the machine refuses the restore, so its image is never discarded silently.
        private static bool AdoptsPowerOn(MachineLease lease, WorldMachineCheckpoint row) =>
            ((row.Kind == WorldMachineCheckpointKind.Refused) &&
            (lease is MachineLease.Running running) &&
            (((running.Runtime as IQueuedMachineRuntime)?.CompletedSteps ?? running.CompletedSteps) == 0) &&
            (((running.Runtime as IQueuedMachineRuntime)?.PendingSteps ?? 0) == 0));

        public void Apply() {
            foreach (var (lease, row) in m_rows) {
                if ((lease is MachineLease.Running running) && (row.Kind == WorldMachineCheckpointKind.Running)) {
                    ((IMachineCheckpointRuntime)running.Runtime).RestoreCheckpoint(checkpoint: row.RuntimeState);
                }
            }
            foreach (var (lease, row) in m_rows) {
                lease.Generation = row.Generation;
                if (lease is MachineLease.Running running) {
                    running.CompletedSteps = row.CompletedSteps;
                }
            }
            // Every link this host holds is torn down first, so a link the checkpoint does not record is gone and a
            // recorded one takes its recorded members; recorded links are dormant, because a coupled one refused capture.
            foreach (var name in m_host.m_links.Keys.ToList()) {
                m_host.TeardownLink(name: name);
            }
            foreach (var link in m_checkpoint.Links) {
                m_host.m_links[link.Name] = new LinkEntry {
                    Declared = link.Declared,
                    DormantReason = link.DormantReason,
                    Link = null,
                    Members = [.. link.Members],
                    Name = link.Name,
                };
            }
            foreach (var row in m_checkpoint.Slots) {
                var slot = m_host.m_slots[row.Index];

                slot.SelectedEntry = row.SelectedEntry;
                slot.LinkName = row.LinkName;
            }
            m_host.m_instanceRevision = m_checkpoint.Revision;
            m_host.m_nextInstanceGeneration = m_checkpoint.NextGeneration;
            m_host.AnyEverPumped = m_checkpoint.AnyEverPumped;
        }
    }
}
