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
                throw new InvalidOperationException(message: $"machine '{pair.Key}' has no durable checkpoint capability");
            }
            instances.Add(item: new(
                pair.Key,
                instance.Declaration.Engine,
                instance.Lease.Generation,
                instance.Lease.CompletedSteps,
                runtime.CaptureCheckpoint()
            ));
        }
        return new(
            m_instanceRevision,
            m_nextInstanceGeneration,
            AnyEverPumped,
            instances
        );
    }
    /// <inheritdoc/>
    public void RestoreCheckpoint(WorldMachineHostCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(checkpoint);
        RequireCheckpointInventory();
        if (
            AnyEverPumped ||
            (checkpoint.Instances is null) ||
            (checkpoint.Instances.Count != m_instances.Count) ||
            (checkpoint.NextGeneration == 0) ||
            checkpoint.Instances.Any(predicate: row => ((row.Generation == 0) || (row.Generation >= checkpoint.NextGeneration) || (row.CompletedSteps < 0) || (row.RuntimeState is not { Length: > 0 }))) ||
            (checkpoint.Instances.Select(selector: row => row.Generation).Distinct().Count() != checkpoint.Instances.Count) ||
            (checkpoint.Instances.Select(selector: row => row.Name).Distinct(comparer: StringComparer.Ordinal).Count() != checkpoint.Instances.Count)
        ) {
            throw new InvalidDataException(message: "machine checkpoint has invalid inventory, generations, or restore timing");
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
        foreach (var row in checkpoint.Instances) {
            var instance = m_instances[row.Name];

            ((IMachineCheckpointRuntime)instance.Lease.Runtime).RestoreCheckpoint(checkpoint: row.RuntimeState);
            instance.Lease.Generation = row.Generation;
            instance.Lease.CompletedSteps = row.CompletedSteps;
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
        if (m_links.Values.Any(predicate: link => (link.Link is not null))) {
            throw new InvalidOperationException(message: "world checkpoint cannot yet preserve a live coupled machine link");
        }
        if (m_slots.Values.Any(predicate: slot => (slot.Machine is not null))) {
            throw new InvalidOperationException(message: "world checkpoint cannot yet preserve a screen-owned machine operation");
        }
    }
}
