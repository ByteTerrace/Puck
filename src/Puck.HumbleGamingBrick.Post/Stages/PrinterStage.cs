using Puck.HumbleGamingBrick.Interfaces;
using Puck.Snapshots;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: the machine's serial printer peripheral as a deterministic serial-cable peer. A synthetic ROM (<see cref="PrinterRom"/>)
/// drives one printer through the full protocol — INIT, an uncompressed DATA band, an RLE-compressed DATA band encoding
/// the SAME image, PRINT, then STATUS polls — over a <see cref="GamePrinterLinkSession"/>, and the completed print is
/// captured as the machine-to-host <see cref="GamePrintout"/> event. The stage asserts the protocol ran clean (a printer
/// alive, no checksum error, the print-in-progress status observed transitioning busy → ready off the deterministic tick
/// clock), that the two DATA bands rendered to identical halves (the compressed band round-tripped exactly), and — the
/// determinism proof, mirroring the serial-link stages — that the emitted print's fingerprint, the final machine
/// snapshot, and the final printer state are bit-identical across a plain replay AND across a mid-image
/// snapshot/restore/reconnect churn, proving the printer's whole state serializes.
/// <para>
/// H-05: two further scenarios drive 13 and 14 consecutive valid DATA bands (one at the image buffer's ~12.5-band
/// capacity boundary, one past it) through <c>INIT</c>/DATA×N/<c>PRINT</c>/STATUS with no crash, checking the printed
/// image against an independent reference model of <c>GamePrinterDevice.UnpackBand</c>'s circular-buffer wrap
/// policy, and replaying each once for determinism.
/// </para>
/// </summary>
internal sealed class PrinterStage : IPostStage {
    private const ulong BudgetStep = 2_048;
    private const int StepCount = 3_000;
    private const ulong OverflowBudgetStep = 2_048;
    private const int OverflowStepCount = 40_000;
    private const byte StatusChecksumError = 0x01;
    private const byte StatusPrinting = 0x06;
    private const byte StatusDone = 0x04;
    private const ushort SerialControlAddress = 0xFF02;
    // The image buffer holds MaxImageHeight/8 = 25 independent 8-row write slots (8*ImageWidth bytes each); a band
    // contributes two consecutive slot writes (row 0 then row 1), so the overflow probe's reference model only needs
    // the total write count modulo that ring size — see ComputeExpectedOverflowImage.
    private const int SegmentBytes = (8 * GamePrinterDevice.ImageWidth);
    private const int SegmentsPerBuffer = (GamePrinterDevice.MaxImageHeight / 8);

    /// <inheritdoc/>
    public string Name =>
        "printer";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var reference = RunScenario(churnAtStep: -1);

        if (Judge(result: reference) is { } failure) {
            return PostStageOutcome.Fail(detail: failure);
        }

        var churnStep = PickChurnStep(probes: reference.Probes);

        if (churnStep < 0) {
            return PostStageOutcome.Fail(detail: "no transfer-idle budget boundary appeared mid-image; the idle gap or budget schedule is wrong");
        }

        // (a) Determinism: a second fresh run on the same schedule reproduces the print, statuses, and final states.
        var replay = RunScenario(churnAtStep: -1);

        if (Difference(expected: reference, actual: replay, leg: "replay") is { } replayFailure) {
            return PostStageOutcome.Fail(detail: replayFailure);
        }

        // (b) Churn: suspend/snapshot/restore/reconnect the machine AND the printer at a transfer-idle boundary mid-image,
        // then continue — the identical outcome proves the printer's parsing/image/countdown state all serializes.
        var churned = RunScenario(churnAtStep: churnStep);

        if (Difference(expected: reference, actual: churned, leg: "churn") is { } churnFailure) {
            return PostStageOutcome.Fail(detail: churnFailure);
        }

        // (c) H-05: 13 valid DATA bands overflow the image buffer's ~12.5-band capacity; 14 drives one band beyond
        // that. Neither may fault, and the printed image must match the wrap-policy reference model exactly.
        foreach (var bandCount in OverflowBandCounts) {
            var overflowReference = RunOverflowScenario(bandCount: bandCount);

            if (JudgeOverflow(result: overflowReference, bandCount: bandCount) is { } overflowFailure) {
                return PostStageOutcome.Fail(detail: overflowFailure);
            }

            var overflowReplay = RunOverflowScenario(bandCount: bandCount);

            if (Difference(expected: overflowReference, actual: overflowReplay, leg: $"{bandCount}-band overflow replay") is { } overflowReplayFailure) {
                return PostStageOutcome.Fail(detail: overflowReplayFailure);
            }
        }

