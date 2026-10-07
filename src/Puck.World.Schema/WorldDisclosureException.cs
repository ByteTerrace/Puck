namespace Puck.World;

/// <summary>The refusal a disclosure raises when a value it would compose derives from state its recipient may not
/// read: a keyed value on a hidden state clock, or a binding to a hidden cell. Nothing derived is composed. It is the
/// one failure a presentation feed answers by detaching its recipient; every other failure of a composition is a
/// fault.</summary>
/// <param name="message">The refusal, naming the value and the state it derives from.</param>
public sealed class WorldDisclosureException(string message) : InvalidOperationException(message: message);
