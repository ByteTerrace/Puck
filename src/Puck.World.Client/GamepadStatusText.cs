using Puck.Input.Devices;

namespace Puck.World.Client;

/// <summary>The terse text <c>world.devices</c> appends to a gamepad's segment: its power report, then its
/// plugged-in accessories, space separated.</summary>
public static class GamepadStatusText {
    /// <summary>Formats a gamepad's power and accessories.</summary>
    /// <param name="power">The power report. <c>battery N%</c> (running on its battery), <c>charging N%</c>,
    /// <c>not charging N%</c>, <c>charged</c> (full, plugged in) and <c>wired</c> (cable-powered, no battery) name
    /// the state; <see cref="GamepadPowerState.Unknown"/> contributes nothing.</param>
    /// <param name="accessories">The plugged-in accessories: <c>headset</c> when both an earpiece and a microphone
    /// are present, otherwise <c>headphones</c> or <c>microphone</c>; nothing when none.</param>
    /// <returns>The status text, or <see langword="null"/> when there is nothing to report.</returns>
    public static string? Describe(GamepadPower power, GamepadAccessories accessories) {
        var percent = $"{power.Percent}%";
        var parts = new List<string>(capacity: 2);

        switch (power.State) {
            case GamepadPowerState.OnBattery:
                parts.Add(item: $"battery {percent}");
                break;
            case GamepadPowerState.Charging:
                parts.Add(item: $"charging {percent}");
                break;
            case GamepadPowerState.NotCharging:
                parts.Add(item: $"not charging {percent}");
                break;
            case GamepadPowerState.Charged:
                parts.Add(item: "charged");
                break;
            case GamepadPowerState.NoBattery:
                parts.Add(item: "wired");
                break;
        }

        var headphones = accessories.HasFlag(flag: GamepadAccessories.Headphones);
        var microphone = accessories.HasFlag(flag: GamepadAccessories.Microphone);

        if (headphones && microphone) {
            parts.Add(item: "headset");
        }
        else if (headphones) {
            parts.Add(item: "headphones");
        }
        else if (microphone) {
            parts.Add(item: "microphone");
        }

        return ((parts.Count == 0)
            ? null
            : string.Join(
                separator: " ",
                values: parts
            )
        );
    }
}
