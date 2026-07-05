using System.CommandLine;
using System.Numerics;
using Puck.Abstractions.Windowing;
using Puck.Commands;
using Puck.Demo.Overworld;
using Puck.Hosting;
using Puck.Input;
using Puck.Input.Output;

namespace Puck.Demo;

/// <summary>
/// The demo's command surface: the single-line console editing verbs (incl. clipboard copy/paste/select) and the
/// gamepad input/haptics proofs. Every verb is a digital or axis command bound to a key, the console, or a controller
/// input by <c>Program</c>'s binding sources. (The generic <c>backend</c> toggle now lives in the launcher.)
/// </summary>
internal sealed class DemoCommandModule(
    IClipboardService clipboard,
    DemoConsole console,
    GamepadManager gamepads,
    IInputClock inputClock,
    IRenderNode rootNode
) : ICommandModule {
    private const string TextMap = "text";

    // Continuous axes fire every frame while active; log only every Nth so the console stays readable.
    private const int AxisLogInterval = 15;

    // The active producer when it can take a debug view mode (null under --validate or a blank root).
    private readonly IDebugViewTarget? m_debugViewTarget = (rootNode as IDebugViewTarget);
    // The creator-mode host (the live overworld root); null for any other run, so the 'creator' verb reports unavailable.
    private readonly ICreatorModeHost? m_creatorHost = (rootNode as ICreatorModeHost);
    private int m_gyroLogTick;
    private int m_moveLogTick;
    private int m_orientationLogTick;
    private int m_touch0LogTick;
    private int m_touch1LogTick;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        foreach (var command in GetConsoleCommands()) { yield return command; }
        foreach (var command in GetControllerCommands()) { yield return command; }
        foreach (var command in GetDebugViewCommands()) { yield return command; }

        yield return CommandDefinition.Verb(
            description: "Toggles CREATOR mode: the in-engine SDF authoring surface. Cycle the primitive with the bumpers, slide it with the left stick (triggers raise/lower), place with South, undo with East, exit with North.",
            handler: _ => {
                if (m_creatorHost is null) {
                    return new CommandResult("[creator: unavailable — the overworld is not the active root]");
                }

                var active = m_creatorHost.ToggleCreatorMode();

                return new CommandResult($"[creator {(active ? "on" : "off")}]");
            },
            name: "creator",
            valueKind: CommandValueKind.Digital
        );
    }

    private IEnumerable<CommandDefinition> GetConsoleCommands() {
        yield return CommandDefinition.Verb(
            description: "Deletes the last character of the console line.",
            handler: _ => {
                console.Backspace();

                return CommandResult.None;
            },
            map: TextMap,
            name: "backspace",
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Toggles console mode.",
            handler: context => {
                var registry = context.Registry!;
                var active = registry.IsMapActive(map: TextMap);

                if (active) {
                    registry.DeactivateMap(map: TextMap);
                } else {
                    registry.ActivateMap(map: TextMap);
                }

                // Mirror the open/closed state to the on-screen console panel.
                console.SetVisible(visible: !active);

                return new CommandResult($"[console {(active ? "closed" : "open")}]");
            },
            name: "console",
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Copies clipboard content.",
            handler: _ => {
                var selected = console.Selected;

                if (selected.Length > 0) {
                    clipboard.SetText(text: selected);
                }

                return new CommandResult("[copy]");
            },
            map: TextMap,
            name: "copy",
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Appends a typed character to the console line.",
            handler: context => {
                if (!string.IsNullOrEmpty(value: context.Text)) {
                    console.Append(text: context.Text);
                }

                return CommandResult.None;
            },
            map: TextMap,
            name: "echo",
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Submits the console line to the registry for interpretation.",
            handler: context => {
                var result = context.Registry!.Submit(line: console.TakeLine());

                if (!string.IsNullOrEmpty(value: result.Output)) {
                    console.WriteLine(message: result.Output);
                }

                return CommandResult.None;
            },
            map: TextMap,
            name: "enter",
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Closes the console when open; otherwise quits the terminal.",
            handler: context => {
                var registry = context.Registry!;

                if (registry.IsMapActive(map: TextMap)) {
                    registry.DeactivateMap(map: TextMap);
                    console.SetVisible(visible: false);

                    return new CommandResult("[console closed]");
                }

                return registry.Submit(line: "quit");
            },
            name: "escape",
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Pastes clipboard content.",
            handler: _ => {
                if (
                    clipboard.TryGetText(text: out var text) &&
                    (text.Length > 0)
                ) {
                    console.Append(text: text);
                }

                return new CommandResult("[paste]");
            },
            map: TextMap,
            name: "paste",
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Selects all console content.",
            handler: _ => {
                console.SelectAll();

                return new CommandResult("[select]");
            },
            map: TextMap,
            name: "select",
            valueKind: CommandValueKind.Digital
        );
    }
    private IEnumerable<CommandDefinition> GetControllerCommands() {
        yield return CommandDefinition.Verb(
            description: "Logs a Switch Pro south-face button press (controller input proof).",
            handler: static _ => new CommandResult("[gamepad A]"),
            name: "gamepad-a",
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Logs a Switch Pro start button press.",
            handler: static _ => new CommandResult("[gamepad start]"),
            name: "gamepad-start",
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Logs a DualSense touchpad click (controller input proof).",
            handler: static _ => new CommandResult("[gamepad touchpad]"),
            name: "gamepad-touchpad",
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Logs a DualSense mute-button press (controller input proof).",
            handler: static _ => new CommandResult("[gamepad mute]"),
            name: "gamepad-mute",
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Rumbles the controller that pressed the button (per-device haptics proof).",
            handler: context => {
                if (!gamepads.TryGetOutput(deviceId: context.DeviceId, output: out var output)) {
                    return new CommandResult("[rumble: no device]");
                }

                var accepted = output.Rumble(effect: new RumbleEffect(
                    DurationMilliseconds: 300u,
                    HighFrequency: 0.4f,
                    LowFrequency: 0.6f
                ));

                return new CommandResult(accepted ? "[rumble]" : "[rumble: rejected]");
            },
            name: "gamepad-rumble",
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Fires trigger (impulse) rumble where supported (Xbox via GameInput), else dual-motor rumble.",
            handler: context => {
                if (!gamepads.TryGetOutput(deviceId: context.DeviceId, output: out var output)) {
                    return new CommandResult("[trigger-rumble: no device]");
                }

                // Impulse-trigger rumble is Xbox-only; on a family without it (Switch, DualSense) fall back to
                // dual-motor rumble so every controller gives a meaningful response to the same binding.
                if (output.Capabilities.HasFlag(flag: GamepadOutputCapabilities.TriggerRumble)) {
                    var accepted = output.RumbleTriggers(effect: new TriggerRumbleEffect(
                        DurationMilliseconds: 400u,
                        Left: 0.8f,
                        Right: 0.8f
                    ));

                    return new CommandResult(accepted ? "[trigger-rumble]" : "[trigger-rumble: rejected]");
                }

                var fallback = output.Rumble(effect: new RumbleEffect(
                    DurationMilliseconds: 400u,
                    HighFrequency: 0.8f,
                    LowFrequency: 0.4f
                ));

                return new CommandResult(fallback ? "[trigger-rumble: dual-motor fallback]" : "[trigger-rumble: unsupported]");
            },
            name: "gamepad-trigger-rumble",
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Sets the controller's RGB indicator where supported (the DualSense light bar).",
            handler: context => {
                if (!gamepads.TryGetOutput(deviceId: context.DeviceId, output: out var output)) {
                    return new CommandResult("[led: no device]");
                }

                if (!output.Capabilities.HasFlag(flag: GamepadOutputCapabilities.Led)) {
                    return new CommandResult("[led: unsupported]");
                }

                var accepted = output.SetLed(color: new LedColor(Red: 0x00, Green: 0x80, Blue: 0x80));

                return new CommandResult(accepted ? "[led: cyan]" : "[led: rejected]");
            },
            name: "gamepad-led",
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Arms DualSense adaptive-trigger resistance on the pressing pad (typed trigger effect); pull L2/R2 to feel it.",
            handler: context => {
                if (!gamepads.TryGetOutput(deviceId: context.DeviceId, output: out var output)) {
                    return new CommandResult("[trigger-effect: no device]");
                }

                if (!output.Capabilities.HasFlag(flag: GamepadOutputCapabilities.TriggerEffect)) {
                    return new CommandResult("[trigger-effect: unsupported]");
                }

                // Strong, uniform resistance from roughly the first third of the pull to the end, on both triggers.
                var resistance = TriggerEffectSpec.Feedback(position: 3, strength: 8);
                var accepted = output.SetTriggerEffect(left: resistance, right: resistance);

                return new CommandResult(accepted ? "[trigger-effect: resistance armed]" : "[trigger-effect: rejected]");
            },
            name: "gamepad-trigger-effect",
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Clears DualSense adaptive-trigger resistance on the pressing pad.",
            handler: context => {
                if (!gamepads.TryGetOutput(deviceId: context.DeviceId, output: out var output)) {
                    return new CommandResult("[trigger-effect: no device]");
                }

                if (!output.Capabilities.HasFlag(flag: GamepadOutputCapabilities.TriggerEffect)) {
                    return new CommandResult("[trigger-effect: unsupported]");
                }

                var accepted = output.SetTriggerEffect(left: TriggerEffectSpec.Off, right: TriggerEffectSpec.Off);

                return new CommandResult(accepted ? "[trigger-effect: cleared]" : "[trigger-effect: rejected]");
            },
            name: "gamepad-trigger-effect-off",
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Schedules a DualSense trigger buzz one second out (clock-timed haptics); press, wait, feel it land. D-pad Down clears it.",
            handler: context => {
                if (!gamepads.TryGetOutput(deviceId: context.DeviceId, output: out var output)) {
                    return new CommandResult("[trigger-schedule: no device]");
                }

                if (!output.Capabilities.HasFlag(flag: GamepadOutputCapabilities.TriggerEffect)) {
                    return new CommandResult("[trigger-schedule: unsupported]");
                }

                // Schedule a buzz to land one second from now against the SAME capture clock that stamps input, so
                // the effect arrives on a precise tick rather than whenever the write happens to be serviced.
                var buzz = TriggerEffectSpec.Vibration(position: 2, amplitude: 6, frequency: 30);
                var accepted = output.SetTriggerEffectAt(left: buzz, right: buzz, fireAtTick: (inputClock.NowTicks + EngineTicks.PerSecond));

                return new CommandResult(accepted ? "[trigger-schedule: buzz in 1s]" : "[trigger-schedule: rejected]");
            },
            name: "gamepad-trigger-schedule",
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Reports the left stick vector (throttled).",
            handler: context => {
                if (0 != (m_moveLogTick++ % AxisLogInterval)) {
                    return CommandResult.None;
                }

                var value = context.Value.AsAxis2D;

                return new CommandResult($"[move {value.X:F2},{value.Y:F2}]");
            },
            name: "gamepad-move",
            valueKind: CommandValueKind.Axis2D
        );
        yield return CommandDefinition.Verb(
            description: "Reports the first DualSense touchpad finger position, normalized 0..1 (throttled).",
            handler: context => {
                if (0 != (m_touch0LogTick++ % AxisLogInterval)) {
                    return CommandResult.None;
                }

                var value = context.Value.AsAxis2D;

                return new CommandResult($"[touch0 {value.X:F2},{value.Y:F2}]");
            },
            name: "gamepad-touch0",
            valueKind: CommandValueKind.Axis2D
        );
        yield return CommandDefinition.Verb(
            description: "Reports the second DualSense touchpad finger position, normalized 0..1 (throttled).",
            handler: context => {
                if (0 != (m_touch1LogTick++ % AxisLogInterval)) {
                    return CommandResult.None;
                }

                var value = context.Value.AsAxis2D;

                return new CommandResult($"[touch1 {value.X:F2},{value.Y:F2}]");
            },
            name: "gamepad-touch1",
            valueKind: CommandValueKind.Axis2D
        );
        yield return CommandDefinition.Verb(
            description: "Reports the gyro angular velocity in rad/s (throttled).",
            handler: context => {
                if (0 != (m_gyroLogTick++ % AxisLogInterval)) {
                    return CommandResult.None;
                }

                var value = context.Value.AsAxis3D;

                return new CommandResult($"[gyro {value.X:F2},{value.Y:F2},{value.Z:F2}]");
            },
            name: "gamepad-gyro",
            valueKind: CommandValueKind.Axis3D
        );
        yield return CommandDefinition.Verb(
            description: "Logs the fused controller orientation as euler degrees (throttled).",
            handler: context => {
                if (0 != (m_orientationLogTick++ % AxisLogInterval)) {
                    return CommandResult.None;
                }

                var (pitch, yaw, roll) = ToEulerDegrees(orientation: context.Value.AsOrientation);

                return new CommandResult($"[orientation pitch={pitch:F0} yaw={yaw:F0} roll={roll:F0}]");
            },
            name: "gamepad-orientation",
            valueKind: CommandValueKind.Orientation
        );
    }
    private IEnumerable<CommandDefinition> GetDebugViewCommands() {
        CommandResult SetDebugViewMode(int mode) {
            if (m_debugViewTarget is null) {
                return new CommandResult("[debug.view: no SDF producer active]");
            }

            m_debugViewTarget.DebugMode = mode;

            return new CommandResult($"[debug.view: {DebugViewModes.Name(mode: mode)}]");
        }

        for (var mode = 0; (mode < DebugViewModes.Count); mode++) {
            var selectedMode = mode;
            var selectedName = DebugViewModes.Name(mode: selectedMode);

            yield return CommandDefinition.Verb(
                description: $"Sets the SDF debug view mode to {selectedName}.",
                handler: _ => SetDebugViewMode(mode: selectedMode),
                name: DebugViewModes.Command(mode: selectedMode),
                valueKind: CommandValueKind.Digital
            );
        }

        var viewModeArgument = new Argument<string>(name: "mode") {
            Description = $"One of: {string.Join(separator: ", ", values: DebugViewModes.Names)}.",
        };

        yield return new CommandDefinition(
            Description: "Sets the SDF debug view mode (off, depth, normals, raydir, material-id, iteration-count).",
            Handler: context => {
                var name = (context.Parse?.GetValue(argument: viewModeArgument) ?? "off");

                if (!DebugViewModes.TryParse(name: name, mode: out var mode)) {
                    return new CommandResult($"[debug.view: unknown mode '{name}']");
                }

                return SetDebugViewMode(mode: mode);
            },
            Name: "debug.view",
            TextCommand: new Command(name: "debug.view", description: "Sets the SDF debug view mode.") {
                viewModeArgument,
            },
            ValueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Cycles to the next SDF debug view mode.",
            handler: _ => {
                if (m_debugViewTarget is null) {
                    return new CommandResult("[debug.view: no SDF producer active]");
                }

                var next = ((m_debugViewTarget.DebugMode + 1) % DebugViewModes.Count);

                m_debugViewTarget.DebugMode = next;

                return new CommandResult($"[debug.view: {DebugViewModes.Name(mode: next)}]");
            },
            name: "debug.view.cycle",
            valueKind: CommandValueKind.Digital
        );
    }

    // Tait-Bryan angles (degrees) from the orientation quaternion, for a readable log of the fused pose: pitch
    // tracks tilting the pad fore/aft, roll tracks banking it left/right, yaw tracks turning it (drifts slowly).
    private static (float Pitch, float Yaw, float Roll) ToEulerDegrees(Quaternion orientation) {
        const float ToDegrees = (180f / MathF.PI);

        var sinPitch = (2f * ((orientation.W * orientation.X) - (orientation.Y * orientation.Z)));
        var pitch = ((MathF.Abs(x: sinPitch) >= 1f)
            ? MathF.CopySign(x: (MathF.PI / 2f), y: sinPitch)
            : MathF.Asin(x: sinPitch));
        var yaw = MathF.Atan2(y: (2f * ((orientation.W * orientation.Y) + (orientation.X * orientation.Z))), x: (1f - (2f * ((orientation.X * orientation.X) + (orientation.Y * orientation.Y)))));
        var roll = MathF.Atan2(y: (2f * ((orientation.W * orientation.Z) + (orientation.X * orientation.Y))), x: (1f - (2f * ((orientation.X * orientation.X) + (orientation.Z * orientation.Z)))));

        return ((pitch * ToDegrees), (yaw * ToDegrees), (roll * ToDegrees));
    }
}
