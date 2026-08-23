using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Encodes and decodes a full <see cref="WorldAuthorityCheckpoint"/> over the same bounded
/// <see cref="WireWriter"/>/<see cref="WireReader"/> discipline every peer decoder in this engine follows: a
/// <c>"PCKP"</c> magic, a fail-closed <c>u16</c> version (refuses any value other than 1 by name — a checkpoint
/// carries no compat path), a <c>sha256-64</c> content pin of the whole framed body, then that body — itself a
/// <c>sha256-64</c> pin of the captured definition JSON followed by the checkpoint's eight sections in the record's
/// own declared order, each its own length-prefixed block. Journal entries and a buffered
/// <see cref="WorldPendingOpCheckpoint.Mutate"/> op reuse <see cref="WorldSubmissionCodec"/>'s own mutation leaf
/// verbatim; every embedded document (the definition, the base definition, an escrow lease's destination definition)
/// reuses <see cref="WorldDefinitionSerialization.Serialize"/> bytes verbatim — this codec never re-serializes a
/// document itself. Every read is bounded; every decoder — the outer envelope, the body, and each of the eight
/// sections — asks its own <see cref="WireReader.TryFinish"/> exactly once, so a truncated or trailing-byte payload
/// refuses by name at the scope that actually owns the leftover bytes.</summary>
public static partial class WorldAuthorityCheckpointCodec {
    // "PCKP" — Puck Checkpoint.
    private const uint Magic = 0x504B4350U;
    private const int MaxCollectionCount = 1_000_000;
    private const int MaxHashChars = 128;
    private const int MaxSectionBytes = ((64 * 1024) * 1024);
    private const int MaxStringBytes = WireLimits.MaxStringBytes;
    private const ushort SupportedVersion = 2;

    private delegate T ReadItem<T>(ref WireReader reader);
    private delegate T ReadStructItem<T>(ref WireReader reader) where T : struct;
    private delegate T ReadClassItem<T>(ref WireReader reader) where T : class;

    /// <summary>Encodes a full checkpoint.</summary>
    /// <param name="checkpoint">The checkpoint to encode.</param>
    /// <returns>The encoded blob.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="checkpoint"/> is <see langword="null"/>.</exception>
    public static byte[] Encode(WorldAuthorityCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);

        var body = new WireWriter();

        body.WriteString(value: WorldDefinitionFileSource.ComputeContentHash(content: checkpoint.Server.DefinitionJson));
        body.WriteBlock(value: EncodeServer(section: checkpoint.Server));
        body.WriteBlock(value: EncodePopulation(section: checkpoint.Population));
        body.WriteBlock(value: EncodeGrants(section: checkpoint.Grants));
        body.WriteBlock(value: EncodeEscrow(section: checkpoint.Escrow));
        body.WriteBlock(value: EncodeInputHold(section: checkpoint.InputHold));
        body.WriteBlock(value: EncodeEventFeed(section: checkpoint.EventFeed));
        body.WriteBlock(value: EncodeOwnedWorlds(section: checkpoint.OwnedWorlds));
        body.WriteBlock(value: EncodeHostRow(section: checkpoint.HostRow));
        body.WriteBlock(value: EncodeFields(section: checkpoint.Fields));

        var bodyBytes = body.ToArray();
        var writer = new WireWriter();

        writer.WriteUInt32(value: Magic);
        WriteUInt16(
            value: SupportedVersion,
            writer: writer
        );
        // A whole-body content pin, over everything the envelope frames — the per-section decoders below also pin
        // the definition specifically (by its own sha256-64), but a corruption landing outside the definition bytes
        // (a section's own field, a length prefix, a discriminant) has no other structural reason to be caught, so
        // this is what makes a single-bit flip anywhere in the payload refuse by name rather than silently decoding
        // a different value.
        writer.WriteString(value: WorldDefinitionFileSource.ComputeContentHash(content: bodyBytes));
        writer.WriteBlock(value: bodyBytes);

