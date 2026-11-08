namespace Puck.Commands;

/// <summary>Names who a command answers: any stamped principal, a local ingress, or the operator alone.
/// <para>The registry enforces it at its dispatch boundary, before the handler runs, so a handler never has to
/// remember to ask. A verb reached by a principal its audience does not admit (<see cref="CommandAudienceExtensions.Admits"/>)
/// — a seat's console, a bound input, a schedule step, an addon or a peer, as the audience says — is refused by name,
/// and its handler never runs.</para>
/// <para>Every registration declares one: the factories take no default, and the registry refuses
/// <see cref="Unspecified"/> by name, so a new verb is never readable by every principal because nobody chose.</para></summary>
public enum CommandAudience : byte {
    /// <summary>No audience was declared; the registry refuses the registration and admits no principal.</summary>
    Unspecified = 0,
    /// <summary>Any stamped principal may run the command. Its answer to a recipient (anyone but the operator or the
    /// world) derives only from that recipient's view: no text, count, tick or byte length of it may depend on a row
    /// the recipient cannot read whole.</summary>
    Anyone = 1,
    /// <summary>Only <see cref="PrincipalKind.Console"/> may run the command: a developer diagnostic printing
    /// what the evaluation itself computed, which may derive from state another principal is not shown.</summary>
    Operator = 2,
    /// <summary>Only a local ingress may run the command: <see cref="PrincipalKind.Console"/> or a
    /// <see cref="PrincipalKind.Seat"/>, never an addon, a peer, a session or the world's own program. For a verb that
    /// touches the machine it runs on (writes local files, watches local sources), which a remote or authored
    /// principal must never reach. A seat's answer owes the same disclosure as <see cref="Anyone"/>'s.</summary>
    Local = 3,
}
/// <summary>The one predicate behind <see cref="CommandAudience"/>: the registry's dispatch check and every reader
/// that asks which verbs a principal may run (an authored command's check, a fuzzer's verb draw) call it.</summary>
public static class CommandAudienceExtensions {
    /// <summary>Returns whether an audience admits a principal.</summary>
    /// <param name="audience">The command's audience.</param>
    /// <param name="principal">The acting principal.</param>
    /// <returns><see langword="true"/> when the principal may run a command with this audience.</returns>
    public static bool Admits(this CommandAudience audience, Principal principal) => audience switch {
        CommandAudience.Anyone => true,
        CommandAudience.Operator => (principal.Kind == PrincipalKind.Console),
        CommandAudience.Local => (principal.Kind is PrincipalKind.Console or PrincipalKind.Seat),
        _ => false,
    };
    /// <summary>Describes, for a refusal, who an audience admits.</summary>
    /// <param name="audience">The command's audience.</param>
    /// <returns>A short clause: <c>an operator verb</c>, <c>a local verb</c>, or <c>a verb</c>.</returns>
    public static string Describe(this CommandAudience audience) => audience switch {
        CommandAudience.Operator => "an operator verb",
        CommandAudience.Local => "a local verb, for the console or a local seat",
        _ => "a verb",
    };
}
