namespace Puck.Commands;

/// <summary>
/// One command's value and edge within a single tick's <see cref="CommandSnapshot"/>. The command is
/// identified by its interned <see cref="CommandId"/> (a stable ordinal, not a string) so the snapshot is
/// compact, hashable, and bit-identical across machines.
/// </summary>
/// <remarks>
/// Construction is INTERNAL by design, for the same reason <see cref="CommandContext"/>'s is. An entry is the
/// credential <see cref="CommandRegistry.ApplySnapshot"/> acts on: its <see cref="Principal"/> becomes the handler's
/// <see cref="CommandContext.Principal"/> verbatim and its <see cref="Text"/> becomes the line that is decoded and
/// executed. Anything that could build one could therefore dispatch an authority verb with arguments of its choosing
/// under an identity of its choosing, having passed through neither ingress door. The <see cref="InputRouter"/>'s
/// per-tick mixer is the only builder; every other surface READS an entry.
/// </remarks>
public readonly record struct CommandEntry {
    /// <summary>Initializes a new instance of the <see cref="CommandEntry"/> struct.</summary>
    /// <param name="commandId">The interned command id (<see cref="CommandRegistry.TryGetId"/>).</param>
    /// <param name="value">The command's value for this tick.</param>
    /// <param name="phase">The edge this tick represents.</param>
    /// <param name="origin">How the command entered the command pipeline.</param>
    /// <param name="dispatch">Whether this entry's handler fires when the snapshot is applied.</param>
    /// <param name="text">The submitted line for a simulation-routed console command or an argument-bearing binding
    /// press; otherwise <see langword="null"/>.</param>
    /// <param name="device">The local device that produced this command.</param>
    /// <param name="source">The deterministic binding owner: a provider-neutral physical source, or a stable
    /// synthesized destination id for a chord or toggle. <see langword="null"/> for non-binding injections.</param>
    /// <param name="assignedSlot">Whether the physical signal that produced this entry created its device-to-slot assignment.</param>
    /// <param name="principal">The identity acting through this entry, as stamped by its ingress door.</param>
    internal CommandEntry(
        ushort commandId,
        CommandValue value,
        CommandPhase phase,
        CommandOrigin origin,
        bool dispatch = true,
        string? text = null,
        InputDeviceId device = default,
        string? source = null,
        bool assignedSlot = false,
        CommandPrincipal principal = default
    ) {
        AssignedSlot = assignedSlot;
        CommandId = commandId;
        Device = device;
        Dispatch = dispatch;
        Origin = origin;
        Phase = phase;
        Principal = principal;
        Source = source;
        Text = text;
        Value = value;
    }

    /// <summary>Whether applying this local live entry releases <see cref="TextCommandSource"/>'s deferred-mutation
    /// drain barrier. Local-only like <see cref="Device"/>; a re-driven entry reconstructs it as <see langword="false"/>.</summary>
    internal bool CompletesTextSubmission { get; init; }
    internal TextSubmissionBarrier? SubmissionBarrier { get; init; }

    /// <summary>Whether the physical signal that produced this entry created its device-to-slot assignment. Unlike
    /// <see cref="Device"/>, this is deterministic snapshot semantics so a first-seat gesture is consumed identically
    /// wherever the same input stream is re-driven.</summary>
    public bool AssignedSlot { get; internal init; }
    /// <summary>The interned command id (<see cref="CommandRegistry.TryGetId"/>).</summary>
    public ushort CommandId { get; internal init; }
    /// <summary>
    /// The local device that produced this command, for output handlers that act on the originating controller
    /// (e.g. rumble). This is a <em>local-only</em> annotation: it is excluded from the deterministic identity and
    /// must not be hashed — the lane's slot is the cross-machine identity.
    /// </summary>
    public InputDeviceId Device { get; internal init; }
    /// <summary>Whether this entry's handler fires when the snapshot is applied. Held digitals reassert with
    /// this <see langword="false"/>, while continuous analog routes re-dispatch; bindings that explicitly activate on
    /// release carry <see langword="true"/> on their completed edge.</summary>
    public bool Dispatch { get; internal init; }
    /// <summary>How this command entered the command pipeline. This is deterministic snapshot content and remains
    /// meaningful when a binding is synthesized and therefore has no <see cref="Source"/>.</summary>
    public CommandOrigin Origin { get; internal init; }
    /// <summary>The edge this tick represents: <see cref="CommandPhase.Started"/> / <see cref="CommandPhase.Active"/>
    /// (held) / <see cref="CommandPhase.Completed"/>.</summary>
    public CommandPhase Phase { get; internal init; }
    /// <summary>
    /// The identity acting through this entry. An injected entry carries the principal its sink was CONSTRUCTED with;
    /// every other entry is stamped at snapshot assembly from the lane's <see cref="ICommandPrincipalResolver"/> answer.
    /// An entry that reaches dispatch carrying <see cref="CommandPrincipalKind.Unspecified"/> found a path that skipped
    /// both doors.
    /// </summary>
    public CommandPrincipal Principal { get; internal init; }
    /// <summary>The deterministic binding owner that produced this entry: a provider-neutral physical source id
    /// (e.g. <c>keyboard.w</c>) or a stable synthesized destination id for a chord or toggle. It is
    /// <see langword="null"/> for an injection with no binding owner. Unlike
    /// <see cref="Device"/>, this is deterministic BINDING vocabulary, not a per-connection identity — a consumer that
    /// must distinguish independent contributions dispatching commands reads this, never <see cref="Device"/>.
    /// Use <see cref="Origin"/> for ingress.</summary>
    public string? Source { get; internal init; }
    /// <summary>The submitted line for a simulation-routed console command or an argument-bearing binding press;
    /// otherwise <see langword="null"/>. This is deterministic snapshot payload; it lets argument-bearing verbs
    /// execute at their assigned tick.</summary>
    public string? Text { get; internal init; }
    /// <summary>The command's value for this tick.</summary>
    public CommandValue Value { get; internal init; }

    /// <summary>Compares deterministic entry content. Process-local annotations are deliberately excluded.</summary>
    public bool Equals(CommandEntry other) {
        return (
            (AssignedSlot == other.AssignedSlot) &&
            (CommandId == other.CommandId) &&
            (Dispatch == other.Dispatch) &&
            (Origin == other.Origin) &&
            (Phase == other.Phase) &&
            Principal.Equals(other: other.Principal) &&
            string.Equals(
            a: Source,
            b: other.Source,
            comparisonType: StringComparison.Ordinal
        ) &&
            string.Equals(
            a: Text,
            b: other.Text,
            comparisonType: StringComparison.Ordinal
        ) &&
            Value.Equals(other: other.Value)
        );
    }
    /// <inheritdoc/>
    public override int GetHashCode() {
        var hash = new HashCode();

        hash.Add(value: AssignedSlot);
        hash.Add(value: CommandId);
        hash.Add(value: Dispatch);
        hash.Add(value: Origin);
        hash.Add(value: Phase);
        hash.Add(value: Principal);
        hash.Add(
            value: Source,
            comparer: StringComparer.Ordinal
        );
        hash.Add(
            value: Text,
            comparer: StringComparer.Ordinal
        );
        hash.Add(value: Value);

        return hash.ToHashCode();
    }
}
