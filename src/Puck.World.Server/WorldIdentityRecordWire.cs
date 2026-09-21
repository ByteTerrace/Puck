using System.Text.Json;
using Puck.Networking;

namespace Puck.World.Server;

// Checkpoints and reservation projections carry exactly the same typed, bounded identity-record payload.
internal static class WorldIdentityRecordWire {
    public static void Write(WireWriter writer, WorldStateSection? records) {
        WorldIdentityRecords.Validate(section: records);
        var bytes = ((records is null) ? [] : JsonSerializer.SerializeToUtf8Bytes(records, WorldJsonContext.Default.WorldStateSection));

        if (bytes.Length > WireLimits.MaxDocumentBytes) {
            throw new InvalidOperationException(message: "identity records exceed the document wire budget");
        }
        writer.WriteBlock(value: bytes);
    }
    public static WorldStateSection? Read(ref WireReader reader) {
        var bytes = reader.ReadBlock(field: "identity records", maxBytes: WireLimits.MaxDocumentBytes);

        if (reader.Failed || (bytes.Length == 0)) {
            return null;
        }
        try {
            var records = (JsonSerializer.Deserialize(bytes, WorldJsonContext.Default.WorldStateSection)
                ?? throw new InvalidOperationException(message: "identity record payload is null"));

            WorldIdentityRecords.Validate(section: records);
            return records;
        } catch (Exception exception) when ((exception is JsonException or InvalidOperationException or ArgumentException or FormatException or NotSupportedException)) {
            reader.Fail(refusal: WireRefusal.PayloadMalformed, detail: $"identity records refuse: {exception.Message}");
            return null;
        }
    }
}
