// Part of Puck.BareMetal. Compiler-recognized marker types the C# language depends on but the
// minimal core omits.

namespace System.Runtime.CompilerServices
{
    // Required custom modifier the C# compiler emits on the type of a `volatile` field
    // (modreq(IsVolatile)). Its mere existence enables the `volatile` keyword; it carries no
    // members. The JIT applies the acquire/release semantics. Needed for cross-thread flags
    // (e.g. spin barriers) on the multi-threaded host.
    public sealed class IsVolatile
    {
    }
}
