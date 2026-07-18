namespace Puck.Platform.Windows;

/// <summary>Parses only explicit VRR declarations from a monitor's effective EDID.</summary>
internal static class EdidVariableRefreshParser {
    private const int EdidBlockSize = 128;
    private const byte CtaExtensionTag = 0x02;
    private const byte DisplayIdExtensionTag = 0x70;
    private const byte DisplayIdAdaptiveSyncTag = 0x2B;
    private const byte DisplayIdCtaDataBlockCollectionTag = 0x81;
    private const byte CtaVendorSpecificTag = 0x03;

    /// <summary>Extracts positively identified VRR capabilities; generic monitor range limits are intentionally ignored.</summary>
    /// <param name="edid">The effective base EDID plus its declared extension blocks.</param>
    /// <param name="activeSignalHertz">The current physical signal rate used to select an applicable DisplayID mode.</param>
    /// <returns>Supported capabilities, or unknown when no trustworthy declaration is present.</returns>
    public static VariableRefreshCapabilities Parse(ReadOnlySpan<byte> edid, double? activeSignalHertz = null) {
        if (
            (edid.Length < EdidBlockSize) ||
            !HasEdidHeader(block: edid[..EdidBlockSize]) ||
            !HasValidChecksum(block: edid[..EdidBlockSize])
        ) {
            return VariableRefreshCapabilities.Unknown;
        }

        var extensionCount = edid[126];
        var requiredLength = checked((extensionCount + 1) * EdidBlockSize);

        if (edid.Length < requiredLength) {
            return VariableRefreshCapabilities.Unknown;
        }

        var accumulator = default(RangeAccumulator);

        for (var extensionIndex = 0; extensionIndex < extensionCount; ++extensionIndex) {
            var offset = ((extensionIndex + 1) * EdidBlockSize);
            var extension = edid.Slice(start: offset, length: EdidBlockSize);

            if (!HasValidChecksum(block: extension)) {
                // An unreadable declared block could contain a narrower or contradictory capability declaration.
                return VariableRefreshCapabilities.Unknown;
            }

            var extensionAccumulator = default(RangeAccumulator);
            var structurallyValid = extension[0] switch {
                CtaExtensionTag => ParseCtaExtension(extension: extension, accumulator: ref extensionAccumulator),
                DisplayIdExtensionTag => ParseDisplayIdExtension(extension: extension, activeSignalHertz: activeSignalHertz, accumulator: ref extensionAccumulator),
                _ => true,
            };

            if (!structurallyValid) {
                return VariableRefreshCapabilities.Unknown;
            }

            accumulator.Merge(other: in extensionAccumulator);
        }

        return accumulator.CreateCapabilities();
    }

    private static bool HasEdidHeader(ReadOnlySpan<byte> block) =>
        (block[0] == 0x00) &&
        (block[1] == 0xFF) &&
        (block[2] == 0xFF) &&
        (block[3] == 0xFF) &&
        (block[4] == 0xFF) &&
        (block[5] == 0xFF) &&
        (block[6] == 0xFF) &&
        (block[7] == 0x00);

    private static bool HasValidChecksum(ReadOnlySpan<byte> block) {
        if (block.Length != EdidBlockSize) {
            return false;
        }

        var sum = 0;

        foreach (var value in block) {
            sum += value;
        }

        return ((sum & 0xFF) == 0);
    }

    private static bool ParseCtaExtension(ReadOnlySpan<byte> extension, ref RangeAccumulator accumulator) {
        var dataBlockEnd = extension[2];

        if (dataBlockEnd == 0) {
            return true;
        }

        if ((dataBlockEnd < 4) || (dataBlockEnd > 127)) {
            return false;
        }

        return ParseCtaDataBlocks(dataBlocks: extension[4..dataBlockEnd], accumulator: ref accumulator);
    }

    private static bool ParseCtaDataBlocks(ReadOnlySpan<byte> dataBlocks, ref RangeAccumulator accumulator) {
        for (var offset = 0; offset < dataBlocks.Length;) {
            var header = dataBlocks[offset];
            var payloadLength = (header & 0x1F);
            var blockLength = (payloadLength + 1);

            if ((offset + blockLength) > dataBlocks.Length) {
                return false;
            }

            if (((header >> 5) == CtaVendorSpecificTag) && (payloadLength >= 3)) {
                var dataBlock = dataBlocks.Slice(start: offset, length: blockLength);

                if (
                    (dataBlock[1] == 0xD8) &&
                    (dataBlock[2] == 0x5D) &&
                    (dataBlock[3] == 0xC4)
                ) {
                    ParseHdmiForumBlock(dataBlock: dataBlock, accumulator: ref accumulator);
                } else if (
                    (dataBlock[1] == 0x1A) &&
                    (dataBlock[2] == 0x00) &&
                    (dataBlock[3] == 0x00)
                ) {
                    ParseAmdFreeSyncBlock(dataBlock: dataBlock, accumulator: ref accumulator);
                }
            }

            offset += blockLength;
        }

        return true;
    }

