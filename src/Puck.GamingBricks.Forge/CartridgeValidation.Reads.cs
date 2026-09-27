using Puck.State;

namespace Puck.GamingBricks.Forge;

internal sealed partial class CartridgeValidation {
    // Every operand read: an array element, a button, the light sensor, or a declared slot of the width the field reads.
    private void Read(InstructionPayload.State state, string path, bool wide) {
        if (state.Key is not null) {
            Element(
                array: state.Name.Spelling,
                key: state.Key.Spelling,
                path: path
            );

            return;
        }

        if (CartridgeExpressions.TryKey(
            name: state.Name.Spelling,
            button: out var button,
            mode: out var mode
        )) {
            if (button is not ("a" or "b" or "start" or "select" or "up" or "down" or "left" or "right")) {
                Error(
                    message: $"Unknown joypad button '{button}'.",
                    path: path
                );
            }

            if (mode is not ("held" or "pressed" or "released")) {
                Error(
                    message: $"Expected held, pressed or released; found '{mode}'.",
                    path: path
                );
            }

            return;
        }

        if (CartridgeExpressions.IsLight(state: state)) {
            if (document.Target != "cgb") {
                Error(
                    message: $"Reading '{CartridgeExpressions.Light}' needs the cgb target's infrared receiver; the {document.Target} target has no light sensor.",
                    path: path
                );
            }

            return;
        }

        if (state.Name.Spelling.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "$"
        )) {
            Error(
                path: path,
                message: $"'{state.Name}' is not a channel a cartridge answers; input reads through '{CartridgeExpressions.KeyPrefix}<button>:<mode>' and '{CartridgeExpressions.Light}'."
            );

            return;
        }

        if (!m_variables.Contains(item: state.Name.Spelling)) {
            Error(
                path: path,
                message: $"Unknown state variable '{state.Name}'."
            );

            return;
        }

        if (
            !wide &&
            m_widths.TryGetValue(
            key: state.Name.Spelling,
            value: out var width
        ) &&
            (width != 1)
        ) {
            Error(
                path: path,
                message: $"'{state.Name}' is a wide slot; this field reads a byte."
            );
        }
    }
}
