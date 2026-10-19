namespace Puck.HumbleGamingBrick.Post;

/// <summary>Dispatches a <see cref="SuiteCase"/> to its <see cref="ProbeKind"/>'s implementation and normalizes the
/// result to one shared <see cref="ProbeOutcome"/> shape, so <see cref="SuiteEvaluator"/> never branches on probe kind.</summary>
internal static class ProbeRunner {
    private static ProbeOutcome RunAcceptance(SuiteCase suiteCase) {
        var (passed, detail) = AcceptanceRomProbe.Run(
            romCase: ToRomCase(suiteCase: suiteCase)
        );

        return new ProbeOutcome(
            Detail: detail,
            Verdict: ((passed == true)
            ? ProbeVerdict.Pass
            : ((passed == false)
                ? ProbeVerdict.Fail
                : ProbeVerdict.Inconclusive))
        );
    }
    private static ProbeOutcome RunRomProbe<TResult>(
        SuiteCase suiteCase,
        Func<RomCase, (TResult Result, string Detail)> run,
        Func<TResult, ProbeVerdict> toVerdict
    ) {
        var (result, detail) = run(ToRomCase(suiteCase: suiteCase));

        return new ProbeOutcome(
            Detail: detail,
            Verdict: toVerdict(result)
        );
    }
    private static ProbeOutcome RunConformance(SuiteCase suiteCase) =>
        RunRomProbe<ConformanceRomResult>(
            suiteCase: suiteCase,
            run: ConformanceRomProbe.Run,
            toVerdict: static result => result switch {
                ConformanceRomResult.Pass => ProbeVerdict.Pass,
                ConformanceRomResult.Fail => ProbeVerdict.Fail,
                _ => ProbeVerdict.Inconclusive,
            }
        );
    private static ProbeOutcome RunGbMicrotest(SuiteCase suiteCase) =>
        RunRomProbe<GbMicrotestResult>(
            suiteCase: suiteCase,
            run: GbMicrotestProbe.Run,
            toVerdict: static result => result switch {
                GbMicrotestResult.Pass => ProbeVerdict.Pass,
                GbMicrotestResult.Fail => ProbeVerdict.Fail,
                _ => ProbeVerdict.Inconclusive,
            }
        );
    private static ProbeOutcome RunRegisterSignature(SuiteCase suiteCase) =>
        RunRomProbe<RegisterSignatureResult>(
            suiteCase: suiteCase,
            run: RegisterSignatureProbe.Run,
            toVerdict: static result => result switch {
                RegisterSignatureResult.Pass => ProbeVerdict.Pass,
                RegisterSignatureResult.Fail => ProbeVerdict.Fail,
                _ => ProbeVerdict.Inconclusive,
            }
        );
    private static RomCase ToRomCase(SuiteCase suiteCase) =>
        new(
            FrameCap: suiteCase.FrameCap,
            FullPath: suiteCase.FullPath,
            Group: suiteCase.Suite,
            Model: suiteCase.Model,
            Name: Path.GetFileNameWithoutExtension(path: suiteCase.FullPath)
        );

    /// <summary>Runs a runnable case's probe.</summary>
    /// <param name="suiteCase">The case to run.</param>
    /// <returns>The normalized outcome.</returns>
    public static ProbeOutcome Run(SuiteCase suiteCase) {
        return suiteCase.Probe switch {
            ProbeKind.ConformanceSerial => RunConformance(
            suiteCase: suiteCase
        ),
            ProbeKind.AcceptanceFibonacci => RunAcceptance(
            suiteCase: suiteCase
        ),
            ProbeKind.RegisterSignature => RunRegisterSignature(
            suiteCase: suiteCase
        ),
            ProbeKind.GbMicrotest => RunGbMicrotest(
            suiteCase: suiteCase
        ),
            ProbeKind.Screenshot => ScreenshotProbe.Run(
            suiteCase: suiteCase
        ),
            ProbeKind.HexPattern => HexPatternProbe.Run(
            suiteCase: suiteCase
        ),
            ProbeKind.Audio => AudioProbe.Run(
            suiteCase: suiteCase
        ),
            _ => throw new NotSupportedException(message: $"Unhandled probe kind '{suiteCase.Probe}'."),
        };
    }
}
