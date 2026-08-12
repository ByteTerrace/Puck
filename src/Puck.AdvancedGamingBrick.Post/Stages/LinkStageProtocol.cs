namespace Puck.AdvancedGamingBrick.Post;

internal static class LinkStageProtocol {
    internal static LinkSideVerdict ReadVerdict(AgbMachineInstance console) {
        var bus = (AgbBus)console.Machine.Bus;
        var rounds = new (uint Low, uint High)[MicroRoms.LinkRounds];

        for (var round = 0; (round < rounds.Length); ++round) {
            var recordAddress = (MicroRoms.LinkRecordAddress + ((uint)round * 8u));

            rounds[round] = (bus.DebugRead32(address: recordAddress), bus.DebugRead32(address: (recordAddress + 4u)));
        }

        return new LinkSideVerdict(
            IrqCount: bus.DebugRead32(address: MicroRoms.LinkIrqCountAddress),
            Marker: bus.DebugRead32(address: MicroRoms.LinkMarkerAddress),
            SerialControl: bus.DebugRead32(address: MicroRoms.LinkControlAddress),
            Rounds: rounds
        );
    }
    internal static string? VerifySide(LinkSideVerdict verdict, string side, uint expectedControl) {
        if (verdict.Marker != MicroRoms.LinkCompletionMarker) {
            return $"the {side} never completed its {MicroRoms.LinkRounds} rounds (marker 0x{verdict.Marker:X8})";
        }

        if (verdict.IrqCount != MicroRoms.LinkRounds) {
            return $"the {side} observed {verdict.IrqCount} serial IRQ requests; expected {MicroRoms.LinkRounds}";
        }

        if (verdict.SerialControl != expectedControl) {
            return $"the {side}'s final SIOCNT is 0x{verdict.SerialControl:X4}; expected 0x{expectedControl:X4} (id bits / busy)";
        }

        var childWord = MicroRoms.LinkChildSeedWord;

        for (var round = 0; (round < verdict.Rounds.Length); ++round) {
            var parentWord = (ushort)(MicroRoms.LinkParentSendBase + round);
            var expectedLow = (uint)(parentWord | (childWord << 16));

            if (verdict.Rounds[round].Low != expectedLow) {
                return $"the {side}'s round {round} SIOMULTI0/1 is 0x{verdict.Rounds[round].Low:X8}; expected 0x{expectedLow:X8}";
            }

            if (verdict.Rounds[round].High != 0xFFFFFFFFu) {
                return $"the {side}'s round {round} SIOMULTI2/3 is 0x{verdict.Rounds[round].High:X8}; expected 0xFFFFFFFF (absent players)";
            }

            childWord = (ushort)(parentWord ^ MicroRoms.LinkChildTransformMask);
        }

        return null;
    }
}
internal readonly record struct LinkSideVerdict(
    uint IrqCount,
    uint Marker,
    uint SerialControl,
    (uint Low, uint High)[] Rounds
);
