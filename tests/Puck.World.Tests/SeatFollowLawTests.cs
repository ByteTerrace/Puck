using Xunit;

namespace Puck.World.Tests;

/// <summary>The follow camera (<c>views.seatControl.follow</c>): a positive finite rate, and only beside a World yaw
/// reference — a body-relative yaw already rides the body.</summary>
public sealed class SeatFollowLawTests {
    private static bool TryValidate(WorldDefinition definition) => WorldDefinitionValidator.TryValidate(
        definition: definition,
        neighbours: null,
        reason: out _
    );
    private static WorldDefinition WithControl(WorldSeatViewControl control) {
        var document = Fixtures.BuildDocument();

        return (document with { ViewsRaw = document.Views with { SeatControlRaw = control } });
    }

    // A follow needs a positive rate and the world yaw reference; each denial breaks one, and the control is the
    // world-referenced follow at rate four.
    [InlineData("views.seatControl.follow-rate-positive", WorldSeatYawReference.World, 0f)]
    [InlineData("views.seatControl.follow-needs-world-yaw", WorldSeatYawReference.Body, 4f)]
    [Theory]
    public void AnUnfollowableSeatControl_Refuses_ControlWorldRateFourClean(string lawId, WorldSeatYawReference yawReference, float rate) {
        static WorldDefinition With(WorldSeatYawReference yawReference, float rate) => WithControl(control: new WorldSeatViewControl(
            YawReference: yawReference,
            MinPitch: -0.5f,
            MaxPitch: 1f,
            Follow: new WorldSeatFollow(Rate: rate)
        ));

        Laws.RefusalWithControl(
            lawId: lawId,
            deniedOutcome: () => TryValidate(definition: With(
                rate: rate,
                yawReference: yawReference
            )),
            controlOutcome: static () => TryValidate(definition: With(
                rate: 4f,
                yawReference: WorldSeatYawReference.World
            ))
        );
    }
}
