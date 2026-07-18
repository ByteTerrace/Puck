// Part of Puck.BareMetal. Marker attributes ordinary C# expects from the BCL. No runtime behavior;
// they only need to exist so referencing source compiles.

namespace System {
    // Applied to enums whose values are bit flags. Informational only; nothing consumes it.
    public sealed class FlagsAttribute : Attribute {
    }
}
