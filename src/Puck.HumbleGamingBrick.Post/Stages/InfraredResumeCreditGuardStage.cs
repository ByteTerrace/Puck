namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: proves the <see cref="IrLinkSession"/> resume constructor rejects a resume token whose credit exceeds
/// the machine's own cycle count rather than computing <c>CycleCount − credit</c> with unsigned arithmetic and silently
/// wrapping to a huge target — the infrared twin of <see cref="SerialResumeCreditGuardStage"/>, since both link
/// sessions share the same credit-preserving resume contract. A wrapped target would leave the resumed session chasing
/// a target billions of cycles away instead of failing fast on the mismatched token. Also proves the rejection happens
/// before either transceiver is wired: after the throw both machines still connect through a plain session, so a
/// rejected token never leaves a port linked with no session left to disconnect it.
/// </summary>
internal sealed class InfraredResumeCreditGuardStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name =>
        "infrared-resume-credit-guard";
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var firstRom = SyntheticRom.Create(cartridgeType: 0x00);
        var secondRom = SyntheticRom.Create(cartridgeType: 0x00);

        using var first = PostMachine.Build(
            model: ConsoleModel.Cgb,
            rom: firstRom
        );
        using var second = PostMachine.Build(
            model: ConsoleModel.Cgb,
            rom: secondRom
        );
        // Larger than any freshly booted machine's cycle count could ever satisfy — the token does not fit either
        // machine, the signature a reordered/substituted or otherwise corrupted token leaves behind. The oversized credit
        // sits on the SECOND side so the guard is proven on both constructor arguments, not just the first.
        var bogusToken = new IrLinkResumeToken(
            FirstCredit: 0UL,
            SecondCredit: (ulong.MaxValue / 2)
        );

        ArgumentException? thrown = null;
        IrLinkSession? session = null;

        try {
            session = new IrLinkSession(
                first: first,
                resumeToken: bogusToken,
                second: second
            );
        } catch (ArgumentException exception) {
            thrown = exception;
        }

        if (thrown is null) {
            session?.Dispose();

            return PostStageOutcome.Fail(detail: "the resume constructor accepted a credit far larger than the machine's cycle count instead of throwing ArgumentException — CycleCount - credit wrapped to a bogus target rather than being rejected");
        }

        if (thrown.ParamName != "resumeToken") {
            return PostStageOutcome.Fail(detail: $"the resume constructor rejected the oversized credit but blamed parameter '{thrown.ParamName}' instead of 'resumeToken'");
        }

        // The rejection must precede the wiring: a plain session over the same two machines must still connect cleanly,
        // which it cannot if the failed resume left either transceiver linked.
        try {
            using var plainSession = new IrLinkSession(
                first: first,
                second: second
            );
        } catch (InvalidOperationException exception) {
            return PostStageOutcome.Fail(detail: $"the rejected resume left a transceiver linked — a plain reconnect over the same machines threw: {exception.Message}");
        }

        return PostStageOutcome.Pass(detail: "the resume constructor rejected a credit exceeding the machine's cycle count before it could wrap the pacing target, and before either transceiver was wired");
    }
}
