// Part of Puck.BareMetal. The stock .NET 10 ILC maps the JIT's BulkWriteBarrier helper to
// System.Buffer.BulkMoveWithWriteBarrier. No GC means no write barriers, so a bulk ref move is
// just a plain byte move via SpanHelpers.Memmove.
//
// ABI: the JIT calls this with a fixed (ref byte, ref byte, nuint) signature — must match exactly.

namespace System
{
    internal static class Buffer
    {
        internal static void BulkMoveWithWriteBarrier(ref byte destination, ref byte source, nuint byteCount)
            => SpanHelpers.Memmove(ref destination, ref source, byteCount);
    }
}
