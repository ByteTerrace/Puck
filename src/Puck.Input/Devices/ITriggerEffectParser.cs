using Puck.Input.Output;

namespace Puck.Input.Devices;

/// <summary>
/// A controller that applies typed adaptive-trigger effects (the DualSense). The two triggers are independent, so
/// the effect for L2 and R2 is supplied separately; a parser composes them into its normal output report rather
/// than a separate raw write. Implementing this interface advertises
/// <see cref="Output.GamepadOutputCapabilities.TriggerEffect"/>.
/// </summary>
public interface ITriggerEffectParser {
    /// <summary>Applies an adaptive-trigger effect to each trigger.</summary>
    /// <param name="left">The effect for the left trigger (L2).</param>
    /// <param name="right">The effect for the right trigger (R2).</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>A task that completes when the report has been written.</returns>
    ValueTask SetTriggerEffectAsync(TriggerEffectSpec left, TriggerEffectSpec right, CancellationToken cancellationToken = default);
}
