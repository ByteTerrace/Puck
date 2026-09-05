namespace Puck.HumbleGamingBrick.Post;

/// <summary>Dispatches a <see cref="LedgerCase"/> to its <see cref="ProbeKind"/>'s implementation and normalizes the
/// result to one shared <see cref="ProbeOutcome"/> shape, so <see cref="LedgerEvaluator"/> never branches on probe kind.</summary>
internal static class ProbeRunner {
    /// <summary>Runs a runnable case's probe.</summary>
    /// <param name="ledgerCase">The case to run.</param>
    /// <returns>The normalized outcome.</returns>
    public static ProbeOutcome Run(LedgerCase ledgerCase) {
        return ledgerCase.Probe switch {
            ProbeKind.ConformanceSerial => RunConformance(ledgerCase: ledgerCase),
            ProbeKind.AcceptanceFibonacci => RunAcceptance(ledgerCase: ledgerCase),
            ProbeKind.RegisterSignature => RunRegisterSignature(ledgerCase: ledgerCase),
            ProbeKind.GbMicrotest => RunGbMicrotest(ledgerCase: ledgerCase),
            ProbeKind.Screenshot => ScreenshotProbe.Run(ledgerCase: ledgerCase),
            ProbeKind.HexPattern => HexPatternProbe.Run(ledgerCase: ledgerCase),
            ProbeKind.Audio => AudioProbe.Run(ledgerCase: ledgerCase),
            _ => throw new NotSupportedException(message: $"Unhandled probe kind '{ledgerCase.Probe}'."),
        };
    }

    private static RomCase ToRomCase(LedgerCase ledgerCase) =>
        new(
        FrameCap: ledgerCase.FrameCap,
        FullPath: ledgerCase.FullPath,
        Group: ledgerCase.Suite,
        Model: ledgerCase.Model,
        Name: Path.GetFileNameWithoutExtension(path: ledgerCase.FullPath)
    );
    private static ProbeOutcome RunAcceptance(LedgerCase ledgerCase) {
        var (passed, detail) = AcceptanceRomProbe.Run(romCase: ToRomCase(ledgerCase: ledgerCase));

        return new ProbeOutcome(
            Detail: detail,
            Verdict: ((passed == true)
                ? ProbeVerdict.Pass
                : ((passed == false)
                    ? ProbeVerdict.Fail
                    : ProbeVerdict.Inconclusive))
        );
    }
    private static ProbeOutcome RunConformance(LedgerCase ledgerCase) {
        var (result, detail) = ConformanceRomProbe.Run(romCase: ToRomCase(ledgerCase: ledgerCase));

        return new ProbeOutcome(
            Detail: detail,
            Verdict: (result switch {
                ConformanceRomResult.Pass => ProbeVerdict.Pass,
                ConformanceRomResult.Fail => ProbeVerdict.Fail,
                _ => ProbeVerdict.Inconclusive,
            })
        );
    }
    private static ProbeOutcome RunGbMicrotest(LedgerCase ledgerCase) {
        var (result, detail) = GbMicrotestProbe.Run(romCase: ToRomCase(ledgerCase: ledgerCase));

        return new ProbeOutcome(
            Detail: detail,
            Verdict: (result switch {
                GbMicrotestResult.Pass => ProbeVerdict.Pass,
                GbMicrotestResult.Fail => ProbeVerdict.Fail,
                _ => ProbeVerdict.Inconclusive,
            })
        );
    }
    private static ProbeOutcome RunRegisterSignature(LedgerCase ledgerCase) {
        var (result, detail) = RegisterSignatureProbe.Run(romCase: ToRomCase(ledgerCase: ledgerCase));

        return new ProbeOutcome(
            Detail: detail,
            Verdict: (result switch {
                RegisterSignatureResult.Pass => ProbeVerdict.Pass,
                RegisterSignatureResult.Fail => ProbeVerdict.Fail,
                _ => ProbeVerdict.Inconclusive,
            })
        );
    }
}
