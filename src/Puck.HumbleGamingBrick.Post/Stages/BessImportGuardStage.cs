using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: gates the BESS importer's own validate-then-apply safety contract (M-08) against the shared
/// <see cref="BessMalformedCorpus"/> — always run, not diagnostic-only. This is doctrine-clean as a POST gate (not
/// evidence tooling) because every case exercises <see cref="BessImporter.Import"/>, OUR code's own behavior;
/// nothing here depends on an external reference emulator or corpus, so it needs no skip path and runs anywhere.
/// Three case sets, all against dedicated color-capable (CGB) probe machines:
/// <list type="bullet">
/// <item><see cref="BessMalformedCorpus.Build"/>: each case must be rejected with <see cref="InvalidDataException"/>,
/// and the probe's machine snapshot must be byte-identical before and after every attempt — proving a rejected
/// import is a true no-op, not a caught exception over already-mutated state (the review's counterexample:
/// destination-capacity and CPU/register mutation happen on the SAME call, so a partial apply before the throw would
/// otherwise leave the probe silently corrupted). Includes the optional <c>MBC </c> block's own shape (a
/// length-not-divisible-by-3 fragment, and a record whose address falls outside <c>0x0000-0x7FFF</c>/
/// <c>0xA000-0xBFFF</c>), an incompatible <c>CORE</c> major version, a duplicate <c>CORE</c> block, and a known
/// block (<c>MBC </c>) appearing before the required <c>CORE</c> block (H-10/H-11) — all validated in the same pure
/// parse pass, before the CORE/register/buffer state above it is ever applied.</item>
/// <item><see cref="BessMalformedCorpus.BuildGracefulShapeCases"/>: the spec's legal-but-undersized work-RAM/video-RAM
/// shapes must instead be ACCEPTED (no throw) with the destination's untouched remainder zero-filled — a probe
/// sentinel-seeded before import catches the failure mode where old destination bytes are silently retained past the
/// imported span.</item>
/// <item><see cref="BessMalformedCorpus.BuildExtendedCoreCase"/> (M-10): a <c>CORE</c> block padded with extra tail
/// bytes beyond the defined 0xD0-byte prefix must also be ACCEPTED, and must produce the SAME BESS-modeled state —
/// both the returned <see cref="BessImportReport"/> and the resulting machine snapshot — as importing the
/// unextended file into an independent probe of the same configuration; this is the forward-compat proof the spec's
/// "ignore any excess bytes" clause requires.</item>
/// </list>
/// </summary>
internal sealed class BessImportGuardStage : IPostStage {
    private const int ExportFrames = 8;

    /// <inheritdoc/>
    public string Name =>
        "bess-import-guard";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var rom = SyntheticRom.Create();

        // CGB, so the destination-capacity check for the palette regions (capacity 0x40, not 0) is exercised as a real
        // boundary rather than the degenerate "any nonzero size is already too big" DMG case.
        using var source = PostMachine.Build(model: ConsoleModel.Cgb, rom: rom);

        PostMachine.RunFrames(instance: source, frames: ExportFrames);

        var (goodFile, _) = BessExporter.Export(instance: source, model: ConsoleModel.Cgb);

        using var probe = PostMachine.Build(model: ConsoleModel.Cgb, rom: rom);
        var baseline = probe.Machine.Snapshot();
        var caseCount = 0;
        var failures = new List<string>();

        foreach (var (label, malformed) in BessMalformedCorpus.Build(goodFile: goodFile)) {
            ++caseCount;

            InvalidDataException? rejection = null;

            try {
                BessImporter.Import(instance: probe, file: malformed);
            } catch (InvalidDataException exception) {
                rejection = exception;
            }

            if (rejection is null) {
                failures.Add(item: $"\"{label}\": import did not throw InvalidDataException");

                continue;
            }

            if (!baseline.ContentEquals(other: probe.Machine.Snapshot())) {
                failures.Add(item: $"\"{label}\": rejected but the probe machine snapshot changed");
            }
        }

