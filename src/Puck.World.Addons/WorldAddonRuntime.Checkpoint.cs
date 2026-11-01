using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Scripting;
using Puck.World.Protocol;

namespace Puck.World.Addons;

public sealed partial class WorldAddonRuntime {
    private sealed record CellImage(string Subject, string Name);
    private sealed record MountImage(string Name, byte[] Guest, bool Pumped, AddonInCell[] Pending,
        bool Disclosed, int DisclosedRevision, int OverflowedAtRevision, string[] Drive, string[] Observe,
        MountedAddon.WatchState[]? Watches, ulong EventGap, ulong ReportedGap, ushort CellGeneration, CellImage[] Cells,
        byte[]? MachineSnapshot, string? SnapshotMachine, ulong SnapshotGeneration, ulong LastFuel, ulong TotalFuel, ulong PeakFuel,
        ulong AnswersDropped, ulong EventsDelivered, ulong RoutesDelivered, ulong CollisionsDelivered);
    [JsonSerializable(typeof(MountImage[]))]
    [JsonSourceGenerationOptions(Converters = [typeof(Puck.Abstractions.Documents.StrictEnumConverter<AddonInCellKind>),
        typeof(Puck.Abstractions.Documents.StrictEnumConverter<AddonVerdict>)])]
    private sealed partial class CheckpointJsonContext : JsonSerializerContext;

    /// <inheritdoc/>
    public byte[] CaptureCheckpoint() {
        var images = m_mounted.Select(selector: addon => new MountImage(addon.Instance.Name, addon.Instance.CaptureCheckpoint(),
            addon.HasEverPumped, addon.Pending.AsSpan(0, addon.PendingCount).ToArray(), addon.Disclosed,
            addon.DisclosedRevision, addon.OverflowedAtRevision,
            addon.DisclosedDrive.Select(selector: subject => subject.Describe()).ToArray(),
            addon.DisclosedObserve.Select(selector: subject => subject.Describe()).ToArray(), addon.MemoryWatchState,
            addon.EventGapCount, addon.LastReportedEventGap, addon.CellGeneration,
            addon.Cells.Select(selector: cell => new CellImage(cell.Subject.Describe(), cell.Name.Value)).ToArray(),
            addon.MachineSnapshot, addon.SnapshotMachine, addon.SnapshotGeneration,
            addon.LastTickFuelConsumed, addon.TotalFuelConsumed, addon.PeakTickFuelConsumed, addon.TotalAnswersDropped,
            addon.EventCellsDelivered, addon.RouteEventsDelivered, addon.CollisionEventsDelivered)).ToArray();

        return JsonSerializer.SerializeToUtf8Bytes(images, CheckpointJsonContext.Default.MountImageArray);
    }
    /// <inheritdoc/>
    public void RestoreCheckpoint(byte[] checkpoint) {
        var images = (JsonSerializer.Deserialize(checkpoint, CheckpointJsonContext.Default.MountImageArray) ?? throw new InvalidDataException(message: "Missing addon checkpoint."));

        if (!TryPrepare(current: null, candidate: m_server.Configuration, plan: out var plan, reason: out var reason)) {
            throw new InvalidOperationException(message: $"Addon checkpoint preparation refused: {reason}");
        }
        using var prepared = ((PreparedAddonInstall)plan!);

        if ((images.Length != prepared.Mounted.Count) || (images.Select(image => image.Name).Distinct(StringComparer.Ordinal).Count() != images.Length)) {
            throw new InvalidDataException(message: "Addon checkpoint mount inventory differs.");
        }
        foreach (var image in images) {
            var addon = (prepared.Mounted.SingleOrDefault(predicate: addon => (addon.Instance.Name == image.Name))
                ?? throw new InvalidDataException(message: $"Addon checkpoint names undeclared mount '{image.Name}'."));

            if ((image.Pending.Length >= addon.Instance.InputCellCapacity) || (image.Cells.Length > 4096) ||
                ((image.Watches?.Length ?? 0) != (addon.MemoryWatchState?.Length ?? 0)) ||
                (image.MachineSnapshot?.Length > ((4 * 1024) * 1024))) {
                throw new InvalidDataException(message: $"Addon '{image.Name}' checkpoint exceeds its declared capacity.");
            }
            addon.Instance.RestoreCheckpoint(image.Guest);
            image.Pending.CopyTo(addon.Pending, 0); addon.PendingCount = image.Pending.Length;
            addon.HasEverPumped = image.Pumped;
            addon.Disclosed = image.Disclosed; addon.DisclosedRevision = image.DisclosedRevision;
            addon.OverflowedAtRevision = image.OverflowedAtRevision;
            addon.DisclosedDrive = image.Drive.Select(ParseSubject).ToArray();
            addon.DisclosedObserve = image.Observe.Select(ParseSubject).ToArray();
            image.Watches?.CopyTo(addon.MemoryWatchState!, 0);
            addon.EventGapCount = image.EventGap; addon.LastReportedEventGap = image.ReportedGap;
            addon.CellGeneration = image.CellGeneration;
            addon.Cells = image.Cells.Select(cell => new BoundCell(ParseSubject(cell.Subject), CellName.Parse(cell.Name))).ToArray();
            addon.MachineSnapshot = image.MachineSnapshot;
            addon.SnapshotMachine = image.SnapshotMachine; addon.SnapshotGeneration = image.SnapshotGeneration;
            addon.LastTickFuelConsumed = image.LastFuel; addon.TotalFuelConsumed = image.TotalFuel; addon.PeakTickFuelConsumed = image.PeakFuel;
            addon.TotalAnswersDropped = image.AnswersDropped; addon.EventCellsDelivered = image.EventsDelivered;
            addon.RouteEventsDelivered = image.RoutesDelivered; addon.CollisionEventsDelivered = image.CollisionsDelivered;
        }
        CommitCore(prepared, restoring: true);
        Finish(plan: prepared);
    }

    private static GrantSubject ParseSubject(string text) => (GrantSubject.TryParse(subject: out var subject, token: text)
        ? subject : throw new InvalidDataException(message: $"Invalid addon checkpoint subject '{text}'."));
}
