using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Hosting;
using Puck.Input;
using Puck.Launcher;
using Puck.Platform.Windows.Gamepad;
using Puck.Platform.Windows.Hid;

namespace Puck.Demo;

/// <summary>Composition-root wiring for the demo's controller support, kept out of the top-level program to
/// keep its class coupling in check.</summary>
internal static class GamepadDemoRegistration
{
    /// <summary>
    /// Registers the gamepad manager, optionally a focus-gated <see cref="GamepadInputSource"/> (picked up by the
    /// command shell as an additional source), the demo's gamepad bindings, and the hosted service that governs device
    /// lifetime.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="registerGlobalSource">Whether to register the global gamepad command source. The live MiniAction
    /// mode sets this <see langword="false"/> so it can be the SOLE per-frame gamepad drainer (per-device routing) —
    /// otherwise the global source's destructive drain would consume each controller's edges before the game sees them.
    /// The hardware manager + hotplug service are always registered.</param>
    /// <returns>The same <paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddDemoGamepad(this IServiceCollection services, bool registerGlobalSource = true) {
        ArgumentNullException.ThrowIfNull(services);

        // Per-controller cursor state: command handlers write it, the cursor overlay node reads it.
        services.AddSingleton<CursorStore>();
        services.AddSingleton(implementationFactory: static _ => new GamepadManager(
            // The Windows HID transport and the Windows Xbox (XInput + GameInput) backend live in Puck.Platform;
            // a Linux build would inject hidraw / evdev implementations of the same abstractions instead.
            acquisitionSource: new Win32XboxAcquisitionSource(diagnostics: static message => Console.Error.WriteLine(value: message)),
            diagnostics: static message => Console.Error.WriteLine(value: message),
            hidSource: new Win32HidDeviceSource()
        ));

        if (registerGlobalSource) {
            services.AddSingleton<ICommandSource>(implementationFactory: static sp => new GamepadInputSource(
                bindings: new Dictionary<string, IReadOnlyList<CommandBinding>>(comparer: StringComparer.OrdinalIgnoreCase) {
                    [InputSources.Gamepad.ButtonSouth] = [new(Command: "gamepad-a")],
                    [InputSources.Gamepad.ButtonEast] = [new(Command: "gamepad-rumble")],
                    [InputSources.Gamepad.ButtonWest] = [new(Command: "gamepad-trigger-rumble")],
                    [InputSources.Gamepad.ButtonNorth] = [new(Command: "gamepad-led")],
                    [InputSources.Gamepad.Start] = [new(Command: "gamepad-start")],
                    [InputSources.Gamepad.DpadUp] = [new(Command: "gamepad-trigger-effect")],
                    [InputSources.Gamepad.DpadDown] = [new(Command: "gamepad-trigger-effect-off")],
                    [InputSources.Gamepad.Touchpad] = [new(Command: "gamepad-touchpad")],
                    [InputSources.Gamepad.Mute] = [new(Command: "gamepad-mute")],
                    [InputSources.Gamepad.Touchpad0] = [new(Command: "gamepad-touch0"), new(Command: "cursor-touch")],
                    [InputSources.Gamepad.Touchpad1] = [new(Command: "gamepad-touch1")],
                    [InputSources.Gamepad.LeftStick] = [new(Command: "gamepad-move"), new(Command: "cursor-nudge-stick")],
                    [InputSources.Gamepad.Gyro] = [new(Command: "gamepad-gyro")],
                    [InputSources.Gamepad.Accelerometer] = [new(Command: "cursor-tilt")],
                    [InputSources.Gamepad.Orientation] = [new(Command: "gamepad-orientation")],
                },
                diagnostics: static message => Console.Error.WriteLine(value: message),
                isActiveFor: sp.GetRequiredService<IInputFocus>().IsActiveFor,
                manager: sp.GetRequiredService<GamepadManager>()
            ));
        }

        services.AddHostedService<GamepadHostedService>();

        return services;
    }
}
