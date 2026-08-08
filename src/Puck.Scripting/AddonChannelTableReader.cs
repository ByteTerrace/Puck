using System.Buffers.Binary;

namespace Puck.Scripting;

/// <summary>The STRUCTURAL channel descriptor table decoder: validates and decodes exactly <c>count</c>
/// contiguous 16-byte descriptors, checking only what the wire shape itself pins — the <c>Kind</c> discriminant,
/// reserved-must-be-zero fields, duplicate kinds, and the per-kind count/pointer shape. It touches no guest
/// memory beyond the table itself: an <c>Input</c> channel's declared channel-name table is decoded separately
/// (by <see cref="AddonChannelNameTableReader"/> against the caller's <see cref="IAddonChannelResolver"/>), and
/// lane binding is a mount-time concern this reader does not own.</summary>
public static class AddonChannelTableReader {
    /// <summary>Validates and decodes <paramref name="count"/> channel descriptors from <paramref name="source"/>.</summary>
    /// <param name="source">The packed descriptor table bytes — exactly <paramref name="count"/> × <see cref="AddonAbi.ChannelDescriptorBytes"/> bytes.</param>
    /// <param name="count">The channel count the guest declared.</param>
    /// <param name="destination">The caller-owned buffer decoded descriptors are written into.</param>
    /// <param name="error">When this returns <see langword="false"/>, the specific rejection reason, naming the offending descriptor index; otherwise empty.</param>
    /// <returns><see langword="true"/> if every descriptor decoded cleanly and the table's pairing rules hold; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    public static bool TryDecode(ReadOnlySpan<byte> source, int count, AddonChannelDescriptor[] destination, out string error) {
        ArgumentNullException.ThrowIfNull(destination);

        error = "";

        if ((count < 1) || (count > AddonAbi.MaxChannels) || (count > destination.Length) || ((count * AddonAbi.ChannelDescriptorBytes) > source.Length)) {
            error = $"channel count {count} out of range [1, {AddonAbi.MaxChannels}] or table truncated";
            return false;
        }

        var seenKinds = new HashSet<AddonChannelKind>();
        var inputDeclared = false;
        var requestDeclared = false;
        var responseDeclared = false;

        for (var index = 0; (index < count); ++index) {
            var entry = source.Slice(start: (index * AddonAbi.ChannelDescriptorBytes), length: AddonAbi.ChannelDescriptorBytes);
            var kindByte = entry[AddonAbi.ChannelDescriptorOffsets.Kind];
            var kind = (AddonChannelKind)kindByte;

            if (!Enum.IsDefined(value: kind)) {
                error = $"descriptor {index}: channel kind {kindByte} is not defined";
                return false;
            }

            if (entry[AddonAbi.ChannelDescriptorOffsets.Reserved0] != 0) {
                error = $"descriptor {index}: reserved byte must be zero";
                return false;
            }

            if (BinaryPrimitives.ReadUInt64LittleEndian(source: entry[AddonAbi.ChannelDescriptorOffsets.Reserved1..]) != 0UL) {
                error = $"descriptor {index}: reserved field must be zero";
                return false;
            }

            if (!seenKinds.Add(item: kind)) {
                error = $"descriptor {index}: duplicate channel kind {kind}";
                return false;
            }

            var verbCount = BinaryPrimitives.ReadUInt16LittleEndian(source: entry[AddonAbi.ChannelDescriptorOffsets.VerbCount..]);
            var verbTablePtr = BinaryPrimitives.ReadUInt32LittleEndian(source: entry[AddonAbi.ChannelDescriptorOffsets.VerbTablePtr..]);

            switch (kind) {
                case AddonChannelKind.Input:
                    if ((verbCount < 1) || (verbCount > AddonAbi.MaxChannelNames)) {
                        error = $"descriptor {index}: input VerbCount {verbCount} out of range [1, {AddonAbi.MaxChannelNames}]";
                        return false;
                    }

                    inputDeclared = true;
                    break;

                case AddonChannelKind.Request:
                    // A guest may speak a PREFIX of the pinned request vocabulary — growing the vocabulary later
                    // must not refuse a guest built against fewer verbs, or "growing verbs is data, not a break"
                    // would be false. The decode-side range check runs against the DECLARED count.
                    if ((verbTablePtr != 0) || (verbCount < 1) || (verbCount > AddonAbi.RequestVerbs.Count)) {
                        error = $"descriptor {index}: request channel must declare VerbTablePtr = 0 and VerbCount in [1, {AddonAbi.RequestVerbs.Count}]";
                        return false;
                    }

                    requestDeclared = true;
                    break;

                case AddonChannelKind.Response:
                    if ((verbCount != 0) || (verbTablePtr != 0)) {
                        error = $"descriptor {index}: response channel must declare VerbCount = 0 and VerbTablePtr = 0";
                        return false;
                    }

                    responseDeclared = true;
                    break;
            }

            destination[index] = new AddonChannelDescriptor(
                Kind: kind,
                VerbCount: verbCount,
                VerbTablePtr: verbTablePtr
            );
        }

        if (requestDeclared != responseDeclared) {
            error = "the request and response channels must be declared together — the pair is one facility";
            return false;
        }

        // Grant disclosures ride the response channel; an Input channel with no request/response pair could
        // never learn a handle to drive through and would be provably inert.
        if (inputDeclared && !requestDeclared) {
            error = "an input channel requires the request/response pair — otherwise no handle can ever reach it";
            return false;
        }

        return true;
    }
}
