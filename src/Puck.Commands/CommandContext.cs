using System.CommandLine;

namespace Puck.Commands;

/// <summary>
/// Carries the per-invocation state passed to a command handler, unifying the text-parsing and
/// source-driven activation paths behind a single signature.
/// </summary>
/// <remarks>
/// Construction is INTERNAL by design. A context is the credential a handler acts on — it carries the
/// <see cref="Principal"/> an ingress door stamped — so anything that could build one could invoke an authority
/// handler under an identity of its own choosing. The <see cref="CommandRegistry"/> and the snapshot mixer are its
/// only builders; every other surface reads a command through <see cref="CommandMetadata"/> instead.
/// </remarks>
public readonly record struct CommandContext {
    /// <summary>Initializes a new instance of the <see cref="CommandContext"/> struct.</summary>
    /// <param name="value">The command value for this invocation.</param>
    /// <param name="phase">The transition this invocation represents.</param>
    /// <param name="origin">How the invocation entered the command pipeline.</param>
    /// <param name="parse">The parse result when full parsing was required; <see langword="null"/> for bindings and
    /// wire-native text.</param>
    /// <param name="text">An optional text payload supplied by the activation.</param>
    /// <param name="registry">The registry that dispatched the invocation.</param>
    /// <param name="deviceId">The device that produced the activation, for source-driven input.</param>
    /// <param name="source">The provider-neutral physical source that produced the activation; <see langword="null"/> for text/injected invocations with no physical control behind them.</param>
    /// <param name="slot">The logical player lane that owns the invocation.</param>
    /// <param name="assignedSlot">Whether this invocation's physical signal created its device-to-slot assignment.</param>
    /// <param name="principal">The identity acting through this invocation, as stamped by its ingress door.</param>
    internal CommandContext(
        CommandValue value,
        CommandPhase phase,
        CommandOrigin origin,
        ParseResult? parse,
        string? text = null,
        CommandRegistry? registry = null,
        InputDeviceId deviceId = default,
        string? source = null,
        int slot = 0,
        bool assignedSlot = false,
        CommandPrincipal principal = default
    ) {
        AssignedSlot = assignedSlot;
        DeviceId = deviceId;
        Origin = origin;
        Parse = parse;
        Phase = phase;
        Principal = principal;
        Registry = registry;
        Slot = slot;
        Source = source;
        Text = text;
        Value = value;
    }

    /// <summary>Whether this invocation's physical signal created its device-to-slot assignment. Snapshot-driven
    /// handlers use this deterministic bit to distinguish a first-seat gesture from an ordinary action; it remains
    /// valid during replay even though <see cref="DeviceId"/> is local-only.</summary>
    public bool AssignedSlot { get; internal init; }

    /// <summary>The device that produced the activation (for source-driven input), letting a handler target the
    /// specific device — e.g. rumbling the controller that pressed the button. <see langword="default"/> for the
    /// text path.</summary>
    public InputDeviceId DeviceId { get; internal init; }

    /// <summary>How this invocation entered the command pipeline. Use this, rather than the presence of
    /// <see cref="Source"/>, to distinguish submitted text from an authored binding: a presentation-driven or
    /// synthesized binding has no physical source but is still a binding.</summary>
    public CommandOrigin Origin { get; internal init; }

    /// <summary>The parse result when submitted text required full parsing. This is also <see langword="null"/> for
    /// wire-native text, so it describes parser work rather than command origin.</summary>
    public ParseResult? Parse { get; internal init; }

    /// <summary>The transition this invocation represents.</summary>
    public CommandPhase Phase { get; internal init; }

    /// <summary>The identity ACTING through this invocation, stamped by the ingress door that produced it: the text
    /// door stamps <see cref="CommandPrincipal.Console"/>, the snapshot mixer stamps the lane's resolved principal
    /// (<see cref="ICommandPrincipalResolver"/>), and an injection sink stamps the identity it was constructed with.
    /// A handler READS this to attribute its action; a handler that constructs a principal instead is asserting an
    /// identity rather than carrying one.</summary>
    public CommandPrincipal Principal { get; internal init; }

    /// <summary>The registry that dispatched the invocation, allowing handlers to query or affect command state.
    /// May be <see langword="null"/> when no registry context is available.</summary>
    public CommandRegistry? Registry { get; internal init; }

    /// <summary>The provider-neutral physical source id that produced this invocation (e.g. <c>keyboard.w</c>), or
    /// <see langword="null"/> when no physical control is behind it. Unlike
    /// <see cref="DeviceId"/> (a per-connection identity, excluded from simulation state), this is deterministic
    /// binding vocabulary — the field a handler reads only when it must distinguish two DIFFERENT physical controls
    /// dispatching the SAME command (e.g. two keys bound to one channel). Use <see cref="Origin"/> for ingress.</summary>
    public string? Source { get; internal init; }

    /// <summary>The stable logical player lane that owns the invocation. Snapshot-driven handlers must use this,
    /// rather than <see cref="DeviceId"/>, when choosing simulation state: device identities are local annotations
    /// and are not serialized into recordings. The immediate/text path defaults to slot <c>0</c>.</summary>
    public int Slot { get; internal init; }

    /// <summary>An optional text payload supplied by the activation.</summary>
    public string? Text { get; internal init; }

    /// <summary>The command value for this invocation.</summary>
    public CommandValue Value { get; internal init; }
}