    // CTA coordinates include the one-byte data-block header. HDMI Forum VRRmin/VRRmax occupy bytes 9 and 10.
    private static void ParseHdmiForumBlock(ReadOnlySpan<byte> dataBlock, ref RangeAccumulator accumulator) {
        // The OUI's versioned prefix is stable: zero is invalid, while later versions retain these base fields.
        if ((dataBlock.Length < 11) || (dataBlock[4] == 0)) {
            return;
        }

        var minimum = (dataBlock[9] & 0x3F);
        var maximum = (((dataBlock[9] & 0xC0) << 2) | dataBlock[10]);

        // HDMI Forum defines 1..48 for VRRmin. VRRmax values below 100 are interpreted as the current mode's base
        // refresh rate; some shipping low-rate sinks use a nonzero sub-100 encoding instead of the canonical zero.
        if ((minimum is < 1 or > 48) || ((maximum >= 100) && (maximum <= minimum))) {
            return;
        }

        accumulator.Add(
            minimumHertz: minimum,
            maximumHertz: ((maximum < 100) ? null : maximum),
            source: VariableRefreshSource.HdmiForum
        );
    }

    // AMD's FreeSync VSDB is vendor-defined. The stable v1/v2-compatible prefix after the OUI is
    // major-version, minor/capabilities, minimum Hz, maximum Hz, flags. Newer blocks retain that prefix.
    private static void ParseAmdFreeSyncBlock(ReadOnlySpan<byte> dataBlock, ref RangeAccumulator accumulator) {
        if ((dataBlock.Length < 9) || (dataBlock[4] == 0)) {
            return;
        }

        var minimum = dataBlock[6];
        var maximum = dataBlock[7];

        if ((minimum == 0) || (maximum <= minimum)) {
            return;
        }

        accumulator.Add(
            minimumHertz: minimum,
            maximumHertz: maximum,
            source: VariableRefreshSource.AmdFreeSync
        );
    }

    private static bool ParseDisplayIdExtension(ReadOnlySpan<byte> extension, double? activeSignalHertz, ref RangeAccumulator accumulator) {
        // DisplayID section header: tag, version, payload byte count, product type, extension count.
        var payloadLength = extension[2];
        var payloadStart = 5;
        var payloadEnd = (payloadStart + payloadLength);

        // DisplayID 2.1 retains the 0x20 wire-format section version; future section layouts are not assumed compatible.
        if ((payloadEnd > 127) || (extension[1] != 0x20)) {
            return false;
        }

        for (var offset = payloadStart; offset < payloadEnd;) {
            if ((payloadEnd - offset) < 3) {
                return false;
            }

            var tag = extension[offset];
            var revisionAndDescriptorSize = extension[offset + 1];
            var blockPayloadLength = extension[offset + 2];
            var blockEnd = (offset + 3 + blockPayloadLength);

            if (blockEnd > payloadEnd) {
                return false;
            }

            if (tag == DisplayIdAdaptiveSyncTag) {
                if (!ParseDisplayIdAdaptiveSyncBlock(
                    block: extension.Slice(start: offset, length: (blockEnd - offset)),
                    revisionAndDescriptorSize: revisionAndDescriptorSize,
                    activeSignalHertz: activeSignalHertz,
                    accumulator: ref accumulator
                )) {
                    return false;
                }
            } else if (tag == DisplayIdCtaDataBlockCollectionTag) {
                if (!ParseCtaDataBlocks(
                    dataBlocks: extension.Slice(start: (offset + 3), length: blockPayloadLength),
                    accumulator: ref accumulator
                )) {
                    return false;
                }
            }

            offset = blockEnd;
        }

        return true;
    }

