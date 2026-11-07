namespace Puck.State.Rules;

/// <summary>What a host proposes into a firing's scope after the firing's first effect sequence proposed cleanly, so
/// the two are one firing: both land and the host's own state lands with them, or neither does.</summary>
/// <remarks>A decision's common effects and its chosen option's effects are the case: the option reads bindings the
/// common effects do not, and the choice must not outlive an option that refused.</remarks>
public interface IFiringContinuation {
    /// <summary>Chooses the effect sequence to propose next, and binds whatever it reads.</summary>
    /// <returns>The effects to propose into the same scope, or <see langword="null"/> when there are none and the
    /// firing proposes only what it has.</returns>
    IRuleEffect[]? Next();
    /// <summary>Releases what <see cref="Next"/> bound, once the sequence it returned has been proposed, however that
    /// ended. An arm the sequence queued keeps the binding it was queued under.</summary>
    void End();
}
