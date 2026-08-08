using System.Diagnostics;
using Puck.Abstractions.Pacing;
using Puck.Launcher;
using Puck.Platform.Windows;

namespace Puck.Post;

/// <summary>Tier-A fixtures for explicit EDID VRR discovery and display-aware pacing policy.</summary>
internal sealed class DisplayTimingStage : IPostStage {
    private const int EdidBlockSize = 128;

    /// <inheritdoc/>
    public string Name => "display-timing";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        try {
            ValidateEdidParsing();
            ValidatePacingPolicy();

            return PostStageOutcome.Pass(detail: "explicit DisplayID/HDMI/FreeSync VRR declarations parsed; generic/malformed inputs rejected; pacing uses real bounds");
        } catch (InvalidOperationException exception) {
            return PostStageOutcome.Fail(detail: exception.Message);
        }
    }

    private static void ValidateEdidParsing() {
        var noVariableRefresh = CreateBaseEdid(extensionCount: 0);

        // A perfectly valid EDID monitor-range descriptor is not an Adaptive-Sync declaration.
        noVariableRefresh[72] = 0x00;
        noVariableRefresh[73] = 0x00;
        noVariableRefresh[74] = 0x00;
        noVariableRefresh[75] = 0xFD;
        noVariableRefresh[76] = 0x00;
        noVariableRefresh[77] = 48;
        noVariableRefresh[78] = 144;
        SetChecksum(block: noVariableRefresh.AsSpan(start: 0, length: EdidBlockSize));
        RequireUnknown(
            capabilities: EdidVariableRefreshParser.Parse(edid: noVariableRefresh, activeSignalHertz: 120.0),
            scenario: "generic EDID monitor range"
        );

        var displayId = CreateDisplayIdEdid(minimumCode: 48, maximumCode: 119);
        var displayIdCapabilities = EdidVariableRefreshParser.Parse(edid: displayId, activeSignalHertz: 120.0);

        RequireSupported(
            capabilities: displayIdCapabilities,
            source: VariableRefreshSource.DisplayIdAdaptiveSync,
            minimumHertz: (48.0 / 1.001),
            maximumHertz: (120.0 * 1.00035),
            scenario: "DisplayID Adaptive-Sync"
        );

        var hdmiForum = CreateCtaEdid(CreateHdmiForumBlock(minimumHertz: 48, maximumHertz: 120));

        RequireSupported(
            capabilities: EdidVariableRefreshParser.Parse(edid: hdmiForum, activeSignalHertz: 120.0),
            source: VariableRefreshSource.HdmiForum,
            minimumHertz: 48.0,
            maximumHertz: 120.0,
            scenario: "HDMI Forum VRR"
        );

        var lowRateHdmiForum = CreateCtaEdid(CreateHdmiForumBlock(minimumHertz: 40, maximumHertz: 60));
        var lowRateCapabilities = EdidVariableRefreshParser.Parse(edid: lowRateHdmiForum, activeSignalHertz: 60.0);

        if ((lowRateCapabilities.Range is not { MaximumHertz: null } lowRateRange) || (lowRateRange.MinimumHertz != 40.0)) {
            throw new InvalidOperationException(message: "HDMI Forum's noncanonical low-rate maximum was not treated as mode-max");
        }

        var hdmiModeMaximum = CreateCtaEdid(CreateHdmiForumBlock(minimumHertz: 40, maximumHertz: 0));
        var modeMaximumCapabilities = EdidVariableRefreshParser.Parse(edid: hdmiModeMaximum, activeSignalHertz: 120.0);

        if ((modeMaximumCapabilities.Range is not { MaximumHertz: null } modeRange) || (modeRange.MinimumHertz != 40.0)) {
            throw new InvalidOperationException(message: "HDMI Forum mode-derived VRR maximum was not preserved as mode-max");
        }

        var amdFreeSync = CreateCtaEdid(CreateAmdFreeSyncBlock(minimumHertz: 48, maximumHertz: 144));

        RequireSupported(
            capabilities: EdidVariableRefreshParser.Parse(edid: amdFreeSync, activeSignalHertz: 144.0),
            source: VariableRefreshSource.AmdFreeSync,
            minimumHertz: 48.0,
            maximumHertz: 144.0,
            scenario: "AMD FreeSync"
        );

        var nestedCta = CreateDisplayIdCtaCollectionEdid(CreateAmdFreeSyncBlock(minimumHertz: 48, maximumHertz: 165));

        RequireSupported(
            capabilities: EdidVariableRefreshParser.Parse(edid: nestedCta, activeSignalHertz: 120.0),
            source: VariableRefreshSource.AmdFreeSync,
            minimumHertz: 48.0,
            maximumHertz: 165.0,
            scenario: "DisplayID CTA data-block collection"
        );

        var reservedDisplayIdHeader = CreateDisplayIdEdid(minimumCode: 48, maximumCode: 119);
        reservedDisplayIdHeader[EdidBlockSize + 6] |= 0x80;
        SetChecksum(block: reservedDisplayIdHeader.AsSpan(start: EdidBlockSize, length: EdidBlockSize));
        RequireUnknown(
            capabilities: EdidVariableRefreshParser.Parse(edid: reservedDisplayIdHeader, activeSignalHertz: 120.0),
            scenario: "DisplayID Adaptive-Sync reserved header bits"
        );

        var badChecksum = ((byte[])displayId.Clone());
        badChecksum[130] ^= 0x01;
        RequireUnknown(
            capabilities: EdidVariableRefreshParser.Parse(edid: badChecksum, activeSignalHertz: 120.0),
            scenario: "bad extension checksum"
        );

        var malformedCtaTail = CreateCtaEdid(
            CreateHdmiForumBlock(minimumHertz: 48, maximumHertz: 120),
            [0xFF]
        );
        RequireUnknown(
            capabilities: EdidVariableRefreshParser.Parse(edid: malformedCtaTail, activeSignalHertz: 120.0),
            scenario: "valid CTA declaration followed by a truncated block"
        );

        var malformedDisplayIdTail = CreateDisplayIdEdid(minimumCode: 48, maximumCode: 119);
        malformedDisplayIdTail[EdidBlockSize + 2] = 10;
        malformedDisplayIdTail[EdidBlockSize + 14] = 0x2B;
        SetChecksum(block: malformedDisplayIdTail.AsSpan(start: EdidBlockSize, length: EdidBlockSize));
        RequireUnknown(
            capabilities: EdidVariableRefreshParser.Parse(edid: malformedDisplayIdTail, activeSignalHertz: 120.0),
            scenario: "valid DisplayID declaration followed by a truncated block"
        );

        var corruptDeclaredExtension = new byte[3 * EdidBlockSize];
        hdmiForum.CopyTo(array: corruptDeclaredExtension, index: 0);
        corruptDeclaredExtension[126] = 2;
        SetChecksum(block: corruptDeclaredExtension.AsSpan(start: 0, length: EdidBlockSize));
        corruptDeclaredExtension[2 * EdidBlockSize] = 0x02;
        corruptDeclaredExtension[(3 * EdidBlockSize) - 1] = 1;
        RequireUnknown(
            capabilities: EdidVariableRefreshParser.Parse(edid: corruptDeclaredExtension, activeSignalHertz: 120.0),
            scenario: "valid declaration followed by a corrupt declared extension"
        );

        var truncated = CreateBaseEdid(extensionCount: 1);
        RequireUnknown(
            capabilities: EdidVariableRefreshParser.Parse(edid: truncated, activeSignalHertz: 120.0),
            scenario: "truncated declared extension"
        );

        var conflicting = CreateCtaEdid(
            CreateHdmiForumBlock(minimumHertz: 48, maximumHertz: 120),
            CreateAmdFreeSyncBlock(minimumHertz: 130, maximumHertz: 144)
        );
        RequireUnknown(
            capabilities: EdidVariableRefreshParser.Parse(edid: conflicting, activeSignalHertz: 120.0),
            scenario: "contradictory explicit ranges"
        );
    }

    private static void ValidatePacingPolicy() {
        var standard = Snapshot(signalHertz: 120.0, minimumHertz: 48.0, maximumHertz: 120.0);
        RequireDecision(
            actual: PresentPacingPolicy.Resolve(timing: standard, requestedHertz: 0.0),
            expectedHertz: 117.0,
            expectedBasis: PresentPacingBasis.VariableRefreshRange,
            scenario: "48-120 Hz automatic"
        );

        var narrow = Snapshot(signalHertz: 120.0, minimumHertz: 118.0, maximumHertz: 120.0);
        RequireDecision(
            actual: PresentPacingPolicy.Resolve(timing: narrow, requestedHertz: 0.0),
            expectedHertz: 118.0,
            expectedBasis: PresentPacingBasis.VariableRefreshRange,
            scenario: "118-120 Hz narrow range"
        );

        var reducedSignal = Snapshot(signalHertz: 60.0, minimumHertz: 48.0, maximumHertz: 120.0);
        RequireDecision(
            actual: PresentPacingPolicy.Resolve(timing: reducedSignal, requestedHertz: 0.0),
            expectedHertz: 57.0,
            expectedBasis: PresentPacingBasis.VariableRefreshRange,
            scenario: "120 Hz panel on a 60 Hz signal"
        );

        var belowRangeSignal = Snapshot(signalHertz: 30.0, minimumHertz: 48.0, maximumHertz: 120.0);
        RequireDecision(
            actual: PresentPacingPolicy.Resolve(timing: belowRangeSignal, requestedHertz: 0.0),
            expectedHertz: 30.0,
            expectedBasis: PresentPacingBasis.SignalTiming,
            scenario: "signal below advertised VRR interval"
        );

        var unknownVariableRefresh = new DisplayTimingSnapshot(
            Signal: new DisplaySignalTiming(hertz: 120.0),
            VariableRefresh: VariableRefreshCapabilities.Unknown
        );
        RequireDecision(
            actual: PresentPacingPolicy.Resolve(timing: unknownVariableRefresh, requestedHertz: 0.0),
            expectedHertz: 120.0,
            expectedBasis: PresentPacingBasis.SignalTiming,
            scenario: "unknown VRR fallback"
        );

        var variableRefreshWithoutSignal = new DisplayTimingSnapshot(
            Signal: DisplaySignalTiming.Unknown,
            VariableRefresh: VariableRefreshCapabilities.CreateSupported(
                range: new VariableRefreshRange(minimumHertz: 48.0, maximumHertz: 165.0),
                source: VariableRefreshSource.AmdFreeSync
            )
        );
        RequireDecision(
            actual: PresentPacingPolicy.Resolve(timing: variableRefreshWithoutSignal, requestedHertz: 0.0),
            expectedHertz: 0.0,
            expectedBasis: PresentPacingBasis.Unbounded,
            scenario: "advertised VRR without active signal timing"
        );

        RequireDecision(
            actual: PresentPacingPolicy.Resolve(timing: standard, requestedHertz: 240.0),
            expectedHertz: 120.0,
            expectedBasis: PresentPacingBasis.ExplicitTarget,
            scenario: "explicit target above physical signal ceiling"
        );
        RequireDecision(
            actual: PresentPacingPolicy.Resolve(timing: standard, requestedHertz: 30.0),
            expectedHertz: 30.0,
            expectedBasis: PresentPacingBasis.ExplicitTarget,
            scenario: "explicit target below VRR minimum"
        );

        var adaptiveBelowSignal = Snapshot(signalHertz: 165.0, minimumHertz: 48.0, maximumHertz: 100.0);
        RequireDecision(
            actual: PresentPacingPolicy.Resolve(timing: adaptiveBelowSignal, requestedHertz: 120.0),
            expectedHertz: 120.0,
            expectedBasis: PresentPacingBasis.ExplicitTarget,
            scenario: "explicit target above adaptive range but below physical signal"
        );
        RequireDecision(
            actual: PresentPacingPolicy.Resolve(timing: adaptiveBelowSignal, requestedHertz: 200.0),
            expectedHertz: 165.0,
            expectedBasis: PresentPacingBasis.ExplicitTarget,
            scenario: "explicit target above physical signal"
        );
        RequireDecision(
            actual: PresentPacingPolicy.Resolve(timing: DisplayTimingSnapshot.Unknown, requestedHertz: 0.0),
            expectedHertz: 0.0,
            expectedBasis: PresentPacingBasis.Unbounded,
            scenario: "unknown display"
        );

        if (new PresentPacingDecision(TargetHertz: (Stopwatch.Frequency * 2.0), Basis: PresentPacingBasis.ExplicitTarget).ToPeriodTicks(frequency: Stopwatch.Frequency) != 1L) {
            throw new InvalidOperationException(message: "A sub-tick positive pacing period did not clamp to one tick");
        }

        if (new PresentPacingDecision(TargetHertz: double.Epsilon, Basis: PresentPacingBasis.ExplicitTarget).ToPeriodTicks(frequency: Stopwatch.Frequency) != long.MaxValue) {
            throw new InvalidOperationException(message: "An overflowing positive pacing period did not saturate");
        }

        try {
            _ = new VariableRefreshRange(minimumHertz: 60.0, maximumHertz: 60.0);
            throw new InvalidOperationException(message: "A degenerate VRR interval was accepted");
        } catch (ArgumentOutOfRangeException) {
            // Expected: a supported interval must have positive width.
        }

        try {
            _ = VariableRefreshCapabilities.CreateSupported(
                range: default,
                source: VariableRefreshSource.DisplayIdAdaptiveSync
            );
            throw new InvalidOperationException(message: "Default VRR range bypassed the supported-capability invariant");
        } catch (ArgumentOutOfRangeException) {
            // Expected: default(struct) cannot represent supported capabilities.
        }
    }

    private static DisplayTimingSnapshot Snapshot(double signalHertz, double minimumHertz, double maximumHertz) => new(
        Signal: new DisplaySignalTiming(hertz: signalHertz),
        VariableRefresh: VariableRefreshCapabilities.CreateSupported(
            range: new VariableRefreshRange(minimumHertz: minimumHertz, maximumHertz: maximumHertz),
            source: VariableRefreshSource.DisplayIdAdaptiveSync
        )
    );

    private static byte[] CreateBaseEdid(byte extensionCount) {
        var edid = new byte[EdidBlockSize];
        ReadOnlySpan<byte> header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

        header.CopyTo(destination: edid);
        edid[18] = 1;
        edid[19] = 4;
        edid[126] = extensionCount;
        SetChecksum(block: edid);

        return edid;
    }

    private static byte[] CreateDisplayIdEdid(byte minimumCode, ushort maximumCode) {
        var edid = CreateBaseEdid(extensionCount: 1);

        Array.Resize(array: ref edid, newSize: (2 * EdidBlockSize));

        var extension = edid.AsSpan(start: EdidBlockSize, length: EdidBlockSize);

        extension[0] = 0x70;
        extension[1] = 0x20;
        extension[2] = 9;
        extension[5] = 0x2B;
        extension[6] = 0x00;
        extension[7] = 6;
        extension[8] = 0x05; // native range + FAVT/AVT operation mode
        extension[9] = 0;
        extension[10] = minimumCode;
        extension[11] = ((byte)maximumCode);
        extension[12] = ((byte)((maximumCode >> 8) & 0x03));
        extension[13] = 0;
        SetChecksum(block: extension);

        return edid;
    }

    private static byte[] CreateCtaEdid(params byte[][] dataBlocks) {
        var edid = CreateBaseEdid(extensionCount: 1);

        Array.Resize(array: ref edid, newSize: (2 * EdidBlockSize));

        var extension = edid.AsSpan(start: EdidBlockSize, length: EdidBlockSize);
        var offset = 4;

        extension[0] = 0x02;
        extension[1] = 3;

        foreach (var dataBlock in dataBlocks) {
            dataBlock.CopyTo(array: edid, index: (EdidBlockSize + offset));
            offset += dataBlock.Length;
        }

        extension[2] = ((byte)offset);
        SetChecksum(block: extension);

        return edid;
    }

    private static byte[] CreateDisplayIdCtaCollectionEdid(params byte[][] dataBlocks) {
        var edid = CreateBaseEdid(extensionCount: 1);

        Array.Resize(array: ref edid, newSize: (2 * EdidBlockSize));

        var extension = edid.AsSpan(start: EdidBlockSize, length: EdidBlockSize);
        var payloadLength = 0;

        extension[0] = 0x70;
        extension[1] = 0x20;
        extension[5] = 0x81;
        extension[6] = 0;

        foreach (var dataBlock in dataBlocks) {
            dataBlock.CopyTo(array: edid, index: (EdidBlockSize + 8 + payloadLength));
            payloadLength += dataBlock.Length;
        }

        extension[7] = checked((byte)payloadLength);
        extension[2] = checked((byte)(3 + payloadLength));
        SetChecksum(block: extension);

        return edid;
    }

    private static byte[] CreateHdmiForumBlock(byte minimumHertz, ushort maximumHertz) {
        var block = new byte[11];

        block[0] = ((3 << 5) | 10);
        block[1] = 0xD8;
        block[2] = 0x5D;
        block[3] = 0xC4;
        block[4] = 1;
        block[9] = ((byte)((minimumHertz & 0x3F) | ((maximumHertz >> 2) & 0xC0)));
        block[10] = ((byte)maximumHertz);

        return block;
    }

    private static byte[] CreateAmdFreeSyncBlock(byte minimumHertz, byte maximumHertz) => [
        ((3 << 5) | 8),
        0x1A,
        0x00,
        0x00,
        1,
        1,
        minimumHertz,
        maximumHertz,
        0,
    ];

    private static void SetChecksum(Span<byte> block) {
        block[127] = 0;

        var sum = 0;

        for (var index = 0; index < 127; ++index) {
            sum += block[index];
        }

        block[127] = ((byte)((256 - (sum & 0xFF)) & 0xFF));
    }

    private static void RequireUnknown(VariableRefreshCapabilities capabilities, string scenario) {
        if (capabilities.Support != VariableRefreshSupport.Unknown) {
            throw new InvalidOperationException(message: $"{scenario} falsely reported {capabilities.Support} VRR");
        }
    }

    private static void RequireSupported(
        VariableRefreshCapabilities capabilities,
        VariableRefreshSource source,
        double minimumHertz,
        double maximumHertz,
        string scenario
    ) {
        if (
            (capabilities.Support != VariableRefreshSupport.Supported) ||
            (capabilities.Source != source) ||
            (capabilities.Range is not { MaximumHertz: { } actualMaximum } range) ||
            (Math.Abs(value: (range.MinimumHertz - minimumHertz)) > 0.0001) ||
            (Math.Abs(value: (actualMaximum - maximumHertz)) > 0.0001)
        ) {
            throw new InvalidOperationException(message: $"{scenario} did not produce the expected explicit VRR range/source");
        }
    }

    private static void RequireDecision(PresentPacingDecision actual, double expectedHertz, PresentPacingBasis expectedBasis, string scenario) {
        if (
            (Math.Abs(value: (actual.TargetHertz - expectedHertz)) > 0.0001) ||
            (actual.Basis != expectedBasis)
        ) {
            throw new InvalidOperationException(message: $"{scenario} resolved {actual.TargetHertz:0.###} Hz/{actual.Basis}, expected {expectedHertz:0.###} Hz/{expectedBasis}");
        }
    }
}
