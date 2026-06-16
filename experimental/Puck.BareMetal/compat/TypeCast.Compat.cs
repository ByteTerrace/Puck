// JIT cast / array-store helpers. Part of Puck.BareMetal.
//
// The stock .NET 10 ILC maps the JIT's cast / array-store helpers to methods on
// System.Runtime.TypeCast (see ILCompiler JitHelper.GetEntryPoint): IsInstanceOf*,
// CheckCast*, StelemRef, LdelemaRef. The JIT calls them with a fixed ABI, so the signatures
// below must match stock corelib exactly (MethodTable* first, etc.).
//
// Scope: Puck.Runtime has a single-inheritance, no-GC object model. We implement assignability for
// the class hierarchy (base-type walk) AND interfaces (interface-map walk — the same map the
// interface-dispatch resolver in compat/native/puck-rt.c uses), so `is`/`as`/cast against an
// interface and storing an implementer into an `IService[]` both work. Only exact-instantiation
// matches are recognized for generic interfaces; co-/contra-variant casts (e.g. testing an
// IEnumerable<string> as IEnumerable<object>) would need the structural variance walk and still
// fall through to fail-fast. Puck.Runtime has no GC, so none of this collects.

using System.Runtime.CompilerServices;

namespace System.Runtime
{
    internal static unsafe class TypeCast
    {
        private struct ArrayElement
        {
            public object Value;
        }

        // MethodTable._uFlags lives in the first 4 bytes; the EETypeElementType is in its top
        // bits (Internal.Runtime.EETypeFlags.ElementTypeMask/Shift).
        private const uint ElementTypeMask = 0x7C000000u;
        private const int ElementTypeShift = 26;
        private const uint ET_Class = 0x14u;       // EETypeElementType.Class
        private const uint ET_SystemArray = 0x16u; // EETypeElementType.SystemArray (System.Array itself)
        private const uint ET_Array = 0x17u;       // EETypeElementType.Array
        private const uint ET_SzArray = 0x18u;     // EETypeElementType.SzArray

        private static uint ElementType(MethodTable* mt) => (*(uint*)mt & ElementTypeMask) >> ElementTypeShift;
        private static bool IsArray(MethodTable* mt) { uint et = ElementType(mt); return et == ET_Array || et == ET_SzArray; }
        // System.Object is the unique CLASS with no base type; interfaces also have a null base,
        // hence the element-type guard.
        private static bool IsObjectType(MethodTable* mt) => ElementType(mt) == ET_Class && mt->_relatedType == null;
        private static bool IsSystemArrayType(MethodTable* mt) => ElementType(mt) == ET_SystemArray;

        // Walk the CLASS base-type chain (MethodTable._relatedType is the base type for classes,
        // terminating at System.Object whose related type is null). Includes the exact match.
        //
        // IMPORTANT: for an ARRAY, _relatedType is the ELEMENT type, not a base type — walking it
        // would wrongly report e.g. IService[] as deriving from IService. So stop at arrays; an
        // array's class-assignability (-> object / System.Array / covariant array) is handled in
        // IsClassAssignable instead.
        private static bool DerivesFrom(MethodTable* mt, MethodTable* target)
        {
            while (mt != null)
            {
                if (mt == target)
                    return true;

                if (IsArray(mt))
                    return false;

                mt = mt->_relatedType;
            }

            return false;
        }

        // Does the type implement the interface? NativeAOT stores the FULL (transitive) set of
        // implemented interfaces in each type's interface map, so — like the runtime's own
        // IsInstanceOfInterface — a single scan of the type's own map suffices (no base walk).
        //
        // The interface map is not a fixed struct field: it follows the 24-byte MethodTable
        // header and the vtable, so its offset depends on the vtable slot count. Read the counts
        // out of the header by offset (they are private on the MethodTable struct).
        private static bool ImplementsInterface(MethodTable* mt, MethodTable* interfaceType)
        {
            ushort numVtableSlots = *(ushort*)((byte*)mt + 16);
            ushort numInterfaces = *(ushort*)((byte*)mt + 18);
            MethodTable** interfaceMap = (MethodTable**)((byte*)mt + 24 + (nuint)8 * numVtableSlots);

            for (ushort i = 0; i < numInterfaces; i++)
                if (interfaceMap[i] == interfaceType)
                    return true;

            return false;
        }