    private static bool ParseDisplayIdAdaptiveSyncBlock(
        ReadOnlySpan<byte> block,
        byte revisionAndDescriptorSize,
        double? activeSignalHertz,
        ref RangeAccumulator accumulator
    ) {
        var revision = (revisionAndDescriptorSize & 0x07);
        var descriptorLength = (6 + ((revisionAndDescriptorSize >> 4) & 0x07));
        var payloadLength = block[2];

        if (
            (revision != 0) ||
            ((revisionAndDescriptorSize & 0x88) != 0) ||
            (payloadLength < descriptorLength) ||
            ((payloadLength % descriptorLength) != 0)
        ) {
            return false;
        }

        var selected = default(DisplayIdRangeSelection);

        for (var offset = 3; offset < block.Length; offset += descriptorLength) {
            var descriptor = block.Slice(start: offset, length: descriptorLength);
            var flags = descriptor[0];
            var mode = ((flags >> 2) & 0x03);

            // Revision 0 reserves bits 7:6, modes 2/3, and descriptor byte 4 bits 7:2. Game-style variable
            // presentation needs Adaptive V-Total support (mode 1), not fixed-average-V-total alone (mode 0).
            if (
                ((flags & 0xC0) != 0) ||
                (mode != 1) ||
                ((descriptor[4] & 0xFC) != 0)
            ) {
                continue;
            }

            var minimumCode = descriptor[2];
            var maximumCode = (descriptor[3] | ((descriptor[4] & 0x03) << 8));

            if ((minimumCode == 0) || (maximumCode == 0)) {
                continue;
            }

            var minimumHertz = (minimumCode / 1.001);
            var maximumHertz = ((maximumCode + 1) * 1.00035);

            if (maximumHertz <= minimumHertz) {
                continue;
            }

            selected.Consider(
                minimumHertz: minimumHertz,
                maximumHertz: maximumHertz,
                native: ((flags & 0x01) != 0),
                activeSignalHertz: activeSignalHertz
            );
        }

        if (selected.HasValue) {
            accumulator.Add(
                minimumHertz: selected.MinimumHertz,
                maximumHertz: selected.MaximumHertz,
                source: VariableRefreshSource.DisplayIdAdaptiveSync
            );
        }

        return true;
    }

    private struct DisplayIdRangeSelection {
        public bool HasValue;
        public bool Native;
        public double MinimumHertz;
        public double MaximumHertz;

        public void Consider(double minimumHertz, double maximumHertz, bool native, double? activeSignalHertz) {
            // A descriptor below the current physical signal cannot describe the active operating mode.
            if ((activeSignalHertz is { } active) && (maximumHertz + 0.01 < active)) {
                return;
            }

            if (
                !HasValue ||
                (native && !Native) ||
                ((native == Native) && (maximumHertz < MaximumHertz))
            ) {
                HasValue = true;
                Native = native;
                MinimumHertz = minimumHertz;
                MaximumHertz = maximumHertz;
            }
        }
    }

    private struct RangeAccumulator {
        private bool m_hasValue;
        private bool m_conflicted;
        private double m_minimumHertz;
        private double? m_maximumHertz;
        private VariableRefreshSource m_source;

        public void Merge(in RangeAccumulator other) {
            if (other.m_conflicted) {
                m_conflicted = true;

                return;
            }

            if (other.m_hasValue) {
                Add(
                    minimumHertz: other.m_minimumHertz,
                    maximumHertz: other.m_maximumHertz,
                    source: other.m_source
                );
            }
        }

        public void Add(double minimumHertz, double? maximumHertz, VariableRefreshSource source) {
            if (!m_hasValue) {
                m_hasValue = true;
                m_minimumHertz = minimumHertz;
                m_maximumHertz = maximumHertz;
                m_source = source;

                return;
            }

            m_minimumHertz = Math.Max(val1: m_minimumHertz, val2: minimumHertz);
            m_maximumHertz = (m_maximumHertz, maximumHertz) switch {
                ({ } left, { } right) => Math.Min(val1: left, val2: right),
                ({ } left, null) => left,
                (null, { } right) => right,
                _ => null,
            };
            m_source |= source;

            if ((m_maximumHertz is { } maximum) && (maximum <= m_minimumHertz)) {
                m_conflicted = true;
            }
        }

        public readonly VariableRefreshCapabilities CreateCapabilities() {
            if (!m_hasValue || m_conflicted) {
                return VariableRefreshCapabilities.Unknown;
            }

            return VariableRefreshCapabilities.CreateSupported(
                range: new VariableRefreshRange(minimumHertz: m_minimumHertz, maximumHertz: m_maximumHertz),
                source: m_source
            );
        }
    }
}
