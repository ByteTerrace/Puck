using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    // ---- fields section: a presence byte, then one raw Q48.16 array per declared field ----

    private static byte[] EncodeFields(WorldFieldLattice.WorldFieldCheckpoint? section) {
        var writer = new WireWriter();

        writer.WriteBoolean(value: (section is not null));

        if (section is not null) {
            writer.WriteInt32(value: section.Raw.Count);

            foreach (var field in section.Raw) {
                WriteLongArray(
                    values: field,
                    writer: writer
                );
            }
        }

        return writer.ToArray();
    }
    private static bool TryDecodeFields(byte[] bytes, out string reason, out WorldFieldLattice.WorldFieldCheckpoint? section) {
        var reader = new WireReader(bytes: bytes);
        var present = reader.ReadBoolean();
        long[][] raw = [];

        if (present) {
            var count = reader.ReadCount(
                field: "fields count",
                maximum: WorldFieldCapacity.MaxFields,
                minimum: 0
            );

            if (!reader.Failed) {
                raw = new long[count][];

                for (var field = 0; (field < count); field++) {
                    raw[field] = ReadLongArray(
                        field: $"fields[{field}] cells",
                        reader: ref reader
                    );
                }
            }
        }

        if (!reader.TryFinish(failure: out var failure)) {
            section = null;
            reason = $"fields section: {failure}";

            return false;
        }

        section = (present
            ? new WorldFieldLattice.WorldFieldCheckpoint(Raw: raw)
            : null
        );
        reason = string.Empty;

        return true;
    }
}
