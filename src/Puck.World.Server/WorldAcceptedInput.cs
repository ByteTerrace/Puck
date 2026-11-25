using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The authority's derived body fold after timed admission or an already admitted body route.
/// This is not a transport or replay submission; held values are derived from the semantic input mapping.</summary>
public readonly record struct WorldAcceptedInput(ulong Tick, int EntityIndex, PlayerIntent Intent, Principal Principal,
    PlayerIntent HeldChannels = default, int MeasuredHoldTicks = 0) {
    /// <summary>Folds an admitted submission onto the channels of the body it drives: each channel of the input mapping
    /// lands on the body's channel of the same name, a relative source as movement since the last batch, a role
    /// channel as the composed motion request quantized to the body's channel, and every other channel as a held
    /// device level. The one fold every body input passes, the authority's and a client's prediction of its own body
    /// alike.</summary>
    /// <param name="submission">The admitted submission.</param>
    /// <param name="mapping">The channels of the input mapping the submission's batch names.</param>
    /// <param name="target">The channels of the body it drives.</param>
    /// <returns>The body's input.</returns>
    public static WorldAcceptedInput Fold(in IntentSubmission submission, WorldChannelTable mapping, WorldChannelTable target) {
        var input = submission.Input;
        var movement = input.Image with { Channels = default };
        var held = default(PlayerIntent);
        var remap = mapping.RemapTo(target: target);

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            var destination = remap[ordinal];

            if (destination < 0) { continue; }
            if (mapping.Shape(ordinal: ordinal) == ChannelShape.Relative) { movement = movement.WithRelative(ordinal: destination, value: input.Image.Channels.Relative(ordinal: ordinal)); } else if (mapping.IsRole(ordinal: ordinal)) { movement = movement.WithChannel(ordinal: destination, value: target.Quantize(ordinal: destination, value: submission.Intent[ordinal])); } else { held = held.WithChannel(ordinal: destination, value: input.Image[ordinal]); }
        }

        return new(submission.Tick, submission.EntityIndex, movement, submission.Principal, held, submission.MeasuredHoldTicks);
    }
    /// <summary>Returns this input with every relative channel of its body at rest: the input held again on a tick no
    /// newer batch arrived, whose movement since the last batch was already taken.</summary>
    /// <param name="target">The channels of the body the input drives.</param>
    /// <returns>The held input.</returns>
    public WorldAcceptedInput WithoutRelative(WorldChannelTable target) {
        var intent = Intent;
        var remaining = target.RelativeMask;

        while (remaining != 0) {
            var ordinal = System.Numerics.BitOperations.TrailingZeroCount(value: remaining);

            remaining &= (remaining - 1);
            intent = intent.WithRelative(ordinal: ordinal, value: default);
        }

        return this with { Intent = intent };
    }
}
