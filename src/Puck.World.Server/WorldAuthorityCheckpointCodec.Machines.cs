using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static byte[] EncodeMachines(WorldMachineHostCheckpoint section) {
        var writer = new WireWriter();

        writer.WriteUInt64(value: section.Revision);
        writer.WriteUInt64(value: section.NextGeneration);
        writer.WriteBoolean(value: section.AnyEverPumped);
        writer.WriteArray(
            items: section.Instances,
            writeItem: static (w, row) => {
                w.WriteString(value: row.Name); w.WriteString(value: row.Engine);
                w.WriteUInt64(value: row.Generation); w.WriteInt64(value: row.CompletedSteps);
                w.WriteBlock(value: row.RuntimeState);
            }
        );
        writer.WriteArray(
            items: section.Slots,
            writeItem: static (w, row) => {
                w.WriteInt32(value: row.Index);
                w.WriteInt32(value: row.SelectedEntry);
                w.WriteNullableString(value: row.LinkName);
            }
        );
        writer.WriteArray(
            items: section.Links,
            writeItem: static (w, row) => {
                w.WriteString(value: row.Name);
                WriteIntArray(
                    values: row.Members,
                    writer: w
                );
                w.WriteBoolean(value: row.Declared);
                w.WriteNullableString(value: row.DormantReason);
            }
        );
        return writer.ToArray();
    }
    private static bool TryDecodeMachines(byte[] bytes, out WorldMachineHostCheckpoint section, out string reason) {
        var reader = new WireReader(bytes: bytes);
        var revision = reader.ReadUInt64();
        var next = reader.ReadUInt64();
        var pumped = reader.ReadBoolean();
        var instances = reader.ReadArray(
            field: "machine instances",
            readItem: static (ref WireReader r) => new WorldMachineCheckpoint(
                r.ReadRequiredString(
                    field: "machine name",
                    maxBytes: MaxStringBytes
                ),
                r.ReadRequiredString(
                    field: "machine engine",
                    maxBytes: MaxStringBytes
                ),
                r.ReadUInt64(),
                r.ReadInt64(),
                r.ReadBlock(
                    field: "machine runtime",
                    maxBytes: MaxSectionBytes
                )
            ),
            maximum: MaxCollectionCount
        );
        var slots = reader.ReadArray(
            field: "machine slots",
            readItem: static (ref WireReader r) => new WorldMachineSlotCheckpoint(
                Index: r.ReadInt32(),
                SelectedEntry: r.ReadInt32(),
                LinkName: r.ReadNullableString(field: "machine slot link")
            ),
            maximum: MaxCollectionCount
        );
        var links = reader.ReadArray(
            field: "machine links",
            readItem: static (ref WireReader r) => new WorldMachineLinkCheckpoint(
                Name: r.ReadRequiredString(
                    field: "machine link name",
                    maxBytes: MaxStringBytes
                ),
                Members: ReadIntArray(
                    field: "machine link members",
                    reader: ref r
                ),
                Declared: r.ReadBoolean(),
                DormantReason: r.ReadNullableString(field: "machine link dormant reason")
            ),
            maximum: MaxCollectionCount
        );

        section = null!;
        if (!reader.TryFinish(failure: out var failure)) { reason = $"machine section: {failure}"; return false; }
        if (
            (next == 0) ||
            instances.Any(predicate: row => ((row.Generation == 0) || (row.Generation >= next) || (row.CompletedSteps < 0) || (row.RuntimeState.Length == 0))) ||
            (instances.Select(selector: row => row.Name).Distinct(comparer: StringComparer.Ordinal).Count() != instances.Length) ||
            (instances.Select(selector: row => row.Generation).Distinct().Count() != instances.Length)
        ) {
            reason = "machine section has invalid inventory or generation bookkeeping";
            return false;
        }
        if (
            slots.Any(predicate: static row => ((row.Index < 0) || (row.SelectedEntry < 0))) ||
            (slots.Select(selector: static row => row.Index).Distinct().Count() != slots.Length) ||
            (links.Select(selector: static row => row.Name).Distinct(comparer: StringComparer.Ordinal).Count() != links.Length) ||
            links.Any(predicate: static row => ((row.Members.Count < 2) || row.Members.Any(predicate: static member => (member < 0)) || (row.Members.Distinct().Count() != row.Members.Count))) ||
            slots.Any(predicate: row => ((row.LinkName is { } name) && !links.Any(predicate: link => (string.Equals(a: link.Name, b: name, comparisonType: StringComparison.Ordinal) && link.Members.Contains(value: row.Index)))))
        ) {
            reason = "machine section has invalid screen slots or links";
            return false;
        }
        section = new(
            AnyEverPumped: pumped,
            Instances: instances,
            Links: links,
            NextGeneration: next,
            Revision: revision,
            Slots: slots
        );
        reason = string.Empty;
        return true;
    }
}
