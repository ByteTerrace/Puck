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

        return (document with { ViewsRaw = document.Views with { SeatControl = control } });
    }

    [Fact]
    public void FollowRate_NonPositive_Refuses_ControlPositiveClean() {
        Laws.RefusalWithControl(
            lawId: "views.seatControl.follow-rate-positive",
            deniedOutcome: static () => TryValidate(definition: WithControl(control: new WorldSeatViewControl(YawReference: WorldSeatYawReference.World, MinPitch: -0.5f, MaxPitch: 1f, Follow: new WorldSeatFollow(Rate: 0f)))),
            controlOutcome: static () => TryValidate(definition: WithControl(control: new WorldSeatViewControl(YawReference: WorldSeatYawReference.World, MinPitch: -0.5f, MaxPitch: 1f, Follow: new WorldSeatFollow(Rate: 4f))))
        );
    }
    [Fact]
    public void FollowUnderABodyYawReference_Refuses_ControlWorldClean() {
        Laws.RefusalWithControl(
            lawId: "views.seatControl.follow-needs-world-yaw",
            deniedOutcome: static () => TryValidate(definition: WithControl(control: new WorldSeatViewControl(YawReference: WorldSeatYawReference.Body, MinPitch: -0.5f, MaxPitch: 1f, Follow: new WorldSeatFollow(Rate: 4f)))),
            controlOutcome: static () => TryValidate(definition: WithControl(control: new WorldSeatViewControl(YawReference: WorldSeatYawReference.World, MinPitch: -0.5f, MaxPitch: 1f, Follow: new WorldSeatFollow(Rate: 4f))))
        );
    }
}
