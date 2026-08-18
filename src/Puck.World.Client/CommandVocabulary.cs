namespace Puck.World.Client;

/// <summary>Single-sourced command names the root editor command module's own constants forward to — the binding
/// authoring surface (<see cref="WorldSeatBindings"/>) lives in this project and
/// cannot reference the root's command modules, so each name's true declaration moves here and the root constant
/// becomes a forwarding const, the same shape <c>WorldPopulation.LocalSeatCount</c> already uses for
/// <c>WorldPopulationLimits.LocalSeatCount</c>.</summary>
public static class EditorCommandNames {
    public const string AscendCommand = "editor.ascend";
    public const string CameraToggleCommand = "editor.camera";
    public const string DescendCommand = "editor.descend";
    public const string EnterCommand = "editor.enter";
    public const string ExitCommand = "editor.exit";
    public const string LookCommand = "editor.stick.look";
    public const string MoveCommand = "editor.stick.move";
    public const string SpeedCommand = "editor.cam.speed";
    public const string StatusCommand = "editor.status";
}
/// <summary>Single-sourced command names the root editor-selection command module's own constants forward to. See
/// <see cref="EditorCommandNames"/>.</summary>
public static class EditorSelectionCommandNames {
    public const string CancelCommand = "editor.cancel";
    public const string DeleteCommand = "editor.delete";
    public const string GrabCommand = "editor.grab";
    public const string PickCommand = "editor.pick";
    public const string ReleaseCommand = "editor.release";
    public const string SelectCommand = "editor.select";
    public const string SnapCommand = "editor.snap";
}
/// <summary>Single-sourced command names the root editor-creation command module's own constants forward to. See
/// <see cref="EditorCommandNames"/>.</summary>
public static class EditorCreationCommandNames {
    public const string NextCommand = "editor.creation.next";
    public const string PrevCommand = "editor.creation.prev";
    public const string SpawnCommand = "editor.spawn.creation";
}
/// <summary>Single-sourced command names the root editor-sculpt command module's own constants forward to. See
/// <see cref="EditorCommandNames"/>.</summary>
public static class EditorSculptCommandNames {
    public const string CommitCommand = "editor.sculpt.commit";
    public const string EaselCommand = "editor.sculpt.easel";
    public const string ExitCommand = "editor.sculpt.exit";
    public const string RedoCommand = "editor.sculpt.redo";
    public const string UndoCommand = "editor.sculpt.undo";
    public const string ZoomCommand = "editor.sculpt.zoom";
}
/// <summary>Single-sourced command names the root editor-sculpt-shape command module's own constants forward to. See
/// <see cref="EditorCommandNames"/>.</summary>
public static class EditorSculptShapeCommandNames {
    public const string AddCommand = "editor.sculpt.add";
    public const string DeselectCommand = "editor.sculpt.deselect";
    public const string DuplicateCommand = "editor.sculpt.duplicate";
    public const string PrimitiveCommand = "editor.sculpt.primitive";
    public const string RemoveCommand = "editor.sculpt.remove";
    public const string ScaleCommand = "editor.sculpt.scale";
    public const string SelectCommand = "editor.sculpt.select";
}
/// <summary>Single-sourced command names the root editor-sculpt-style command module's own constants forward to. See
/// <see cref="EditorCommandNames"/>.</summary>
public static class EditorSculptStyleCommandNames {
    public const string BlendCommand = "editor.sculpt.blend";
    public const string MaterialCommand = "editor.sculpt.material";
}
/// <summary>Single-sourced command names the root editor-sculpt-rig command module's own constants forward to. See
/// <see cref="EditorCommandNames"/>.</summary>
public static class EditorSculptRigCommandNames {
    public const string ChainCommand = "editor.sculpt.chain";
    public const string ChainKindCommand = "editor.sculpt.chain.kind";
    public const string ChainNextCommand = "editor.sculpt.chain.next";
    public const string ChainRemoveCommand = "editor.sculpt.chain.remove";
    public const string FrameCommand = "editor.sculpt.frame";
    public const string FrameRecordCommand = "editor.sculpt.frame.record";
    public const string FrameRemoveCommand = "editor.sculpt.frame.remove";
    public const string PlayCommand = "editor.sculpt.play";
}
/// <summary>Single-sourced command names the root player command module's own constants forward to. See
/// <see cref="EditorCommandNames"/>.</summary>
public static class PlayerCommandNames {
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
/// <see cref="EditorCommandNames"/>.</summary>
public static class WorldWheelCommandNames {
    public const string CancelCommand = "player.wheel.cancel";
    public const string CommitCommand = "player.wheel.commit";
    public const string RingCommand = "player.wheel.ring";
    public const string SelectCommand = "player.wheel.select";
}
