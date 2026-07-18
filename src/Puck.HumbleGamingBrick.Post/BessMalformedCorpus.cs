using System.Buffers.Binary;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// The M-08 malformed-BESS-file corpus: every way a well-formed export can be mutated that
/// <see cref="BessImporter.Import"/> must reject before touching any machine state. Shared between the always-run
/// <see cref="BessImportGuardStage"/> gate (our importer's safety contract; see that stage's doc comment for why this
/// belongs to Post rather than to evidence tooling) and the <c>--bess-export</c> self-check in
/// <see cref="BessDiagnostic"/>, so the case list is defined exactly once. One entry per failure class the review
/// called out: a truncated file, a CORE buffer-table entry pointing outside the file, an undersized CORE block, a
/// garbaged footer magic, a missing required <c>END </c> block, a nonzero-length or non-final <c>END </c> block, an
/// exact-size mismatch per region (palette, OAM, HRAM), one oversized-destination-capacity entry per region class
/// (work-RAM, video-RAM, palette), an optional <c>MBC </c> block whose length is not a multiple of 3, one whose
/// record writes an address outside <c>0x0000-0x7FFF</c>/<c>0xA000-0xBFFF</c> (M-08's remaining gap), an
/// incompatible <c>CORE</c> major version, a duplicate <c>CORE</c> block, and a known block (<c>MBC </c>) appearing
/// before the required <c>CORE</c> block (H-10/H-11).
/// <para>
/// This is deliberately NOT where the spec's legal-but-undersized work-RAM/video-RAM cases, or the legal-extended
/// <c>CORE</c> case, live — those are not malformed data (the BESS spec's own "handle size mismatches gracefully"
/// and "ignore any excess bytes" contracts require them to be ACCEPTED) — see <see cref="BuildGracefulShapeCases"/>
/// and <see cref="BuildExtendedCoreCase"/> instead.
/// </para>
/// </summary>
internal static class BessMalformedCorpus {
    /// <summary>Builds the corpus from a known-good BESS export.</summary>
    /// <param name="goodFile">A well-formed BESS file (typically a fresh <see cref="BessExporter.Export"/> output).</param>
    /// <returns>Each malformed case's label and bytes.</returns>
    public static IEnumerable<(string Label, byte[] Bytes)> Build(byte[] goodFile) {
        yield return ("truncated file", goodFile[..(goodFile.Length / 2)]);
        yield return ("out-of-bounds buffer offset", WithOutOfBoundsBufferOffset(goodFile: goodFile));
        yield return ("undersized CORE block", BuildUndersizedCoreFile());
        yield return ("garbage footer magic", WithGarbageFooterMagic(goodFile: goodFile));
        yield return ("missing END block", WithoutEndBlock(goodFile: goodFile));
        yield return ("nonzero-length END block", BuildNonzeroLengthEndFile());
        yield return ("block after END", BuildBlockAfterEndFile());
        yield return ("oversized work-RAM destination", WithOversizedBufferEntry(goodFile: goodFile, tableOffset: 0x00));
        yield return ("oversized video-RAM destination", WithOversizedBufferEntry(goodFile: goodFile, tableOffset: 0x08));
        yield return ("oversized palette destination", WithOversizedBufferEntry(goodFile: goodFile, tableOffset: 0x28));
        yield return ("undersized palette (not 0 or 0x40)", WithResizedBufferEntry(goodFile: goodFile, tableOffset: 0x28, size: 1));
        yield return ("undersized OAM (not exactly 0xA0)", WithResizedBufferEntry(goodFile: goodFile, tableOffset: 0x18, size: 1));
        yield return ("undersized HRAM (not exactly 0x7F)", WithResizedBufferEntry(goodFile: goodFile, tableOffset: 0x20, size: 1));
        yield return ("trailing-fragment MBC (length % 3 != 0)", WithTrailingFragmentMbcBlock(goodFile: goodFile));
        yield return ("out-of-domain MBC address (0xC000)", WithOutOfDomainMbcAddress(goodFile: goodFile));
        yield return ("incompatible CORE major version (2)", WithIncompatibleMajorVersion(goodFile: goodFile));
        yield return ("duplicate CORE block", WithDuplicateCoreBlock(goodFile: goodFile));
        yield return ("known block (MBC) before CORE", WithMbcBlockBeforeCore(goodFile: goodFile));
    }
    /// <summary>One legal-but-undersized CORE buffer-table case (M-08): the BESS spec's own "handle size mismatches
    /// gracefully" contract for work-RAM/video-RAM, which the spec gives no fixed size. A short entry here is valid
    /// input, not malformed data — <see cref="BessImporter.Import"/> must succeed and zero-fill the remainder rather
    /// than throw or leave the destination's prior contents in place.</summary>
    /// <param name="Label">A human-readable case name.</param>
    /// <param name="File">The BESS file with one buffer-table entry shrunk below its destination's full capacity.</param>
    /// <param name="DestinationStart">The bus address the region starts at.</param>
    /// <param name="DestinationCapacity">The region's full byte count.</param>
    /// <param name="ImportedBytes">The exact bytes the shrunk entry declares — expected at
    /// <c>[DestinationStart, DestinationStart + ImportedBytes.Length)</c> after import; every remaining byte up to
    /// <see cref="DestinationCapacity"/> is expected to read back as 0.</param>
    public readonly record struct GracefulShapeCase(string Label, byte[] File, ushort DestinationStart, int DestinationCapacity, byte[] ImportedBytes);
    /// <summary>Builds the legal-undersized work-RAM/video-RAM cases from a known-good BESS export.</summary>
    /// <param name="goodFile">A well-formed BESS file (typically a fresh <see cref="BessExporter.Export"/> output).</param>
    /// <returns>Each case's file and the destination-region assertion it expects.</returns>
    public static IEnumerable<GracefulShapeCase> BuildGracefulShapeCases(byte[] goodFile) {
        yield return BuildUndersizedRangeCase(goodFile: goodFile, tableOffset: 0x00, label: "undersized work-RAM", destinationStart: MemoryMap.WorkRamBank0Start, destinationCapacity: ((MemoryMap.WorkRamBankNEnd - MemoryMap.WorkRamBank0Start) + 1), declaredSize: 0x100);
        yield return BuildUndersizedRangeCase(goodFile: goodFile, tableOffset: 0x08, label: "undersized video-RAM", destinationStart: MemoryMap.VideoRamStart, destinationCapacity: ((MemoryMap.VideoRamEnd - MemoryMap.VideoRamStart) + 1), declaredSize: 0x100);
    }
    /// <summary>Builds the legal-extended <c>CORE</c> case (M-10): the BESS spec's own "implementations are expected
    /// to ignore any excess bytes" contract for the CORE block's defined 0xD0-byte prefix. This is legal input, not
    /// malformed data — <see cref="BessImporter.Import"/> must succeed, and must produce the SAME BESS-modeled state
    /// (the same <see cref="BessImportReport"/>, and the same resulting machine snapshot) as importing
    /// <paramref name="goodFile"/> unextended — the forward-compat proof the caller is expected to assert, since this
    /// case's equivalence check does not fit <see cref="GracefulShapeCase"/>'s single-destination-region shape.</summary>
    /// <param name="goodFile">A well-formed BESS file (typically a fresh <see cref="BessExporter.Export"/> output).</param>
    /// <returns>The file with its CORE block's payload padded with extra, nonzero tail bytes beyond the defined
    /// prefix — nonzero so a correct import (which must never read them) is distinguishable from one that happens to
    /// read zeros past the prefix by coincidence.</returns>
    public static byte[] BuildExtendedCoreCase(byte[] goodFile) {
        var coreOffset = FindBlockOffset(file: goodFile, tag: "CORE", caseName: "extended-core");
        var corePayload = ExtractBlockPayload(file: goodFile, tag: "CORE", caseName: "extended-core");
        var afterCorePayloadOffset = (coreOffset + 8 + corePayload.Length);
        var extendedPayload = new byte[(corePayload.Length + 16)];

        corePayload.AsSpan().CopyTo(destination: extendedPayload);
        Array.Fill(array: extendedPayload, value: (byte)0x5A, startIndex: corePayload.Length, count: 16);

        var spliced = new List<byte>(capacity: (goodFile.Length + 16));

        spliced.AddRange(collection: goodFile.AsSpan(start: 0, length: coreOffset).ToArray());
        Bess.WriteBlock(destination: spliced, tag: "CORE", payload: extendedPayload);
        spliced.AddRange(collection: goodFile.AsSpan(start: afterCorePayloadOffset).ToArray());

        return spliced.ToArray();
    }
    // Clones the good export and rewrites the CORE block's work-RAM buffer-table entry (Bess.BufferTableOffset+0x04,
    // the file-offset half of the size/offset pair) to point past the end of the file — everything else about the file
    // stays well-formed, so this exercises the source-bounds half of ValidateBufferTable specifically.
    private static byte[] WithOutOfBoundsBufferOffset(byte[] goodFile) {
        var malformed = (byte[])goodFile.Clone();
        var coreDataOffset = FindCoreBlockDataOffset(file: malformed, caseName: "out-of-bounds-offset");
        var fileOffsetField = (coreDataOffset + Bess.BufferTableOffset + 0x04);

        BinaryPrimitives.WriteUInt32LittleEndian(destination: malformed.AsSpan(start: fileOffsetField), value: (uint)(malformed.Length + 4_096));

        return malformed;
    }
    // A structurally well-formed BESS file (real footer, real block headers) whose CORE payload is far shorter than
    // Bess.CoreBlockLength — exercises the explicit CORE-length check rather than the block-graph bounds check.
    private static byte[] BuildUndersizedCoreFile() {
        var file = new List<byte>(capacity: 64);
        var firstBlockOffset = file.Count;

        Bess.WriteBlock(destination: file, tag: "CORE", payload: new byte[16]);
        Bess.WriteBlock(destination: file, tag: "END ", payload: []);
        Bess.WriteFooter(destination: file, firstBlockOffset: (uint)firstBlockOffset);

        return file.ToArray();
    }
    // Flips a byte of the trailing "BESS" tag so TryReadFooter's magic check fails outright.
    private static byte[] WithGarbageFooterMagic(byte[] goodFile) {
        var malformed = (byte[])goodFile.Clone();

        malformed[^4] ^= 0xFF;

        return malformed;
    }
    // Corrupts the END block's own tag (its first byte) so the block-graph walk never observes a literal "END " tag.
    // The block itself stays structurally valid — a zero-length payload at a well-formed offset — and is simply
    // treated as an unsupported, ignored block per spec, so parsing completes normally and only the "was END seen"
    // flag is false: exercises the required-END check specifically, not the block-graph bounds check.
    private static byte[] WithoutEndBlock(byte[] goodFile) {
        var malformed = (byte[])goodFile.Clone();

        if (!Bess.TryReadFooter(file: malformed, firstBlockOffset: out var cursor)) {
            throw new InvalidOperationException(message: "the good export has no BESS footer; cannot build the missing-END corpus case.");
        }

        var end = (malformed.Length - Bess.FooterLength);

        while (cursor < end) {
            if (!Bess.TryReadBlock(file: malformed, offset: cursor, end: end, tag: out var tag, payload: out _, next: out var next)) {
                throw new InvalidOperationException(message: "the good export's block graph is malformed; cannot build the missing-END corpus case.");
            }

            if (tag == "END ") {
                malformed[cursor] ^= 0xFF;

                return malformed;
            }

            cursor = next;
        }

        throw new InvalidOperationException(message: "the good export has no END block; cannot build the missing-END corpus case.");
    }
    // A structurally well-formed BESS file whose END block declares a nonzero payload length (with real bytes behind
    // it, so the block-graph walk reads it cleanly rather than tripping the truncation check) — exercises the
    // END-payload-length requirement specifically ("The length of the END block must be 0").
    private static byte[] BuildNonzeroLengthEndFile() {
        var file = new List<byte>(capacity: 64);
        var firstBlockOffset = file.Count;

        Bess.WriteBlock(destination: file, tag: "CORE", payload: new byte[16]);
        Bess.WriteBlock(destination: file, tag: "END ", payload: new byte[4]);
        Bess.WriteFooter(destination: file, firstBlockOffset: (uint)firstBlockOffset);

        return file.ToArray();
    }
    // A structurally well-formed BESS file with a valid, zero-length END block followed by one more block before the
    // footer — exercises "Naturally, it must be the last block" specifically, independent of the END payload-length
    // check above.
    private static byte[] BuildBlockAfterEndFile() {
        var file = new List<byte>(capacity: 96);
        var firstBlockOffset = file.Count;

        Bess.WriteBlock(destination: file, tag: "CORE", payload: new byte[16]);
        Bess.WriteBlock(destination: file, tag: "END ", payload: []);
        Bess.WriteBlock(destination: file, tag: "NAME", payload: "x"u8.ToArray());
        Bess.WriteFooter(destination: file, firstBlockOffset: (uint)firstBlockOffset);

        return file.ToArray();
    }
    // Clones the good export and rewrites one CORE buffer-table entry (identified by tableOffset) to declare the
    // WHOLE region between the file's start and its footer as its payload: offset 0, size = everything up to the
    // footer. That span always fits inside the file (the source-bounds check stays satisfied), yet is far larger
    // than any destination region's real capacity (work-RAM/video-RAM 0x2000, palette 0x40 or less) — exercises the
    // destination-capacity half of ValidateBufferTable specifically, independent of file layout or cartridge RAM size.
    private static byte[] WithOversizedBufferEntry(byte[] goodFile, int tableOffset) {
        var malformed = (byte[])goodFile.Clone();
        var coreDataOffset = FindCoreBlockDataOffset(file: malformed, caseName: "oversized-destination");
        var absolute = (coreDataOffset + Bess.BufferTableOffset + tableOffset);
        var declaredSize = (uint)(malformed.Length - Bess.FooterLength);

        BinaryPrimitives.WriteUInt32LittleEndian(destination: malformed.AsSpan(start: (absolute + 4)), value: 0); // offset = 0.
        BinaryPrimitives.WriteUInt32LittleEndian(destination: malformed.AsSpan(start: absolute), value: declaredSize);

        return malformed;
    }
    // Clones the good export and shrinks one CORE buffer-table entry's declared SIZE only, leaving its file offset
    // pointing at the same real exported data — the source-bounds check stays satisfied (a smaller span only ever
    // fits more easily), so this isolates the exact-size-shape checks (BufferSizeShape.Exact/ExactOrZero) rather than
    // any bounds check. Used for palette/OAM/HRAM, whose spec rows carry a fixed size with no legal shorter form.
    private static byte[] WithResizedBufferEntry(byte[] goodFile, int tableOffset, uint size) {
        var malformed = (byte[])goodFile.Clone();
        var coreDataOffset = FindCoreBlockDataOffset(file: malformed, caseName: "undersized-buffer");
        var absolute = (coreDataOffset + Bess.BufferTableOffset + tableOffset);

        BinaryPrimitives.WriteUInt32LittleEndian(destination: malformed.AsSpan(start: absolute), value: size);

        return malformed;
    }
    // Builds one BuildGracefulShapeCases entry: shrinks a Range-shaped region's (work-RAM/video-RAM) buffer-table
    // entry to declaredSize, capturing the real exported bytes that span still references BEFORE the mutation — the
    // exact bytes BessImporter.Import is expected to write at the destination's start, with the caller then expected
    // to find zeros for every byte beyond declaredSize up to destinationCapacity.
    private static GracefulShapeCase BuildUndersizedRangeCase(byte[] goodFile, int tableOffset, string label, ushort destinationStart, int destinationCapacity, int declaredSize) {
        var malformed = (byte[])goodFile.Clone();
        var coreDataOffset = FindCoreBlockDataOffset(file: malformed, caseName: label);
        var absolute = (coreDataOffset + Bess.BufferTableOffset + tableOffset);
        var fileOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(source: malformed.AsSpan(start: (absolute + 4)));
        var importedBytes = malformed.AsSpan(start: fileOffset, length: declaredSize).ToArray();

        BinaryPrimitives.WriteUInt32LittleEndian(destination: malformed.AsSpan(start: absolute), value: (uint)declaredSize);

        return new GracefulShapeCase(Label: label, File: malformed, DestinationStart: destinationStart, DestinationCapacity: destinationCapacity, ImportedBytes: importedBytes);
    }
    // Walks the block graph to find the CORE block's payload's absolute file offset — the shared lookup the
    // out-of-bounds-offset and oversized-destination cases both need to locate the buffer table they mutate.
    private static int FindCoreBlockDataOffset(byte[] file, string caseName) =>
        (FindBlockOffset(file: file, tag: "CORE", caseName: caseName) + 8);
    // Walks the block graph to find a block's own absolute file offset (its tag's position) by tag — the shared
    // lookup FindCoreBlockDataOffset and the MBC-block-insertion cases below need to locate a block to read from or
    // splice next to.
    private static int FindBlockOffset(byte[] file, string tag, string caseName) {
        if (!Bess.TryReadFooter(file: file, firstBlockOffset: out var cursor)) {
            throw new InvalidOperationException(message: $"the good export has no BESS footer; cannot build the {caseName} corpus case.");
        }

        var end = (file.Length - Bess.FooterLength);

        while (cursor < end) {
            if (!Bess.TryReadBlock(file: file, offset: cursor, end: end, tag: out var candidate, payload: out _, next: out var next)) {
                throw new InvalidOperationException(message: $"the good export's block graph is malformed; cannot build the {caseName} corpus case.");
            }

            if (candidate == tag) {
                return cursor;
            }

            cursor = next;
        }

        throw new InvalidOperationException(message: $"the good export has no {tag} block; cannot build the {caseName} corpus case.");
    }
    // Reads one block's payload bytes by tag — the shared lookup the duplicate-CORE and extended-CORE cases need to
    // recover the good export's real CORE payload before splicing a mutated copy elsewhere in the file.
    private static byte[] ExtractBlockPayload(byte[] file, string tag, string caseName) {
        var offset = FindBlockOffset(file: file, tag: tag, caseName: caseName);
        var end = (file.Length - Bess.FooterLength);

        if (!Bess.TryReadBlock(file: file, offset: offset, end: end, tag: out _, payload: out var payload, next: out _)) {
            throw new InvalidOperationException(message: $"the good export's {tag} block is malformed; cannot build the {caseName} corpus case.");
        }

        return payload.ToArray();
    }
    // Splices a fabricated block of the given tag/payload immediately before the first block matching
    // insertBeforeTag — block order beyond NAME/INFO-before-CORE and END-must-be-last is unconstrained by spec, so
    // inserting one here exercises order-sensitive validation (an MBC block is legal before END, but not before the
    // required CORE block; a second CORE block is fatal wherever it appears) against an otherwise valid export.
    private static byte[] SpliceBlockBefore(byte[] goodFile, string insertBeforeTag, string tag, byte[] payload, string caseName) {
        var insertOffset = FindBlockOffset(file: goodFile, tag: insertBeforeTag, caseName: caseName);
        var spliced = new List<byte>(capacity: (goodFile.Length + payload.Length + 8));

        spliced.AddRange(collection: goodFile.AsSpan(start: 0, length: insertOffset).ToArray());
        Bess.WriteBlock(destination: spliced, tag: tag, payload: payload);
        spliced.AddRange(collection: goodFile.AsSpan(start: insertOffset).ToArray());

        return spliced.ToArray();
    }
    // Splices a fabricated "MBC " block immediately before the good export's END block — a legal position (MBC is
    // unconstrained relative to END beyond "not after it") regardless of whether the exporting cartridge itself
    // carried a mapper (the shared corpus's SyntheticRom source is ROM-only and never emits one natively) —
    // exercises BessImporter.ValidateMbcBlock against an otherwise valid export.
    private static byte[] WithMbcBlock(byte[] goodFile, byte[] payload) =>
        SpliceBlockBefore(goodFile: goodFile, insertBeforeTag: "END ", tag: "MBC ", payload: payload, caseName: "mbc-block");
    // (H-10) Flips the good export's CORE major version field (offset 0x00, a little-endian 16-bit integer) to 2 —
    // the spec's "Both major and minor versions should be 1. Implementations are expected to reject incompatible
    // majors" — everything else about the file, including the minor version, stays untouched.
    private static byte[] WithIncompatibleMajorVersion(byte[] goodFile) {
        var malformed = (byte[])goodFile.Clone();
        var coreDataOffset = FindCoreBlockDataOffset(file: malformed, caseName: "incompatible-major");

        BinaryPrimitives.WriteUInt16LittleEndian(destination: malformed.AsSpan(start: coreDataOffset), value: 2);

        return malformed;
    }
    // (H-11) Splices a second, byte-identical CORE block immediately before the good export's END block — the
    // spec's Validation-and-Failures "Duplicate CORE block" fatal condition — independent of where in the graph the
    // duplicate lands, since BessImporter rejects the SECOND "CORE" tag it observes regardless of position.
    private static byte[] WithDuplicateCoreBlock(byte[] goodFile) {
        var corePayload = ExtractBlockPayload(file: goodFile, tag: "CORE", caseName: "duplicate-core");

        return SpliceBlockBefore(goodFile: goodFile, insertBeforeTag: "END ", tag: "CORE", payload: corePayload, caseName: "duplicate-core");
    }
    // (H-11) Splices a fabricated "MBC " block immediately before the good export's CORE block (i.e. after
    // NAME/INFO, ahead of the block that must otherwise come first) — the spec's Validation-and-Failures "A known
    // block, other than NAME, appearing before CORE" fatal condition. The payload is one legal MBC record (RAM
    // enable) so this case isolates the block-order check from ValidateMbcBlock's own shape checks, which never run
    // here since the order check rejects the block first.
    private static byte[] WithMbcBlockBeforeCore(byte[] goodFile) =>
        SpliceBlockBefore(goodFile: goodFile, insertBeforeTag: "CORE", tag: "MBC ", payload: [0x00, 0x00, 0x0A], caseName: "mbc-before-core");
    // One legal 3-byte MBC record (RAM-enable write to 0x0000) followed by a single stray byte: a 4-byte payload,
    // not divisible by 3 — the spec's own "length ... must be divisible by 3" and a SameBoy fatal condition
    // ("An invalid length of MBC (not a multiple of 3)"). Isolates the divisibility check from the address-domain
    // check below (the well-formed record it leads with is in-range).
    private static byte[] WithTrailingFragmentMbcBlock(byte[] goodFile) =>
        WithMbcBlock(goodFile: goodFile, payload: [0x00, 0x00, 0x0A, 0xFF]);
    // One 3-byte MBC record at the review's own counterexample address, 0xC000 — inside imported work RAM rather
    // than either permitted MBC window (0x0000-0x7FFF, 0xA000-0xBFFF) — the spec's "Values outside the
    // 0x0000-0x7FFF and 0xA000-0xBFFF ranges are not allowed" and a SameBoy fatal condition. Length-legal (3 bytes),
    // isolating the address-domain check from the divisibility check above.
    private static byte[] WithOutOfDomainMbcAddress(byte[] goodFile) =>
        WithMbcBlock(goodFile: goodFile, payload: [0x00, 0xC0, 0x5A]);
}
