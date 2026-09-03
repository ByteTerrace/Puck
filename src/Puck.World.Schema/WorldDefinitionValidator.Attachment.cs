namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // Absent (Enabled false) validates nothing beyond its own field — every numeric field of an unauthored section
    // reads WorldAttachmentSection.Absent's own inert zeros, never authored values a world never wrote.
    private static void ValidateAttachment(WorldAttachmentSection attachment, ISet<string> channelNames, List<string> errors) {
        if (!attachment.Enabled) {
            return;
        }

        const string Path = "attachment";

        RequireNonNegative(
            errors: errors,
            name: $"{Path}.grappleMaxDistance",
            value: attachment.GrappleMaxDistance
        );
        RequireRange(
            errors: errors,
            max: 180f,
            min: 0f,
            name: $"{Path}.grappleAssistHalfAngleDegrees",
            value: attachment.GrappleAssistHalfAngleDegrees
        );
        RequireNonNegative(
            errors: errors,
            name: $"{Path}.reelRate",
            value: attachment.ReelRate
        );
        RequireNonNegative(
            errors: errors,
            name: $"{Path}.reelInFloor",
            value: attachment.ReelInFloor
        );
        RequireNonNegative(
            errors: errors,
            name: $"{Path}.releaseMomentumScale",
            value: attachment.ReleaseMomentumScale
        );

        // Every channel name is OPTIONAL (a null lane is simply unreachable), but an AUTHORED one must resolve —
        // the same "declared or the field is pointless" door a kit's own speed.held channel already opens on its
        // motion row.
        if (attachment.AttachChannel is { Length: > 0 } attachChannel) {
            _ = RequireDeclared(
                declaredSet: channelNames,
                errors: errors,
                field: string.Empty,
                path: $"{Path}.attachChannel",
                rowNoun: "channel",
                value: attachChannel
            );
        }
        if (attachment.DetachChannel is { Length: > 0 } detachChannel) {
            _ = RequireDeclared(
                declaredSet: channelNames,
                errors: errors,
                field: string.Empty,
                path: $"{Path}.detachChannel",
                rowNoun: "channel",
                value: detachChannel
            );
        }
        if (attachment.ReelChannel is { Length: > 0 } reelChannel) {
            _ = RequireDeclared(
                declaredSet: channelNames,
                errors: errors,
                field: string.Empty,
                path: $"{Path}.reelChannel",
                rowNoun: "channel",
                value: reelChannel
            );
        }
    }
}