        // Assignability to a CLASS target (no interfaces). Handles arrays: every array derives
        // from object and System.Array, and S[] is assignable to T[] when S is assignable to T
        // (reference array covariance).
        private static bool IsClassAssignable(MethodTable* objType, MethodTable* targetType)
        {
            if (DerivesFrom(objType, targetType))
                return true;

            if (IsArray(objType))
            {
                if (IsObjectType(targetType) || IsSystemArrayType(targetType))
                    return true;

                if (IsArray(targetType))
                    return IsAssignableTo(objType->_relatedType, targetType->_relatedType);
            }

            return false;
        }

        private static bool IsAssignableTo(MethodTable* objType, MethodTable* targetType)
            => IsClassAssignable(objType, targetType) || ImplementsInterface(objType, targetType);

        public static object IsInstanceOfClass(MethodTable* pTargetType, object obj)
        {
            if (obj == null)
                return null;

            return IsClassAssignable(obj.m_pMethodTable, pTargetType) ? obj : null;
        }

        public static object IsInstanceOfAny(MethodTable* pTargetType, object obj)
        {
            if (obj == null)
                return null;

            return IsAssignableTo(obj.m_pMethodTable, pTargetType) ? obj : null;
        }

        public static object IsInstanceOfInterface(MethodTable* pTargetType, object obj)
        {
            if (obj == null)
                return null;

            return ImplementsInterface(obj.m_pMethodTable, pTargetType) ? obj : null;
        }

        public static bool IsInstanceOfException(MethodTable* pTargetType, object obj)
            => obj != null && DerivesFrom(obj.m_pMethodTable, pTargetType);

        public static object CheckCastClass(MethodTable* pTargetType, object obj)
        {
            if (obj == null || IsClassAssignable(obj.m_pMethodTable, pTargetType))
                return obj;

            Environment.FailFast(null);
            return null;
        }

        public static object CheckCastClassSpecial(MethodTable* pTargetType, object obj)
            => CheckCastClass(pTargetType, obj);

        public static object CheckCastAny(MethodTable* pTargetType, object obj)
        {
            if (obj == null || IsAssignableTo(obj.m_pMethodTable, pTargetType))
                return obj;

            Environment.FailFast(null);
            return null;
        }

        public static object CheckCastInterface(MethodTable* pTargetType, object obj)
        {
            if (obj == null || ImplementsInterface(obj.m_pMethodTable, pTargetType))
                return obj;

            Environment.FailFast(null);
            return null;
        }

        // Array element read: return a managed reference to the element. Reference-typed
        // array elements are stored inline as object slots.
        public static ref object LdelemaRef(object[] array, nint index, MethodTable* elementType)
            => ref Unsafe.As<ArrayElement[]>(array)[index].Value;

        // Array element write with the covariant store check. Unlike the core
        // StelemRef (an EXACT element-type match — which rejects storing an implementer into an
        // `IService[]` or a derived instance into a `Base[]`), this allows any element assignable
        // to the array's element type, so DI-style `IService[]` collections work.
        public static void StelemRef(object[] array, nint index, object obj)
        {
            ref object element = ref Unsafe.As<ArrayElement[]>(array)[index].Value;

            if (obj == null)
            {
                element = null;
                return;
            }

            MethodTable* elementType = array.m_pMethodTable->_relatedType;

            if (IsAssignableTo(obj.m_pMethodTable, elementType))
            {
                element = obj;
                return;
            }

            Environment.FailFast(null); // genuine array element type mismatch
        }
    }
}
