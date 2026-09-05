using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: proves <see cref="SerialLinkSession.Suspend"/> refuses a mid-transfer boundary rather than silently
/// severing the cable while a shift is in flight — the same contract <c>AgbLinkSession.Suspend</c> enforces on the AGB
/// side. Arms an internal-clock transfer directly on one port's control register (no ROM execution needed, since the
/// transfer stays armed until the port is ticked), then asserts <see cref="SerialLinkSession.Suspend"/> throws
/// <see cref="InvalidOperationException"/> and leaves the cable connected — never returning a resume token whose credit
/// a caller could trust for a round no console can recover mid-shift. A control leg confirms the identical session
/// still suspends cleanly once the transfer completes (both ports idle).
/// </summary>
internal sealed class SerialSuspendIdleGuardStage : IPostStage<PostContext> {
    private const ushort SerialControlAddress = 0xFF02;
    // TransferActive (bit 7) | ClockSelect (bit 0): an internal-clock transfer, armed and not yet shifted.
    private const byte ArmInternalTransfer = 0x81;

    /// <inheritdoc/>
    public string Name =>
        "serial-suspend-idle-guard";
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

        var session = new SerialLinkSession(
            first: first,
            second: second
        );

        try {
            first.GetRequiredService<ISystemBus>().WriteByte(
                address: SerialControlAddress,
                value: ArmInternalTransfer
            );

            InvalidOperationException? thrown = null;

            try {
                _ = session.Suspend();
            } catch (InvalidOperationException exception) {
                thrown = exception;
            }

            if (thrown is null) {
                return PostStageOutcome.Fail(detail: "Suspend() returned a resume token while the first port's transfer was armed (SC bit 7 set) instead of throwing InvalidOperationException");
            }

            var firstStillArmed = ((first.GetRequiredService<ISystemBus>().ReadByte(address: SerialControlAddress) & 0x80) != 0);

            if (!firstStillArmed) {
                return PostStageOutcome.Fail(detail: "the guarded Suspend() call left the port's transfer disarmed — it must reject before mutating anything, not partially sever the cable");
            }

            // Drain the armed transfer to a transfer-idle instant on both ports, then confirm Suspend() now succeeds.
            session.Run(tCycles: 4_096);

            var firstIdle = ((first.GetRequiredService<ISystemBus>().ReadByte(address: SerialControlAddress) & 0x80) == 0);
            var secondIdle = ((second.GetRequiredService<ISystemBus>().ReadByte(address: SerialControlAddress) & 0x80) == 0);

            if (!(firstIdle && secondIdle)) {
                return PostStageOutcome.Fail(detail: $"the armed transfer never completed within the budget (first idle={firstIdle}, second idle={secondIdle})");
            }

            _ = session.Suspend();

            return PostStageOutcome.Pass(detail: "Suspend() rejected a mid-transfer boundary (armed port, cable left connected) and accepted the identical session once transfer-idle");
        } finally {
            session.Dispose();
        }
    }
}
