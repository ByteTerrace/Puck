namespace Puck.Commands;

/// <summary>Declares which terminal-focus state admits a source-driven command.</summary>
public enum CommandInputScope : byte {
    /// <summary>The ordinary command plane: a device must hold terminal input focus.</summary>
    Focused = 0,

    /// <summary>An always-active terminal plane used for commands that restore their own device's focus.</summary>
    FocusExempt,
}
