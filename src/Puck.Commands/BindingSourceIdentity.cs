namespace Puck.Commands;

// A synthesized binding has no physical source to own its downstream held contribution. Give every command
// destination one deterministic source instead: different destinations release independently, while every binding
// that toggles the same destination addresses the same input-side latch and the same downstream contribution.
internal static class BindingSourceIdentity {
    internal static string ForCommand(string command, BindingEntryMode mode) => string.Concat(
        str0: mode == BindingEntryMode.Toggle ? "$toggle:" : "$binding:",
        str1: command
    );
}
