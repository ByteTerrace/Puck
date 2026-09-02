namespace Puck.Commands;

/// <summary>
/// One ingress door for pre-resolved commands, bound at construction to the identity and lane it speaks for. The
/// <see cref="CommandRegistry"/> hands a <see cref="CommandRouting.Simulation"/>-class submitted line to a sink
/// instead of running it inline; the sink stamps its own principal and folds the result into the
/// <see cref="InputRouter"/>'s per-tick <see cref="CommandSnapshot"/>.
/// </summary>
/// <remarks>
/// The public instance is <see cref="InputRouter.ConsoleTextSink"/>. Construction and injection are internal, so a
/// caller may route simulation-class submitted text through the console-bound sink but cannot create a sink carrying
/// a principal of its own choosing.
/// </remarks>
public sealed class CommandInjectionSink {
    private readonly CommandPrincipal m_principal;
    private readonly InputRouter m_router;
    private readonly int m_slot;

    internal CommandInjectionSink(InputRouter router, CommandPrincipal principal, int slot) {
        m_principal = principal;
        m_router = router;
        m_slot = slot;
    }

    /// <summary>Gets the identity every command this sink queues acts as.</summary>
    public CommandPrincipal Principal => m_principal;

    // Queues one pre-resolved command under this sink's bound identity and lane. Internal: the registry's text path is
    // the only producer, and admitting an external one would mean admitting whatever principal it asserted.
    internal void Inject(ushort commandId, CommandValue value, CommandPhase phase, string? text, TextSubmissionBarrier? submissionBarrier = null) {
        m_router.Enqueue(injection: new CommandInjection(
            CommandId: commandId,
            Value: value,
            Phase: phase,
            Origin: CommandOrigin.Text,
            Principal: m_principal,
            Slot: m_slot,
            Text: text
        ) {
            SubmissionBarrier = submissionBarrier,
        });
    }
}
