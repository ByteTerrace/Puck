// The NativeAOT runtime contract: the runtime type layout (MethodTable), object/array allocation,
// the managed<->native transition stubs, and the throw helpers the compiler roots.

using System;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Internal.Runtime.CompilerHelpers;

namespace System.Runtime
{
    // Marks a method whose unmangled native name the runtime/compiler binds to (e.g. "RhpNewFast").
    internal sealed class RuntimeExportAttribute : Attribute
    {
        public RuntimeExportAttribute(string entry) { }
    }

    // Marks a method implemented by an external native symbol the runtime imports.
    internal sealed class RuntimeImportAttribute : Attribute
    {
        public RuntimeImportAttribute(string entry) { }
        public RuntimeImportAttribute(string lib, string entry) { }
    }

    // The runtime type representation (EEType). Layout is a hard contract with the ILC: the field
    // order/sizes must match the MethodTable the ILC emits. Only the members the allocation/array
    // helpers read are named; the rest of the on-disk structure follows in memory.
    internal unsafe struct MethodTable
    {
        internal ushort _usComponentSize;   // element size for arrays/strings; 0 otherwise
        private ushort _usFlags;
        internal uint _uBaseSize;           // base instance size
        internal MethodTable* _relatedType; // array element type / base type
        private ushort _usNumVtableSlots;
        private ushort _usNumInterfaces;
        private uint _uHashCode;
    }
}

namespace Internal.Runtime.CompilerHelpers
{
    // The ILC emits calls to ThrowHelpers for IL that can fault (bounds, divide-by-zero, ...). With no
    // exception model we fail fast. Additional throw helpers the JIT references live in
    // compat/ThrowHelpers.Compat.cs (this is a partial); keep the partial in sync with that file.
    internal partial class ThrowHelpers
    {
        private static void ThrowIndexOutOfRangeException() => Environment.FailFast(null);
        private static void ThrowDivideByZeroException() => Environment.FailFast(null);
        private static void ThrowPlatformNotSupportedException() => Environment.FailFast(null);
    }

    // The class the ILC requires for process startup + the managed/native transition + allocation.
    // The transition stubs are no-ops (no GC, so nothing to coordinate). Object/array allocation
    // zero-fills from the platform heap (UEFI firmware pool, or the mimalloc-backed host allocator).
    internal static unsafe partial class StartupCodeHelpers
    {
        [RuntimeExport("RhpReversePInvoke")]
        private static void RhpReversePInvoke(IntPtr frame) { }
        [RuntimeExport("RhpReversePInvokeReturn")]
        private static void RhpReversePInvokeReturn(IntPtr frame) { }
        [RuntimeExport("RhpPInvoke")]
        private static void RhpPInvoke(IntPtr frame) { }
        [RuntimeExport("RhpPInvokeReturn")]
        private static void RhpPInvokeReturn(IntPtr frame) { }
        [RuntimeExport("RhpGcPoll")]
        private static void RhpGcPoll() { }

        [RuntimeExport("RhpFallbackFailFast")]
        private static void RhpFallbackFailFast() => Environment.FailFast(null);

        [RuntimeExport("RhpNewFast")]
        private static void* RhpNewFast(MethodTable* pMT)
        {
            MethodTable** obj = AllocObject(pMT->_uBaseSize);
            *obj = pMT;
            return obj;
        }

        [RuntimeExport("RhpNewArray")]
        private static void* RhpNewArray(MethodTable* pMT, int numElements)
        {
            if (numElements < 0)
                Environment.FailFast(null);

            MethodTable** obj = AllocObject(pMT->_uBaseSize + (uint)numElements * pMT->_usComponentSize);
            *obj = pMT;
            *(int*)(obj + 1) = numElements; // Array.m_Length sits right after the MethodTable pointer
            return obj;
        }

        // Single-element ref view of an array, for the covariant store check below.
        private struct ArrayElement { public object Value; }

        [RuntimeExport("RhpStelemRef")]
        public static void StelemRef(Array array, nint index, object obj)
        {
            ref object element = ref Unsafe.As<ArrayElement[]>(array)[index].Value;
            MethodTable* elementType = array.m_pMethodTable->_relatedType;

            if (obj != null && elementType != obj.m_pMethodTable)
                Environment.FailFast(null); // array covariance violation

            element = obj;
        }

        [RuntimeExport("RhpAssignRef")]
        public static void RhpAssignRef(void** dst, void* r) => *dst = r;

        [RuntimeExport("RhpCheckedAssignRef")]
        public static void RhpCheckedAssignRef(void** dst, void* r) => *dst = r;

        // Zero-initialized allocation. The two paths mirror the build: a UEFI image pulls from the
        // firmware boot-services pool; a hosted image uses the mimalloc-backed native allocator
        // (compat/native/mimalloc-glue.c -> PuckAllocZeroed).
        private static MethodTable** AllocObject(uint size)
        {
            MethodTable** result;
#if UEFI
            if (Object.EfiSystemTable->BootServices->AllocatePool(2 /* EfiLoaderData */, (nint)size, (void**)&result) != 0)
                result = null;
#else
            result = PuckAllocZeroed(size);
#endif
            if (result == null)
                Environment.FailFast(null);
            return result;
        }

#if !UEFI
        [DllImport("puckrt"), SuppressGCTransition]
        private static extern MethodTable** PuckAllocZeroed(nuint size);
#endif
    }
}
