namespace Puck.HumbleGamingBrick;

/// <summary>The pacing contract <see cref="IrLinkSession"/> and <see cref="SerialLinkSession"/> share, independent of
/// the medium connecting the pair: the furthest-behind interleave that advances two linked machines through a budget
/// — always step whichever machine is further behind its own cumulative target, one instruction at a time, ties going
/// to the first machine, a fixed, state-free rule that depends on nothing but the two machines' states and the budget,
/// so a linked run is deterministic and replay-identical — and the guard a resume token's credit must pass before it
/// re-anchors a target.</summary>
internal static class LinkSessionStepper {
    /// <summary>Returns <paramref name="instance"/> after checking that a resume token's <paramref name="credit"/> fits
    /// inside the machine's own cycle count. Call as a constructor-initializer argument, before the plain constructor
    /// connects either port: once a port is wired and construction has thrown there is no seam left to disconnect it
    /// through.</summary>
    /// <param name="instance">The machine the credit re-anchors.</param>
    /// <param name="credit">The resume token's credit for that machine.</param>
    /// <param name="side">The token side named in the failure message: <c>"first"</c> or <c>"second"</c>.</param>
    /// <returns><paramref name="instance"/>, unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="instance"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="credit"/> exceeds the machine's cycle count — a token that
    /// does not fit the machine, the signature a reordered, substituted, or otherwise corrupted token leaves behind;
    /// <c>CycleCount − credit</c> is unsigned and would otherwise wrap to a target billions of cycles away.</exception>
    public static MachineInstance RequireCreditFits(MachineInstance instance, ulong credit, string side) {
        ArgumentNullException.ThrowIfNull(argument: instance);

        RequireCreditFits(
            credit: credit,
            cycleCount: instance.Machine.Clock.CycleCount,
            side: side
        );

        return instance;
    }
    /// <summary>Checks that a credit fits inside a machine's own cycle count — the raw-value form, for a re-anchor that
    /// already holds the cycle count rather than the instance.</summary>
    /// <param name="cycleCount">The machine's current cycle count.</param>
    /// <param name="credit">The credit re-anchoring that machine's pacing target.</param>
    /// <param name="side">The token side named in the failure message: <c>"first"</c> or <c>"second"</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="credit"/> exceeds <paramref name="cycleCount"/>;
    /// <c>cycleCount − credit</c> is unsigned and would otherwise wrap to a target billions of cycles away.</exception>
    public static void RequireCreditFits(ulong cycleCount, ulong credit, string side) {
        if (credit > cycleCount) {
            throw new ArgumentException(
                message: $"the resume token's {side} credit ({credit}) exceeds the machine's cycle count ({cycleCount}).",
                paramName: "resumeToken"
            );
        }
    }
    /// <summary>Advances both machines forward by a shared budget of T-cycles (dots), interleaved deterministically.</summary>
    /// <param name="first">The first machine (the tie-break winner when both are equally behind).</param>
    /// <param name="firstTarget">The first machine's cumulative pacing target; advanced by <paramref name="tCycles"/>
    /// on entry, then left at or below the first machine's cycle count.</param>
    /// <param name="second">The second machine.</param>
    /// <param name="secondTarget">The second machine's cumulative pacing target, mirroring <paramref name="firstTarget"/>.</param>
    /// <param name="tCycles">The number of T-cycles to advance each machine this call.</param>
    public static void Run(Machine first, ref ulong firstTarget, Machine second, ref ulong secondTarget, ulong tCycles) {
        firstTarget += tCycles;
        secondTarget += tCycles;

        while (true) {
            var firstRemaining = Remaining(
                machine: first,
                target: firstTarget
            );
            var secondRemaining = Remaining(
                machine: second,
                target: secondTarget
            );

            if (
                (firstRemaining == 0UL) &&
                (secondRemaining == 0UL)
            ) {
                return;
            }

            if (firstRemaining >= secondRemaining) {
                StepOnce(machine: first);
            } else {
                StepOnce(machine: second);
            }
        }
    }

    private static ulong Remaining(Machine machine, ulong target) {
        var elapsed = machine.Clock.CycleCount;

        return ((elapsed < target)
            ? (target - elapsed)
            : 0UL);
    }
    private static void StepOnce(Machine machine) {
        if (machine.HasBusMaster) {
            machine.StepInstruction();
        } else {
            machine.StepTick();
        }
    }
}
