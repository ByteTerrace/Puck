namespace Puck.Platform;

public readonly record struct InputPacket(
    InputKind Kind,
    string? Text = null,
    int DeltaX = 0,
    int DeltaY = 0
) {
    public static InputPacket ArrowDown() => new(InputKind.ArrowDown);
    public static InputPacket ArrowLeft() => new(InputKind.ArrowLeft);
    public static InputPacket ArrowRight() => new(InputKind.ArrowRight);
    public static InputPacket ArrowUp() => new(InputKind.ArrowUp);
    public static InputPacket Backspace() => new(InputKind.Backspace);
    public static InputPacket CopyInput() => new(InputKind.CopyInput);
    public static InputPacket CycleFocus() => new(InputKind.CycleFocus);
    public static InputPacket Function1() => new(InputKind.Function1);
    public static InputPacket Function2() => new(InputKind.Function2);
    public static InputPacket Function3() => new(InputKind.Function3);
    public static InputPacket Function4() => new(InputKind.Function4);
    public static InputPacket Function5() => new(InputKind.Function5);
    public static InputPacket Function6() => new(InputKind.Function6);
    public static InputPacket Function7() => new(InputKind.Function7);
    public static InputPacket Function8() => new(InputKind.Function8);
    public static InputPacket MouseMove(int deltaX, int deltaY) => new(
        InputKind.MouseMove,
        DeltaX: deltaX,
        DeltaY: deltaY
    );
    public static InputPacket RecallNext() => new(InputKind.RecallNext);
    public static InputPacket RecallPrevious() => new(InputKind.RecallPrevious);
    public static InputPacket SelectAll() => new(InputKind.SelectAll);
    public static InputPacket Submit() => new(InputKind.Submit);
    public static InputPacket TextInput(string text) {
        ArgumentNullException.ThrowIfNull(text);
        return new InputPacket(
            InputKind.Text,
            text
        );
    }
    public static InputPacket ToggleConsole() => new(InputKind.ToggleConsole);
    public static InputPacket ToggleMainMenu() => new(InputKind.ToggleMainMenu);
}
