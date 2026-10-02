using System.Text.Json;
using Puck.Networking;

namespace Puck.World.Server;

/// <summary>
/// The one wire form of a <see cref="WorldIdentityProjection"/>. Every leaf that carries a traveler's identity writes it
/// here: a federation reservation and commit, a crossing-log and arrival-tape arrival, the escrow and in-doubt rows a
/// checkpoint keeps, and a checkpointed body's profile. It writes the projection's id, name, color, the two rates, the
/// selected records and the facts row, and nothing from the owned document behind them. Every reader is bounded and
/// Try-shaped, since a federated peer's bytes are untrusted.
/// </summary>
public static class WorldIdentityProjectionWire {
    /// <summary>Writes a projection.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="projection">The projection to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The records or the facts row are not a shape a projection carries, or
    /// the records exceed the document wire budget.</exception>
    public static void Write(WireWriter writer, WorldIdentityProjection projection) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        writer.WriteString(value: projection.Id);
        writer.WriteString(value: projection.Name);
        writer.WriteString(value: projection.ColorHex);
        writer.WriteNullableFixed(value: projection.MoveSpeed);
        writer.WriteNullableFixed(value: projection.TurnSpeed);
        WriteRecords(
            records: projection.Records,
            writer: writer
        );
        WriteFacts(
            facts: projection.Facts,
            writer: writer
        );
    }
    /// <summary>Reads a projection written by <see cref="Write"/>, failing the reader by name on a malformed one.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The projection; meaningful only while the reader has not failed.</returns>
    public static WorldIdentityProjection Read(ref WireReader reader) => new(
        Id: reader.ReadRequiredString(field: "identity id"),
        Name: reader.ReadString(field: "identity name"),
        ColorHex: reader.ReadString(field: "identity color"),
        MoveSpeed: reader.ReadNullableFixed(),
        TurnSpeed: reader.ReadNullableFixed(),
        Records: ReadRecords(reader: ref reader),
        Facts: ReadFacts(reader: ref reader)
    );
    /// <summary>Writes a projection that may be absent.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="projection">The projection, or <see langword="null"/>.</param>
    public static void WriteOptional(WireWriter writer, WorldIdentityProjection? projection) => writer.WriteOptional(
        value: projection,
        writeValue: static (w, value) => Write(
            projection: value,
            writer: w
        )
    );
    /// <summary>Reads a projection written by <see cref="WriteOptional"/>.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The projection, or <see langword="null"/> when none was written.</returns>
    public static WorldIdentityProjection? ReadOptional(ref WireReader reader) => reader.ReadOptional(
        readValue: static (ref WireReader r) => Read(reader: ref r)
    );
    /// <summary>Returns whether two projections carry the same bytes on this wire: the one equality an idempotent
    /// commit receipt compares travelers by.</summary>
    /// <param name="left">The first projection, or <see langword="null"/>.</param>
    /// <param name="right">The second projection, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when both are absent, or both encode to identical bytes.</returns>
    public static bool Matches(WorldIdentityProjection? left, WorldIdentityProjection? right) {
        if (
            (left is not { } a) ||
            (right is not { } b)
        ) {
            return (!left.HasValue && !right.HasValue);
        }

        var leftWriter = new WireWriter();
        var rightWriter = new WireWriter();

        Write(
            projection: a,
            writer: leftWriter
        );
        Write(
            projection: b,
            writer: rightWriter
        );

        return leftWriter.WrittenSpan.SequenceEqual(other: rightWriter.WrittenSpan);
    }

    private static void WriteRecords(WireWriter writer, WorldStateSection? records) {
        WorldIdentityRecords.Validate(section: records);

        var bytes = ((records is null)
            ? []
            : JsonSerializer.SerializeToUtf8Bytes(
                jsonTypeInfo: WorldJsonContext.Default.WorldStateSection,
                value: records
            ));

        if (bytes.Length > WireLimits.MaxDocumentBytes) {
            throw new InvalidOperationException(message: "identity records exceed the document wire budget");
        }

        writer.WriteBlock(value: bytes);
    }
    private static WorldStateSection? ReadRecords(ref WireReader reader) {
        var bytes = reader.ReadBlock(
            field: "identity records",
            maxBytes: WireLimits.MaxDocumentBytes
        );

        if (
            reader.Failed ||
            (bytes.Length == 0)
        ) {
            return null;
        }

        try {
            var records = (JsonSerializer.Deserialize(
                jsonTypeInfo: WorldJsonContext.Default.WorldStateSection,
                utf8Json: bytes
            ) ?? throw new InvalidOperationException(message: "identity record payload is null"));

            WorldIdentityRecords.Validate(section: records);

            return records;
        } catch (Exception exception) when ((exception is JsonException or InvalidOperationException or ArgumentException or FormatException or NotSupportedException)) {
            reader.Fail(
                detail: $"identity records refuse: {exception.Message}",
                refusal: WireRefusal.PayloadMalformed
            );

            return null;
        }
    }
    private static void WriteFacts(WireWriter writer, WorldStateRow? facts) {
        WorldIdentityFacts.Validate(row: facts);
        writer.WriteBoolean(value: (facts is not null));

        if (facts is null) {
            return;
        }

        var cells = (facts.Cells ?? []);

        writer.WriteString(value: facts.Name.Value);
        writer.WriteInt32(value: facts.Capacity!.Value);
        writer.WriteInt32(value: cells.Count);

        foreach (var cell in cells) {
            writer.WriteString(value: cell.Key.Value);
            writer.WriteInt64(value: cell.Value.AsInt);
        }
    }
    private static WorldStateRow? ReadFacts(ref WireReader reader) {
        if (!reader.ReadBoolean()) {
            return null;
        }

        var name = reader.ReadRequiredString(field: "identity facts row");
        var capacity = reader.ReadCount(
            field: "identity facts capacity",
            maximum: StateCapacity.MaxCellsPerRow,
            minimum: 1
        );
        var count = reader.ReadCount(
            field: "identity facts count",
            maximum: StateCapacity.MaxCellsPerRow,
            minimum: 0
        );
        var cells = new StateCell[(reader.Failed
            ? 0
            : count)];

        for (var index = 0; (index < cells.Length); index++) {
            var key = reader.ReadRequiredString(field: "identity fact key");
            var value = reader.ReadInt64();

            if (reader.Failed) {
                return null;
            }
            if (!CellName.TryParse(
                candidate: key,
                name: out var cellName,
                reason: out var keyReason
            )) {
                reader.Fail(
                    detail: $"identity fact key '{key}' refuses: {keyReason}",
                    refusal: WireRefusal.PayloadMalformed
                );

                return null;
            }

            cells[index] = new StateCell(
                Key: cellName,
                Value: CellValue.Int(value: value)
            );
        }

        if (reader.Failed) {
            return null;
        }
        if (!CellName.TryParse(
            candidate: name,
            name: out var rowName,
            reason: out var nameReason
        )) {
            reader.Fail(
                detail: $"identity facts row '{name}' refuses: {nameReason}",
                refusal: WireRefusal.PayloadMalformed
            );

            return null;
        }

        var row = new WorldStateRow(
            Name: rowName,
            Kind: CellKind.Int,
            Capacity: capacity,
            Cells: cells
        );

        try {
            WorldIdentityFacts.Validate(row: row);
        } catch (InvalidOperationException exception) {
            reader.Fail(
                detail: exception.Message,
                refusal: WireRefusal.PayloadMalformed
            );

            return null;
        }

        return row;
    }
}