        return PostStageOutcome.Pass(
            detail: $"printed a {reference.Printout!.Width}x{reference.Printout.Height} image through INIT/DATA(raw+RLE)/PRINT/STATUS, busy→ready off the tick clock, replay- and churn-identical (severed transfer-idle at budget step {churnStep}, print fingerprint 0x{reference.Printout.Fingerprint():X16}); 13- and 14-band buffer-overflow scenarios wrapped the image cursor without fault, replay-identical"
        );
    }

    // One complete print scenario on the fixed budget schedule. With churnAtStep >= 0 the session is severed at that
    // boundary (which the reference confirmed transfer-idle), the machine and printer are snapshotted, restored into a
    // fresh machine and printer, and the cable reconnected before the remaining budgets run. The print sink is host-side
    // and survives the swap by being re-attached to the fresh printer.
    private static PrinterScenarioResult RunScenario(int churnAtStep) {
        var rom = PrinterRom.Create();
        var machine = PostMachine.Build(model: ConsoleModel.Dmg, rom: rom);
        var printer = new GamePrinterDevice();
        var sink = new PrintSink();
        var statuses = new List<byte>(capacity: StepCount);
        var probes = new List<BoundaryProbe>(capacity: StepCount);
        var sawChecksumError = false;

        printer.PrintEmitted = sink.OnPrint;

        var session = new GamePrinterLinkSession(machine: machine, printer: printer);

        try {
            for (var step = 0; (step < StepCount); ++step) {
                probes.Add(item: new BoundaryProbe(Idle: IsTransferIdle(machine: machine, printer: printer), ImageOffset: printer.ImageOffset, PrintCount: sink.Count));

                if (step == churnAtStep) {
                    if (!IsTransferIdle(machine: machine, printer: printer)) {
                        throw new InvalidOperationException(message: $"the churn boundary at budget step {step} is not transfer-idle.");
                    }

                    session.Dispose();

                    var machineState = machine.Machine.Snapshot();
                    var printerState = CapturePrinter(printer: printer);
                    var freshMachine = PostMachine.Build(model: ConsoleModel.Dmg, rom: rom);
                    var freshPrinter = new GamePrinterDevice();

                    freshMachine.Machine.Restore(snapshot: machineState);
                    RestorePrinter(printer: freshPrinter, state: printerState);
                    freshPrinter.PrintEmitted = sink.OnPrint;

                    machine.Dispose();

                    machine = freshMachine;
                    printer = freshPrinter;
                    session = new GamePrinterLinkSession(machine: machine, printer: printer);
                }

                session.Run(tCycles: BudgetStep);

                var status = printer.Status;

                statuses.Add(item: status);
                sawChecksumError |= ((status & StatusChecksumError) != 0);
            }

            return new PrinterScenarioResult(
                Printout: sink.Printout,
                PrintCount: sink.Count,
                Probes: probes,
                SawChecksumError: sawChecksumError,
                Statuses: statuses,
                MachineState: machine.Machine.Snapshot(),
                PrinterState: CapturePrinter(printer: printer)
            );
        } finally {
            session.Dispose();
            machine.Dispose();
        }
    }

    // The H-05 overflow boundary: 13 bands is the first count whose 2,560-byte cursor advance exceeds the 32,000-byte
    // image buffer (12 bands land exactly at 30,720; a 13th pushes the naive cursor to 33,280), 14 drives one band past
    // that. No churn — the point is a clean PRINT immediately after N consecutive bands, not mid-image serialization.
    private static readonly int[] OverflowBandCounts = [13, 14];

    // Runs INIT + bandCount raw DATA bands (PrinterRom.CreateOverflow) + PRINT + STATUS polls once, with no churn. The
    // step/budget schedule is far wider than the plain-print scenario's since up to 14 full 650-byte-on-wire packets
    // must clock out over the printer's internal serial rate before the completion marker is reachable.
    private static PrinterScenarioResult RunOverflowScenario(int bandCount) {
        var rom = PrinterRom.CreateOverflow(bandCount: bandCount);
        var machine = PostMachine.Build(model: ConsoleModel.Dmg, rom: rom);
        var printer = new GamePrinterDevice();
        var sink = new PrintSink();
        var statuses = new List<byte>(capacity: OverflowStepCount);
        var sawChecksumError = false;

        printer.PrintEmitted = sink.OnPrint;

        var session = new GamePrinterLinkSession(machine: machine, printer: printer);

        try {
            for (var step = 0; (step < OverflowStepCount); ++step) {
                session.Run(tCycles: OverflowBudgetStep);

                var status = printer.Status;

                statuses.Add(item: status);
                sawChecksumError |= ((status & StatusChecksumError) != 0);
            }

            return new PrinterScenarioResult(
                Printout: sink.Printout,
                PrintCount: sink.Count,
                Probes: [],
                SawChecksumError: sawChecksumError,
                Statuses: statuses,
                MachineState: machine.Machine.Snapshot(),
                PrinterState: CapturePrinter(printer: printer)
            );
        } finally {
            session.Dispose();
            machine.Dispose();
        }
    }

    // Judges an overflow run: no fault reaching this point is itself part of the proof (H-05 was an IndexOutOfRangeException
    // on this exact traffic), plus exactly one print, a clean busy -> ready transition, and the printed image matching the
    // independent wrap-policy reference model exactly (dimensions AND content, not just "didn't crash").
    private static string? JudgeOverflow(PrinterScenarioResult result, int bandCount) {
        if (result.PrintCount != 1) {
            return $"the {bandCount}-band overflow scenario expected exactly one print, observed {result.PrintCount}";
        }

        if (result.Printout is not { } printout) {
            return $"the {bandCount}-band overflow scenario emitted no print";
        }

        if (result.SawChecksumError) {
            return $"the {bandCount}-band overflow scenario raised a checksum error";
        }

        var expectedPixels = ComputeExpectedOverflowImage(bandCount: bandCount);
        var expectedHeight = (expectedPixels.Length / GamePrinterDevice.ImageWidth);

        if ((printout.Width != GamePrinterDevice.ImageWidth) || (printout.Height != expectedHeight)) {
            return $"the {bandCount}-band overflow print is {printout.Width}x{printout.Height}; the wrap-policy reference model expects {GamePrinterDevice.ImageWidth}x{expectedHeight}";
        }

        if (!printout.Pixels.SequenceEqual(other: expectedPixels)) {
            return $"the {bandCount}-band overflow print content diverged from the wrap-policy reference model";
        }

        var firstPrinting = result.Statuses.IndexOf(item: StatusPrinting);

        if (firstPrinting < 0) {
            return $"the {bandCount}-band overflow scenario never reported printing-in-progress (busy) after PRINT";
        }

        if (result.Statuses.IndexOf(item: StatusDone, index: firstPrinting) < 0) {
            return $"the {bandCount}-band overflow scenario reported busy but never transitioned to ready";
        }

        return null;
    }

    // An independent model of GamePrinterDevice.UnpackBand's circular-buffer wrap policy (Core/printer.c:49's overflow
    // citation lives on that method) — computed from band count alone, not by calling the production code. Every
    // overflow band is PrinterRom.BuildOverflowBand, whose first 8-row half always decodes to shade 1 and second half
    // to shade 2, so the image is a ring of SegmentsPerBuffer independent 8-row slots and each band writes two
    // consecutive slots (row 0 = shade 1, row 1 = shade 2) in strict global order; the final printed image is exactly
    // the last (2*bandCount mod SegmentsPerBuffer) slot writes, in order, alternating by parity of their GLOBAL write
    // index — the same reasoning that explains why SameBoy's single top-of-band modulo does not fully guard the buffer
    // (see the citation on UnpackBand): with a wrap mid-band, the freshest content at any slot is whichever half-band
    // write landed there last, not necessarily from the same band.
    private static byte[] ComputeExpectedOverflowImage(int bandCount) {
        var totalWrites = (bandCount * 2);
        var segmentCount = (totalWrites % SegmentsPerBuffer);
        var expected = new byte[segmentCount * SegmentBytes];

        for (var segment = 0; (segment < segmentCount); ++segment) {
            var writeIndex = ((totalWrites - segmentCount) + segment);
            var shade = (byte)(((writeIndex % 2) == 0) ? 1 : 2);

            Array.Fill(array: expected, value: shade, startIndex: (segment * SegmentBytes), count: SegmentBytes);
        }

        return expected;
    }

    // Judges the reference run: exactly one print emitted, the right dimensions, no checksum error, a clean busy -> ready
    // transition observed through the STATUS polls, the two DATA bands rendered to identical halves (the compressed band
    // round-tripped), and a non-uniform image (bytes actually crossed the cable).
    private static string? Judge(PrinterScenarioResult result) {
        if (result.PrintCount != 1) {
            return $"expected exactly one print, observed {result.PrintCount}";
        }

        if (result.Printout is not { } printout) {
            return "no print was emitted";
        }

        if ((printout.Width != GamePrinterDevice.ImageWidth) || (printout.Height != PrinterRom.PrintedRowCount)) {
            return $"the print is {printout.Width}x{printout.Height}; expected {GamePrinterDevice.ImageWidth}x{PrinterRom.PrintedRowCount}";
        }

        if (result.SawChecksumError) {
            return "the printer raised a checksum error during the exchange (a packet's transmitted checksum did not match)";
        }

        var firstPrinting = result.Statuses.IndexOf(item: StatusPrinting);

        if (firstPrinting < 0) {
            return "the printer never reported printing-in-progress (busy) after PRINT";
        }

        var firstDone = result.Statuses.IndexOf(item: StatusDone, index: firstPrinting);

        if (firstDone < 0) {
            return "the printer reported busy but never transitioned to ready (the print countdown never elapsed)";
        }

        // The two DATA bands carry the identical image, so the print's top 16 rows must equal its bottom 16 rows.
        var pixels = printout.Pixels;
        var half = ((printout.Height / 2) * printout.Width);

        if (!pixels[..half].SequenceEqual(other: pixels[half..])) {
            return "the compressed DATA band did not render identically to the uncompressed band (RLE round-trip mismatch)";
        }

        var first = pixels[0];
        var uniform = true;

        foreach (var pixel in pixels) {
            if (pixel != first) {
                uniform = false;

                break;
            }
        }

        return (uniform ? "the printed image is uniform; no varied image data reached the printer" : null);
    }

    // Compares a later run against the reference: the print fingerprint, the emitted-print count, the sampled status
    // sequence, and both final states (machine snapshot + printer state) must match exactly.
    private static string? Difference(PrinterScenarioResult expected, PrinterScenarioResult actual, string leg) {
        if (actual.PrintCount != expected.PrintCount) {
            return $"the {leg} emitted {actual.PrintCount} prints; expected {expected.PrintCount}";
        }

        if ((expected.Printout is null) || (actual.Printout is null) || (actual.Printout.Fingerprint() != expected.Printout.Fingerprint())) {
            return $"the {leg} print fingerprint diverged";
        }

        if (!actual.Statuses.SequenceEqual(second: expected.Statuses)) {
            return $"the {leg} status-poll sequence diverged";
        }

        if (!expected.MachineState.ContentEquals(other: actual.MachineState)) {
            return $"the {leg} final machine state diverged — {HashDivergenceProbe.DescribeDivergence(a: expected.MachineState, b: actual.MachineState)}";
        }

        if (!expected.PrinterState.AsSpan().SequenceEqual(other: actual.PrinterState)) {
            return $"the {leg} final printer state diverged (the printer's serialized state is not reproducible)";
        }

        return null;
    }

    // The first budget boundary that is transfer-idle after at least one DATA band has landed but before the print
    // emits — a genuine mid-image severable instant.
    private static int PickChurnStep(List<BoundaryProbe> probes) {
        for (var step = 0; (step < probes.Count); ++step) {
            var probe = probes[index: step];

            if (probe.Idle && (probe.ImageOffset > 0) && (probe.PrintCount == 0)) {
                return step;
            }
        }

        return -1;
    }

    // A boundary is transfer-idle when the machine's serial transfer bit is clear: with no active transfer no bits are
    // clocked into the printer, so its shift register sits byte-aligned (nothing mid-flight to lose across a snapshot).
    private static bool IsTransferIdle(MachineInstance machine, GamePrinterDevice printer) =>
        ((machine.GetRequiredService<ISystemBus>().ReadByte(address: SerialControlAddress) & 0x80) == 0);

    private static byte[] CapturePrinter(GamePrinterDevice printer) {
        var writer = new StateWriter();

        ((ISnapshotable)printer).SaveState(writer: writer);

        return writer.ToArray();
    }
    private static void RestorePrinter(GamePrinterDevice printer, byte[] state) =>
        ((ISnapshotable)printer).LoadState(reader: new StateReader(buffer: state, start: 0, length: state.Length));

    // A host-side, never-serialized sink for the machine-to-host print event.
    private sealed class PrintSink {
        public int Count;
        public GamePrintout? Printout;

        public void OnPrint(GamePrintout printout) {
            Printout = printout;
            ++Count;
        }
    }
    private readonly record struct BoundaryProbe(
        bool Idle,
        int ImageOffset,
        int PrintCount
    );
    private sealed record PrinterScenarioResult(
        GamePrintout? Printout,
        int PrintCount,
        List<BoundaryProbe> Probes,
        bool SawChecksumError,
        List<byte> Statuses,
        MachineSnapshot MachineState,
        byte[] PrinterState
    );
}
