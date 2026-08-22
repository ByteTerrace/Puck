namespace Puck.World.Client;

/// <summary>Single-sourced command names the root player command module's own constants forward to — the
/// binding authoring surface (<see cref="WorldSeatBindings"/>) lives in this project and
/// cannot reference the root's command modules, so each name's true declaration moves here and the root constant
/// becomes a forwarding const, the same shape <c>WorldPopulation.LocalSeatCount</c> already uses for
/// <c>WorldPopulationLimits.LocalSeatCount</c>.</summary>
public static class PlayerCommandNames {
    public const string AssignCommand = "player.assign";
    public const string ClaimCommand = "player.claim";
    public const string ConfirmCommand = "player.confirm";
    public const string CycleCommand = "player.cycle";
    public const string LookCommand = "player.look";
    /// <summary>The held free-look modifier: camera look continues while body steering is suppressed and movement
    /// remains in the character's authoritative heading frame.</summary>
    public const string FreeLookCommand = "player.look.free";
    /// <summary>The Axis2D look command whose yaw also faces the body along the camera's planar look direction.</summary>
    public const string LookSteerCommand = "player.look.steer";
    /// <summary>The held pointer-orbit command: while held, pointer motion orbits the seat camera.</summary>
    public const string OrbitCommand = "player.orbit";
    /// <summary>The held pointer-steer command: while held, pointer motion orbits the seat camera and the body faces
    /// where the camera looks.</summary>
    public const string SteerCommand = "player.steer";
    /// <summary>The look-swap command: turns the seat camera a half-turn about the body — look behind; again to
    /// look forward.</summary>
    public const string SwapLookCommand = "player.look.swap";
    /// <summary>The look-recenter command: turns the seat camera round behind the body.</summary>
    public const string RecenterLookCommand = "player.look.recenter";
    /// <summary>The no-token Free Cam toggle: <c>player.camera [seat]</c>, the bindable door onto the same
    /// camera-targeting <see cref="WorldSeatModeFamily"/> state <see cref="ModeCommand"/> names explicitly.</summary>
    public const string CameraCommand = "player.camera";
    /// <summary>The generic per-seat mode-family flip: <c>player.mode &lt;family&gt; &lt;state&gt; [seat]</c> — see
    /// <see cref="WorldSeatModeFamily"/>.</summary>
    public const string ModeCommand = "player.mode";
    /// <summary>The generic sensor-input mode toggle.</summary>
    public const string MotionControlsCommand = "player.motion.controls";
    /// <summary>The Axis3D angular-velocity input consumed while motion-control mode is toggled on.</summary>
    public const string MotionAngularCommand = "player.motion.angular";
    public const string MoveCommand = "player.move";
    /// <summary>The live-camera-framed Axis2D movement command that preserves heading so lateral input strafes and
    /// forward travel turns with look yaw.</summary>
    public const string MoveStrafeCommand = "player.move.strafe";

    /// <summary>The runtime command name a seat binding lowers one validated channel ordinal to.</summary>
    /// <param name="ordinal">The channel ordinal.</param>
    public static string RoutedChannelCommandName(int ordinal) => $"channel.ordinal.{ordinal}";
}
/// <summary>Single-sourced command names the root wheel command module's own constants forward to. See
/// <see cref="PlayerCommandNames"/>.</summary>
public static class WorldWheelCommandNames {
    public const string CancelCommand = "player.wheel.cancel";
    public const string CommitCommand = "player.wheel.commit";
    public const string RingCommand = "player.wheel.ring";
    public const string SelectCommand = "player.wheel.select";
}
