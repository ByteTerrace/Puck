// Puck.BareMetal VulkanWindow — minimal ownership/disposal scope (the DI teardown model).
//
// Things register with the scope; on Dispose() the scope disposes them in REVERSE order
// (LIFO), so dependents are torn down before their dependencies (Vulkan before the window
// it draws into). Each registered item is "responsible for itself" — it releases its own
// native resources in its Dispose().
//
// Tracked items are plain System.IDisposable implementers and are disposed via polymorphic
// INTERFACE dispatch (`node.Item.Dispose()` with multiple implementing types). The stock ILC's
// RhpInitialDynamicInterfaceDispatch cell is resolved by compat/native/puck-rt.c (the resolver)
// + compat/native/interface-dispatch-x64.asm (the entry stub).
//
// Prepend-only linked list — a natural fit for LIFO teardown (newest disposed first). A
// covariant IDisposable[] would also work (the array-store check does proper assignability;
// see TypeCast.StelemRef in compat); the list is just the simpler structure here.
using System;

namespace Puck.BareMetal.VulkanWindow;

internal sealed class DisposalScope : IDisposable
{
    private sealed class Node
    {
        public IDisposable Item;
        public Node Next;
    }

    private Node _head;

    public void Track(IDisposable item)
    {
        Node node = new Node();
        node.Item = item;
        node.Next = _head;
        _head = node; // prepend -> Dispose walks newest-first = reverse registration order
    }

    public void Dispose()
    {
        for (Node node = _head; node != null; node = node.Next)
            node.Item.Dispose(); // polymorphic interface dispatch (multiple implementers)
        _head = null;
    }
}
