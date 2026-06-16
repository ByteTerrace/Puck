namespace Puck.Commands;

/// <summary>
/// Binds an input source to a command for a <see cref="BindingCommandSource"/>.
/// </summary>
/// <remarks>
/// When <see cref="Value"/> is <see langword="null"/>, the originating <see cref="InputSignal"/>'s own
/// value and text pass through (for example, a mouse delta driving <c>look</c>, or typed text driving
/// <c>console.insert</c>). When it is set, the constant value is used instead — for example, a digital
/// arrow key driving a two-dimensional <c>move</c> axis.
/// </remarks>
/// <param name="Command">The name of the command to activate.</param>
/// <param name="Value">The constant value to send, or <see langword="null"/> to pass the input's value through.</param>
public readonly record struct CommandBinding(string Command, CommandValue? Value = null);
