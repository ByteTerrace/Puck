using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Hosting;
using Puck.Input;
using Puck.Launcher;
using Puck.Platform.Windows.Gamepad;
using Puck.Platform.Windows.Hid;
using Puck.Storage.DependencyInjection;

namespace Puck.Demo;

/// <summary>Composition-root wiring for the demo's controller support, kept out of the top-level program to
/// keep its class coupling in check.</summary>
internal static class GamepadDemoRegistration {
    /// <summary>
    /// Registers the gamepad manager, optionally a focus-gated <see cref="GamepadInputSource"/> (picked up by the
    /// command shell as an additional source), the demo's gamepad bindings, and the hosted service that governs device
    /// lifetime.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="registerGlobalSource">Whether to register the global gamepad command source. The live Overworld
    /// mode sets this <see langword="false"/> so it can be the SOLE per-frame gamepad drainer (per-device routing) —
    /// otherwise the global source's destructive drain would consume each controller's edges before the game sees them.
    /// The hardware manager + hotplug service are always registered.</param>
    /// <returns>The same <paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddDemoGamepad(this IServiceCollection services, bool registerGlobalSource = true) {
        ArgumentNullException.ThrowIfNull(services);

        // Binding-bar state: the overworld's deterministic input path publishes it, the binding-bar overlay node
        // reads it.
        services.AddSingleton<BindingBar.BindingBarStore>();
        // The local player's binding-page profile: persisted JSON (seeded from the built-in default on first
        // run) compiled into the paged resolver the deterministic input path folds signals through.
        PuckStorageServiceRegistration.AddCore(services: services);
        services.AddSingleton<BindingProfileDocumentStore>();
        services.AddSingleton(implementationFactory: static sp => {
            var store = sp.GetRequiredService<BindingProfileDocumentStore>();
            var @default = BindingProfileDocuments.BuildDefault();

            try {
                var document = store.LoadOrCreateAsync(@default: @default).AsTask().GetAwaiter().GetResult();

                // A stored document from an older vocabulary is reseeded in place (Compile would reject it anyway):
                // the default changed under it, and a permanent fall-back-with-warning would leave the on-disk file
                // lying about what the buttons do.
                if (!string.Equals(a: document.Version, b: @default.Version, comparisonType: StringComparison.Ordinal)) {
                    Console.Error.WriteLine(value: $"[bindings] stored profile is {document.Version}; reseeding as {@default.Version} (customizations reset) at {store.FilePath}.");
                    _ = store.SaveAsync(document: @default).AsTask().GetAwaiter().GetResult();
                    document = @default;
                }

                Console.Error.WriteLine(value: $"[bindings] profile loaded ({document.Pages.Count} pages); edit {store.FilePath} to customize.");

                return new PagedInputBindings(profile: BindingProfile.Compile(document: document));
            } catch (Exception exception) {
                // A malformed or stale stored document must never take input down — fall back to the built-in
                // default and tell the player which file to fix (or delete, to re-seed).
                Console.Error.WriteLine(value: $"[bindings] stored profile rejected ({exception.Message}); using the built-in default. Fix or delete {store.FilePath} to re-seed.");

                return new PagedInputBindings(profile: BindingProfile.Compile(document: @default));
            }
        });
        services.AddSingleton(implementationFactory: static sp => new GamepadManager(
            // The Windows HID transport and the Windows Xbox (XInput + GameInput) backend live in Puck.Platform;
            // a Linux build would inject hidraw / evdev implementations of the same abstractions instead.
            acquisitionSource: new Win32XboxAcquisitionSource(diagnostics: static message => Console.Error.WriteLine(value: message)),
            // The shared capture clock so each HID report is stamped with its true arrival time on the I/O thread.
            clock: sp.GetService<IInputClock>(),
            diagnostics: static message => Console.Error.WriteLine(value: message),
            hidSource: new Win32HidDeviceSource()
        ));
        // The run's single-drainer mechanism (see InputArbiter's doc comment): every gamepad-reading registrant —
        // the brick-pad service, the overworld's page adapters, the router/local intent sources — samples through
        // this instead of calling GamepadManager.Drain itself. Registered unconditionally, alongside the manager it
        // wraps; it stays idle unless a registrant registers a lane.
        services.AddSingleton<IInputArbiter>(implementationFactory: static sp => new InputArbiter(manager: sp.GetRequiredService<GamepadManager>()));

        if (registerGlobalSource) {
            services.AddSingleton<ICommandSource>(implementationFactory: static sp => new GamepadInputSource(
                bindings: new Dictionary<string, IReadOnlyList<CommandBinding>>(comparer: StringComparer.OrdinalIgnoreCase) {
                    [InputSources.Gamepad.ButtonSouth] = [new(Command: "gamepad.a")],
                    [InputSources.Gamepad.ButtonEast] = [new(Command: "gamepad.rumble")],
                    [InputSources.Gamepad.ButtonWest] = [new(Command: "gamepad.trigger-rumble")],
                    [InputSources.Gamepad.ButtonNorth] = [new(Command: "gamepad.led")],
                    [InputSources.Gamepad.Start] = [new(Command: "gamepad.start")],
                    [InputSources.Gamepad.DpadUp] = [new(Command: "gamepad.trigger-effect")],
                    [InputSources.Gamepad.DpadDown] = [new(Command: "gamepad.trigger-effect-off")],
                    [InputSources.Gamepad.LeftShoulder] = [new(Command: "gamepad.trigger-schedule")],
                    [InputSources.Gamepad.Touchpad] = [new(Command: "gamepad.touchpad")],
                    [InputSources.Gamepad.Mute] = [new(Command: "gamepad.mute")],
                    [InputSources.Gamepad.Touchpad0] = [new(Command: "gamepad.touch0")],
                    [InputSources.Gamepad.Touchpad1] = [new(Command: "gamepad.touch1")],
                    [InputSources.Gamepad.LeftStick] = [new(Command: "gamepad.move")],
                    [InputSources.Gamepad.Gyro] = [new(Command: "gamepad.gyro")],
                    [InputSources.Gamepad.Orientation] = [new(Command: "gamepad.orientation")],
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
