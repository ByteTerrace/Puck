// System.IDisposable — the deterministic-cleanup contract the C# `using` statement binds to,
// and the basis of the DI container's ownership model (a service disposes its own native
// resources; the container disposes its services in reverse order). Part of Puck.BareMetal.
// With no GC this only releases *unmanaged* resources — the managed object itself is not
// reclaimed until process exit.

namespace System
{
    public interface IDisposable
    {
        void Dispose();
    }
}
