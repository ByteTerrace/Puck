using System.Numerics;
using Puck.Commands;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for a camera drag's pointer motion: it counts from the press that starts the drag, even before the
/// router applies the press, and stops at the release that ends it, even before the router applies the release.</summary>
public sealed class WorldDragMotionLawTests {
    private sealed class Drag {
        public readonly WorldPointer Pointer = new();
        public bool Steering;
        public Vector2 Turned;
        public readonly WorldDragMotion Motion;

        public Drag() => Motion = new WorldDragMotion(
            pointer: Pointer,
            steering: _ => Steering,
            turn: (_, motion) => Turned += motion
        );

        // The router applies a drag command's edge at a tick, as player.orbit's handler does.
        public void Apply(CommandPhase phase, int slot = 0) => WorldDragMotion.Hold(
            drag: Motion,
            held: held => Steering = held,
            phase: phase,
            slot: slot
        );

        public void Button(bool down) {
            Pointer.SetButton(
                button: 0,
                down: down,
                slot: 0
            );
            Motion.OnPointer(slot: 0);
        }
        public void Move(float x) {
            Pointer.AddMotion(
                delta: new Vector2(
                    x: x,
                    y: 0f
                ),
                slot: 0
            );
            Motion.OnPointer(slot: 0);
        }
    }

    [Fact]
    public void ADragCountsFromItsPressAndStopsAtItsRelease() {
        var drag = new Drag();

        // Motion with no button down, or before the press, never counts.
        drag.Move(x: 7f);
        drag.Button(down: true);
        drag.Move(x: 100f);
        Assert.Equal(expected: Vector2.Zero, actual: drag.Turned);

        // The router applies the press: the motion that came with it counts, and so does what follows.
        drag.Apply(phase: CommandPhase.Started);
        drag.Move(x: 20f);
        Assert.Equal(expected: 120f, actual: drag.Turned.X);

        // The release cuts the stream before the router applies it, and a late press edge turns nothing.
        drag.Button(down: false);
        drag.Move(x: 50f);
        drag.Apply(phase: CommandPhase.Started);
        Assert.Equal(expected: 120f, actual: drag.Turned.X);

        // The router lets go; a drag with no pointer button (a key, a typed command) turns with every move.
        drag.Apply(phase: CommandPhase.Completed);
        drag.Move(x: 1f);
        drag.Apply(phase: CommandPhase.Active);
        drag.Move(x: 3f);
        Assert.Equal(expected: 123f, actual: drag.Turned.X);

        drag.Motion.OnPointer(slot: -1);
        drag.Apply(phase: CommandPhase.Started, slot: -1);
        Assert.Equal(expected: 123f, actual: drag.Turned.X);
    }
    // A boot with no pointer (headless) has no gate: the edge still sets the hold.
    [Fact]
    public void AnEdgeWithNoGateSetsTheHoldAlone() {
        bool? held = null;

        WorldDragMotion.Hold(
            drag: null,
            held: value => held = value,
            phase: CommandPhase.Started,
            slot: 0
        );
        Assert.True(condition: held);
    }
}
