namespace Puck.Commands;

/// <summary>
/// One command's value and edge within a single tick's <see cref="CommandSnapshot"/>. The command is
/// identified by its interned <see cref="CommandId"/> (a stable ordinal, not a string) so the snapshot is
/// compact, hashable, and bit-identical across machines.
/// </summary>
/// <remarks>
/// Construction is INTERNAL by design, for the same reason <see cref="CommandContext"/>'s is. An entry is the
/// credential <see cref="CommandRegistry.ApplySnapshot"/> acts on: its <see cref="Principal"/> becomes the handler's
/// <see cref="CommandContext.Principal"/> verbatim and its <see cref="Text"/> becomes the line that is re-parsed and
/// executed. Anything that could build one could therefore dispatch an authority verb with arguments of its choosing
/// under an identity of its choosing, having passed through neither ingress door. The <see cref="InputRouter"/>'s
/// per-tick mixer is the only builder; every other surface READS an entry.
/// </remarks>
public readonly record struct CommandEntry {
    /// <summary>Initializes a new instance of the <see cref="CommandEntry"/> struct.</summary>
    /// <param name="commandId">The interned command id (<see cref="CommandRegistry.TryGetId"/>).</param>
    /// <param name="value">The command's value for this tick.</param>
    /// <param name="phase">The edge this tick represents.</param>
    /// <param name="dispatch">Whether this entry's handler fires when the snapshot is applied.</param>
    /// <param name="text">The original text line for a simulation-routed console command; <see langword="null"/> for physical input.</param>
    /// <param name="device">The local device that produced this command.</param>
    /// <param name="source">The provider-neutral physical source that produced this command; <see langword="null"/> for injected/synthesized input with no physical control behind it.</param>
    /// <param name="assignedSlot">Whether the physical signal that produced this entry created its device-to-slot assignment.</param>
    /// <param name="principal">The identity acting through this entry, as stamped by its ingress door.</param>
    /// <param name="dispatchWhenMapInactive">Whether this entry releases router-owned state and therefore must pass
    /// a map that closed after the matching press.</param>
    internal CommandEntry(
        ushort commandId,
        CommandValue value,
        CommandPhase phase,
        bool dispatch = true,
        string? text = null,
        InputDeviceId device = default,
        string? source = null,
        bool assignedSlot = false,
        CommandPrincipal principal = default,
        bool dispatchWhenMapInactive = false
    ) {
        AssignedSlot = assignedSlot;
        CommandId = commandId;
        Device = device;
        Dispatch = dispatch;
        DispatchWhenMapInactive = dispatchWhenMapInactive;
        Phase = phase;
        Principal = principal;
        Source = source;
        Text = text;
        Value = value;
    }

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

    // Not public binding data: only the router can assert that this edge unwinds ownership it previously created.
    internal bool DispatchWhenMapInactive { get; init; }

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

    /// <summary>The provider-neutral physical source id that produced this entry (e.g. <c>keyboard.w</c>), or
    /// <see langword="null"/> for an injected/synthesized entry with no physical control behind it. Unlike
    /// <see cref="Device"/>, this is deterministic BINDING vocabulary, not a per-connection identity — a consumer that
    /// must distinguish two DIFFERENT physical controls dispatching the SAME command (e.g. two keys both bound to
    /// one channel) reads this, never <see cref="Device"/>.</summary>
    public string? Source { get; internal init; }

    /// <summary>The original text line for a simulation-routed console command. <see langword="null"/> for physical
    /// input. This is deterministic snapshot payload; it lets argument-bearing verbs execute at their assigned
    /// tick.</summary>
    public string? Text { get; internal init; }

    /// <summary>The command's value for this tick.</summary>
    public CommandValue Value { get; internal init; }

    /// <summary>Whether applying this local live entry releases <see cref="TextCommandSource"/>'s deferred-mutation
    /// drain barrier. Local-only like <see cref="Device"/>; a re-driven entry reconstructs it as <see langword="false"/>.</summary>
    internal bool CompletesTextSubmission { get; init; }
}