        return writer.ToArray();
    }
    /// <summary>Decodes a full checkpoint.</summary>
    /// <param name="bytes">The encoded blob.</param>
    /// <param name="checkpoint">The decoded checkpoint on success.</param>
    /// <param name="reason">The one-line refusal reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the blob decoded exactly.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out WorldAuthorityCheckpoint? checkpoint, out string reason) {
        checkpoint = null;

        var reader = new WireReader(bytes: bytes);
        var magic = reader.ReadUInt32();

        if (
            !reader.Failed &&
            (magic != Magic)
        ) {
            reader.Fail(
                detail: $"checkpoint magic {magic:x8} is not the checkpoint magic",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        var version = ReadUInt16(reader: ref reader);

        if (
            !reader.Failed &&
            (version != SupportedVersion)
        ) {
            reader.Fail(
                detail: $"checkpoint version {version} is not the supported version {SupportedVersion}",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        var bodyHash = reader.ReadRequiredString(
            field: "body hash",
            maxBytes: MaxHashChars
        );
        var bodyBytes = reader.ReadBlock(
            field: "checkpoint body",
            maxBytes: MaxSectionBytes
        );

        if (!reader.TryFinish(failure: out var outerFailure)) {
            reason = $"checkpoint envelope: {outerFailure}";

            return false;
        }

        if (!string.Equals(
            a: WorldDefinitionFileSource.ComputeContentHash(content: bodyBytes),
            b: bodyHash,
            comparisonType: StringComparison.Ordinal
        )) {
            reason = "checkpoint body does not match its own content-address pin";

            return false;
        }

        var body = new WireReader(bytes: bodyBytes);
        var definitionHash = body.ReadRequiredString(
            field: "definition hash",
            maxBytes: MaxHashChars
        );
        var serverBytes = body.ReadBlock(
            field: "server section",
            maxBytes: MaxSectionBytes
        );
        var populationBytes = body.ReadBlock(
            field: "population section",
            maxBytes: MaxSectionBytes
        );
        var grantsBytes = body.ReadBlock(
            field: "grants section",
            maxBytes: MaxSectionBytes
        );
        var escrowBytes = body.ReadBlock(
            field: "escrow section",
            maxBytes: MaxSectionBytes
        );
        var inputHoldBytes = body.ReadBlock(
            field: "input hold section",
            maxBytes: MaxSectionBytes
        );
        var eventFeedBytes = body.ReadBlock(
            field: "event feed section",
            maxBytes: MaxSectionBytes
        );
        var ownedWorldsBytes = body.ReadBlock(
            field: "owned worlds section",
            maxBytes: MaxSectionBytes
        );
        var hostRowBytes = body.ReadBlock(
            field: "host row section",
            maxBytes: MaxSectionBytes
        );
        var fieldsBytes = body.ReadBlock(
            field: "fields section",
            maxBytes: MaxSectionBytes
        );

        if (!body.TryFinish(failure: out var bodyFailure)) {
            reason = $"checkpoint body: {bodyFailure}";

            return false;
        }

        if (!TryDecodeServer(
            bytes: serverBytes,
            definitionHash: definitionHash,
            reason: out reason,
            section: out var server
        )) {
            return false;
        }

        WorldDefinition definition;

        try {
            definition = WorldDefinitionSerialization.Deserialize(utf8Json: server.DefinitionJson);
        } catch (Exception exception) when ((exception is ArgumentException or InvalidDataException or NotSupportedException)) {
            reason = $"server section: definition failed to parse — {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        var defaults = definition.PlayerDefaults;

        if (!TryDecodePopulation(
            bytes: populationBytes,
            reason: out reason,
            section: out var population
        )) {
            return false;
        }
        if (!TryDecodeGrants(
            bytes: grantsBytes,
            reason: out reason,
            section: out var grants
        )) {
            return false;
        }
        if (!TryDecodeEscrow(
            bytes: escrowBytes,
            defaults: defaults,
            reason: out reason,
            section: out var escrow
        )) {
            return false;
        }
        if (!TryDecodeInputHold(
            bytes: inputHoldBytes,
            reason: out reason,
            section: out var inputHold
        )) {
            return false;
        }
        if (!TryDecodeEventFeed(
            bytes: eventFeedBytes,
            reason: out reason,
            section: out var eventFeed
        )) {
            return false;
        }
        if (!TryDecodeOwnedWorlds(
            bytes: ownedWorldsBytes,
            reason: out reason,
            section: out var ownedWorlds
        )) {
            return false;
        }
        if (!TryDecodeHostRow(
            bytes: hostRowBytes,
            defaults: defaults,
            reason: out reason,
            section: out var hostRow
        )) {
            return false;
        }

        if (!TryDecodeFields(
            bytes: fieldsBytes,
            reason: out reason,
            section: out var fields
        )) {
            return false;
        }

        checkpoint = new WorldAuthorityCheckpoint(
            Escrow: escrow,
            EventFeed: eventFeed,
            Grants: grants,
            HostRow: hostRow,
            InputHold: inputHold,
            OwnedWorlds: ownedWorlds,
            Population: population,
            Server: server,
            Fields: fields
        );
        reason = string.Empty;

        return true;
    }

    // ---- shared primitive helpers ----


    private static byte[] EncodeLeafBlock<T>(T value, string what, TryEncodeLeaf<T> tryEncode) {
        if (!tryEncode(value, out var bytes, out var failure)) {
            throw new InvalidOperationException(message: $"{what} failed to encode — {failure}");
        }

        return bytes;
    }
    private static T? ReadLeafBlock<T>(ref WireReader reader, string field, TryDecodeLeaf<T> tryDecode) where T : class {
        var bytes = reader.ReadBlock(
            field: field,
            maxBytes: MaxSectionBytes
        );

        if (reader.Failed) {
            return null;
        }
        if (!tryDecode(
            bytes,
            out var value,
            out var failure
        )) {
            reader.Fail(
                detail: $"{field}: {failure}",
                refusal: WireRefusal.PayloadMalformed
            );

            return null;
        }

        return value;
    }
    // ---- server section leaves ----

}
