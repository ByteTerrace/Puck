namespace Puck.Commands;

/// <summary>Names who a command answers: any stamped principal, or the operator alone.
/// <para>The registry enforces it at its dispatch boundary, before the handler runs, so a handler never has to
/// remember to ask. An operator verb reached by any other principal — a seat's console, a bound input, a schedule
/// step, an addon or a peer — is refused by name, and its handler never runs.</para></summary>
public enum CommandAudience : byte {
    /// <summary>Any stamped principal may run the command; its handler applies whatever authority or disclosure the
    /// command itself owes.</summary>
    Anyone = 0,
    /// <summary>Only <see cref="PrincipalKind.Console"/> may run the command: a developer diagnostic printing
    /// what the evaluation itself computed, which may derive from state another principal is not shown.</summary>
    Operator = 1,
}
