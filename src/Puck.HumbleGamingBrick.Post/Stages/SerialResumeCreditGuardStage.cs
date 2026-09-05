namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: proves the <see cref="SerialLinkSession"/> resume constructor rejects a resume token whose credit
/// exceeds the machine's own cycle count rather than computing <c>CycleCount − credit</c> with unsigned arithmetic and
/// silently wrapping to a huge target — the GB-side analogue of the console-identity/credit validation
/// <c>AgbLinkSession</c>'s resume constructor already performs. A wrapped target would leave the resumed session
/// chasing a target billions of cycles away instead of failing fast on the mismatched token.
/// </summary>
internal sealed class SerialResumeCreditGuardStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name =>
        "serial-resume-credit-guard";
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var firstRom = SyntheticRom.Create(cartridgeType: 0x00);
        var secondRom = SyntheticRom.Create(cartridgeType: 0x00);

        using var first = PostMachine.Build(
            model: ConsoleModel.DmgC,
            rom: firstRom
        );
        using var second = PostMachine.Build(
            model: ConsoleModel.CgbE,
            rom: secondRom
        );
        // Larger than any freshly booted machine's cycle count could ever satisfy — the token does not fit either
        // machine, the signature a reordered/substituted or otherwise corrupted token leaves behind.
        var bogusToken = new SerialLinkResumeToken(
            FirstCredit: (ulong.MaxValue / 2),
            SecondCredit: 0UL
        );

        ArgumentException? thrown = null;
        SerialLinkSession? session = null;

        try {
            session = new SerialLinkSession(
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

        return PostStageOutcome.Pass(detail: "the resume constructor rejected a credit exceeding the machine's cycle count before it could wrap the pacing target");
    }
}
