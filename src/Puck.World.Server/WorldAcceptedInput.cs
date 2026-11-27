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
    /// <summary>Returns a remote owner's packet as an authority admits it on a tick for the body it drives: stamped with
    /// that tick, that body and that owner. The one rewrite a federated packet takes, at the authority and in a client's
    /// prediction of its own body alike.</summary>
    /// <param name="packet">The packet as sent.</param>
    /// <param name="tick">The authority tick it is admitted on.</param>
    /// <param name="entityIndex">The body's 0-based population index.</param>
    /// <param name="principal">The body's owner.</param>
    /// <returns>The admitted submission.</returns>
    public static IntentSubmission Admitted(in IntentSubmission packet, ulong tick, int entityIndex, Principal principal) =>
        packet with { Tick = tick, EntityIndex = entityIndex, Principal = principal, Input = packet.Input with { Tick = tick } };
    /// <summary>Returns the submission an authority takes again on each tick no newer packet of its owner arrives: the
    /// last one admitted, with every relative channel of its mapping at rest in both its held image and its intent, its
    /// events gone and every relative source at rest, since the movement and the presses it carried were already taken.
    /// The one held input, at the authority and in a client's prediction of its own body alike.</summary>
    /// <param name="admitted">The last admitted submission.</param>
    /// <param name="catalog">The input catalog its mapping belongs to.</param>
    /// <returns>The held submission.</returns>
    public static IntentSubmission HeldRepeat(in IntentSubmission admitted, WorldInputCatalog catalog) {
        ArgumentNullException.ThrowIfNull(argument: catalog);

        var held = admitted.Input.Image;
        var projected = admitted.Intent;
        var mapping = catalog.Mappings[admitted.Input.Mapping];

        foreach (var logical in mapping.Live) {
            if (catalog.Channels[logical].Shape != ChannelShape.Relative) { continue; }
            var address = mapping.Address(logicalId: logical);

            held = held.WithRelative(ordinal: address, value: default);
            projected = projected.WithRelative(ordinal: address, value: default);
        }

        return admitted with {
            Intent = projected,
            Input = admitted.Input with { Image = held, Events = default, Contributions = AtRest(contributions: admitted.Input.Contributions) },
        };
    }
    // The sources with every relative source at rest: a relative source reports movement since the last batch, which a
    // held image must not repeat. Copies only when one moved.
    private static ReadOnlyMemory<WorldInputContribution> AtRest(ReadOnlyMemory<WorldInputContribution> contributions) {
        var moved = false;

        foreach (var contribution in contributions.Span) {
            if ((contribution.Kind == WorldInputContributionKind.Relative) && (contribution.RawValue != 0)) {
                moved = true;
                break;
            }
        }
        if (!moved) { return contributions; }

        var copy = contributions.ToArray();

        for (var source = 0; (source < copy.Length); source++) {
            if (copy[source].Kind == WorldInputContributionKind.Relative) { copy[source] = copy[source] with { RawValue = 0 }; }
        }

        return copy;
    }
}