        var gracefulCaseCount = 0;

        foreach (var shapeCase in BessMalformedCorpus.BuildGracefulShapeCases(goodFile: goodFile)) {
            ++gracefulCaseCount;

            using var fillProbe = PostMachine.Build(model: ConsoleModel.Cgb, rom: rom);
            var fillBus = fillProbe.GetRequiredService<ISystemBus>();

            // Sentinel-seed the whole destination window before import: a retained-old-bytes bug (writing only the
            // imported span and leaving the rest as-is) would surface as leftover 0xAA past ImportedBytes.Length,
            // where the spec instead requires 0.
            for (var index = 0; (index < shapeCase.DestinationCapacity); ++index) {
                fillBus.WriteByte(address: (ushort)(shapeCase.DestinationStart + index), value: 0xAA);
            }

            try {
                BessImporter.Import(instance: fillProbe, file: shapeCase.File);
            } catch (InvalidDataException exception) {
                failures.Add(item: $"\"{shapeCase.Label}\": expected acceptance (legal per spec) but import threw: {exception.Message}");

                continue;
            }

            for (var index = 0; (index < shapeCase.DestinationCapacity); ++index) {
                var actual = fillBus.ReadByte(address: (ushort)(shapeCase.DestinationStart + index));
                var expected = ((index < shapeCase.ImportedBytes.Length) ? shapeCase.ImportedBytes[index] : (byte)0);

                if (actual != expected) {
                    failures.Add(item: $"\"{shapeCase.Label}\": destination byte {index} is 0x{actual:X2}, expected 0x{expected:X2} (imported span or spec zero-fill)");

                    break;
                }
            }
        }

        // (M-10) The extended-CORE forward-compat case: not shaped like BuildGracefulShapeCases (there is no single
        // destination region to fill-and-compare), so its equivalence is asserted directly here — import the
        // unextended and extended files into two FRESH, independently built probes of the same configuration, and
        // require both the returned report and the resulting machine snapshot to match exactly.
        ++gracefulCaseCount;

        var extendedCoreLabel = "extended CORE (extra tail bytes)";
        var extendedCoreFile = BessMalformedCorpus.BuildExtendedCoreCase(goodFile: goodFile);

        try {
            using var unextendedProbe = PostMachine.Build(model: ConsoleModel.Cgb, rom: rom);
            using var extendedProbe = PostMachine.Build(model: ConsoleModel.Cgb, rom: rom);

            var unextendedReport = BessImporter.Import(instance: unextendedProbe, file: goodFile);
            var extendedReport = BessImporter.Import(instance: extendedProbe, file: extendedCoreFile);

            if (unextendedReport != extendedReport) {
                failures.Add(item: $"\"{extendedCoreLabel}\": the import report differs from the unextended file's ({unextendedReport} vs {extendedReport})");
            }

            if (!unextendedProbe.Machine.Snapshot().ContentEquals(other: extendedProbe.Machine.Snapshot())) {
                failures.Add(item: $"\"{extendedCoreLabel}\": the resulting machine snapshot differs from the unextended file's");
            }
        } catch (InvalidDataException exception) {
            failures.Add(item: $"\"{extendedCoreLabel}\": expected acceptance (legal per spec) but import threw: {exception.Message}");
        }

        return ((failures.Count == 0)
            ? PostStageOutcome.Pass(detail: $"{caseCount} malformed BESS cases (truncation/out-of-bounds-offset/undersized-CORE/garbage-footer/missing-END/nonzero-or-non-final-END/oversized work-RAM+video-RAM+palette destinations/undersized palette+OAM+HRAM/trailing-fragment+out-of-domain-address MBC/incompatible-CORE-major-version/duplicate-CORE/MBC-before-CORE) all rejected with InvalidDataException, probe machine untouched every time; {gracefulCaseCount} legal cases accepted (zero-filled undersized work-RAM+video-RAM, extended-CORE with matching modeled state)")
            : PostStageOutcome.Fail(detail: string.Join(separator: "; ", values: failures)));
    }
}
