using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Scripting;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Addons;

public sealed partial class WorldAddonRuntime {
    private sealed record CellImage(string Subject, string Name);
    private sealed record MountImage(string Name, byte[] Module, byte[] Guest, bool Pumped, AddonInCell[] Pending,
        bool Disclosed, int DisclosedRevision, int OverflowedAtRevision, string[] Drive, string[] Observe,
        MountedAddon.WatchState[]? Watches, ulong EventGap, ulong ReportedGap, ushort CellGeneration, CellImage[] Cells,
        byte[]? MachineSnapshot, string? SnapshotMachine, ulong SnapshotGeneration, ulong LastFuel, ulong TotalFuel, ulong PeakFuel,
        ulong AnswersDropped, ulong EventsDelivered, ulong RoutesDelivered, ulong CollisionsDelivered);
    [JsonSerializable(typeof(MountImage[]))]
    [JsonSourceGenerationOptions(RespectRequiredConstructorParameters = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = [typeof(Puck.Abstractions.Documents.StrictEnumConverter<AddonInCellKind>),
        typeof(Puck.Abstractions.Documents.StrictEnumConverter<AddonVerdict>)])]
    private sealed partial class CheckpointJsonContext : JsonSerializerContext;

    /// <inheritdoc/>
    public byte[] CaptureCheckpoint() {
        var images = m_mounted.Select(selector: addon => new MountImage(addon.Instance.Name, addon.Instance.ModuleSource.ToArray(), addon.Instance.CaptureCheckpoint(),
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
    public IWorldAddonPreparedPlan PrepareCheckpoint(WorldDefinition definition, byte[] checkpoint) {
        MountImage[] images;

        try {
            images = ((checkpoint.Length == 0) ? [] : (JsonSerializer.Deserialize(checkpoint, CheckpointJsonContext.Default.MountImageArray)
            ?? throw new InvalidDataException(message: "Missing addon checkpoint.")));
        } catch (JsonException error) { throw new InvalidDataException(innerException: error, message: "Malformed addon checkpoint."); }
        if (images.Any(predicate: image => ((image is null) || (image.Name is null) || (image.Module is null) || (image.Guest is null) ||
            (image.Pending is null) || (image.Cells is null) || (image.Drive is null) || (image.Observe is null) ||
            image.Cells.Any(predicate: cell => ((cell is null) || (cell.Subject is null) || (cell.Name is null))))) ||
            (images.Select(selector: image => image.Name).Distinct(comparer: StringComparer.Ordinal).Count() != images.Length)) {
            throw new InvalidDataException(message: "Invalid addon checkpoint inventory.");
        }
        var modules = images.ToDictionary(image => image.Name, image => image.Module, StringComparer.Ordinal);

        if (!definition.Addons.Where(predicate: row => row.Enabled).Select(selector: row => row.Name).ToHashSet(comparer: StringComparer.Ordinal).SetEquals(other: modules.Keys)) {
            throw new InvalidDataException(message: "Addon checkpoint mount inventory differs.");
        }

        if (!TryPrepareCore(candidate: definition, current: null, modules: modules, plan: out var plan, reason: out var reason)) {
            throw new InvalidOperationException(message: $"Addon checkpoint preparation refused: {reason}");
        }
        var prepared = ((PreparedAddonInstall)plan!);

        try {

            if ((images.Length != prepared.Mounted.Count) || (images.Select(selector: image => image.Name).Distinct(comparer: StringComparer.Ordinal).Count() != images.Length)) {
                throw new InvalidDataException(message: "Addon checkpoint mount inventory differs.");
            }
            foreach (var image in images) {
                var addon = (prepared.Mounted.SingleOrDefault(predicate: addon => (addon.Instance.Name == image.Name))
                    ?? throw new InvalidDataException(message: $"Addon checkpoint names undeclared mount '{image.Name}'."));

                if ((image.Pending.Length >= addon.Instance.InputCellCapacity) || (image.Cells.Length > 4096) ||
                    ((image.Watches?.Length ?? 0) != (addon.MemoryWatchState?.Length ?? 0)) ||
                    (image.MachineSnapshot?.Length > MaxMachineSnapshotBytes)) {
                    throw new InvalidDataException(message: $"Addon '{image.Name}' checkpoint exceeds its declared capacity.");
                }
                addon.Instance.RestoreCheckpoint(checkpoint: image.Guest);
                image.Pending.CopyTo(array: addon.Pending, index: 0); addon.PendingCount = image.Pending.Length;
                addon.HasEverPumped = image.Pumped;
                addon.Disclosed = image.Disclosed; addon.DisclosedRevision = image.DisclosedRevision;
                addon.OverflowedAtRevision = image.OverflowedAtRevision;
                addon.DisclosedDrive = image.Drive.Select(selector: ParseSubject).ToArray();
                addon.DisclosedObserve = image.Observe.Select(selector: ParseSubject).ToArray();
                image.Watches?.CopyTo(array: addon.MemoryWatchState!, index: 0);
                addon.EventGapCount = image.EventGap; addon.LastReportedEventGap = image.ReportedGap;
                addon.CellGeneration = image.CellGeneration;
                addon.Cells = image.Cells.Select(selector: cell => new BoundCell(ParseSubject(text: cell.Subject), CellName.Parse(candidate: cell.Name))).ToArray();
                addon.MachineSnapshot = image.MachineSnapshot;
                addon.SnapshotMachine = image.SnapshotMachine; addon.SnapshotGeneration = image.SnapshotGeneration;
                addon.LastTickFuelConsumed = image.LastFuel; addon.TotalFuelConsumed = image.TotalFuel; addon.PeakTickFuelConsumed = image.PeakFuel;
                addon.TotalAnswersDropped = image.AnswersDropped; addon.EventCellsDelivered = image.EventsDelivered;
                addon.RouteEventsDelivered = image.RoutesDelivered; addon.CollisionEventsDelivered = image.CollisionsDelivered;
            }
            prepared.Restoring = true;
            return prepared;
        } catch { prepared.Dispose(); throw; }
    }

    private static GrantSubject ParseSubject(string text) => (GrantSubject.TryParse(subject: out var subject, token: text)
        ? subject : throw new InvalidDataException(message: $"Invalid addon checkpoint subject '{text}'."));
}
