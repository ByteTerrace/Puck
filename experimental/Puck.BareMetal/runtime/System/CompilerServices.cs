// Compiler-recognized attributes, the unsafe/runtime helper intrinsics, and lazy static-constructor
// support. The attributes here are the ones the C# compiler needs to exist to emit ordinary code
// (ref structs, readonly, ref-safety, etc.); FlagsAttribute, IsVolatile and others live in compat/.

namespace System {
    public enum AttributeTargets { All = 32767 }
    public sealed class AttributeUsageAttribute : Attribute {
        public AttributeUsageAttribute(AttributeTargets validOn) { }

        public bool AllowMultiple { get; set; }
        public bool Inherited { get; set; }
    }
    public sealed class ParamArrayAttribute : Attribute { }
}
namespace System.Reflection {
    // Emitted by the compiler on any type that declares an indexer (names the default member).
    public sealed class DefaultMemberAttribute : Attribute {
        public DefaultMemberAttribute(string memberName) { MemberName = memberName; }

        public string MemberName { get; }
    }
}
namespace System.Runtime.CompilerServices {
    // Marks a method the JIT replaces with a known operation (length reads, Unsafe.*, etc.).
    public sealed class IntrinsicAttribute : Attribute { }
    public enum MethodImplOptions { Unmanaged = 4, NoInlining = 8, NoOptimization = 64, AggressiveInlining = 256, AggressiveOptimization = 512, InternalCall = 4096 }
    public sealed class MethodImplAttribute : Attribute {
        public MethodImplAttribute() { }
        public MethodImplAttribute(MethodImplOptions methodImplOptions) { }
    }

    // Emitted by the compiler on readonly/in constructs and ref structs; must merely exist.
    public sealed class IsReadOnlyAttribute : Attribute { }
    public sealed class IsByRefLikeAttribute : Attribute { }
    public sealed class ScopedRefAttribute : Attribute { }
    public sealed class RequiredMemberAttribute : Attribute { }
    public sealed class CompilerGeneratedAttribute : Attribute { }
    public sealed class CompilerFeatureRequiredAttribute : Attribute { public CompilerFeatureRequiredAttribute(string featureName) { } }
    public sealed class RefSafetyRulesAttribute : Attribute { public RefSafetyRulesAttribute(int version) { } }
    public sealed class InlineArrayAttribute : Attribute { public InlineArrayAttribute(int length) { } }

    // The presence of these string constants tells the C# compiler which runtime capabilities the
    // target supports (ref fields, default interface methods, native-int operators, ...).
    public static class RuntimeFeature {
        public const string ByRefFields = nameof(ByRefFields);
        public const string ByRefLikeGenerics = nameof(ByRefLikeGenerics);
        public const string DefaultImplementationsOfInterfaces = nameof(DefaultImplementationsOfInterfaces);
        public const string NumericIntPtr = nameof(NumericIntPtr);
        public const string UnmanagedSignatureCallingConvention = nameof(UnmanagedSignatureCallingConvention);
        public const string VirtualStaticsInInterfaces = nameof(VirtualStaticsInInterfaces);
    }
    public static unsafe class RuntimeHelpers {
        // Array-initializer hook (the compiler emits an RVA blob it expects copied into the array's
        // storage). Empty: does not copy.
        public static void InitializeArray(Array array, RuntimeFieldHandle fldHandle) { }

        [Intrinsic]
        public static int OffsetToStringData => (2 * sizeof(nint));

        public static unsafe int GetHashCode(object o) => (int)(nint)Unsafe.As<object, IntPtr>(source: ref o);
        public static bool Equals(object o1, object o2) => (o1 == o2);
        public static void RunClassConstructor(RuntimeTypeHandle type) { }
        [Intrinsic]
        public static bool IsReferenceOrContainsReferences<T>() => true;
    }

    // Raw memory reinterpretation. Every method is a JIT intrinsic replaced before codegen; for an
    // unconstrained T none of these can be written in C# (no T*, and no throw without an EH model),
    // so the ref-returning primitives self-reference purely to type-check. The bodies never run.
    public static unsafe class Unsafe {
        [Intrinsic] public static void* AsPointer<T>(ref T value) => null;
        [Intrinsic] public static int SizeOf<T>() => 0;
        [Intrinsic] public static ref T AsRef<T>(void* source) => ref AsRef<T>(source: source);
        [Intrinsic] public static ref T AsRef<T>(scoped ref readonly T source) => ref AsRef(source: in source);
        [Intrinsic] public static ref TTo As<TFrom, TTo>(ref TFrom source) => ref As<TFrom, TTo>(source: ref source);
        [Intrinsic] public static T As<T>(object value) where T : class => As<T>(value: value);
        [Intrinsic] public static ref T Add<T>(ref T source, int elementOffset) => ref Add(elementOffset: elementOffset, source: ref source);
        [Intrinsic] public static ref T Add<T>(ref T source, nint elementOffset) => ref Add(elementOffset: elementOffset, source: ref source);
        [Intrinsic] public static ref T AddByteOffset<T>(ref T source, nint byteOffset) => ref AddByteOffset(byteOffset: byteOffset, source: ref source);
        [Intrinsic] public static nint ByteOffset<T>(ref readonly T origin, ref readonly T target) => (nint)((byte*)AsPointer(value: ref AsRef(source: in target)) - (byte*)AsPointer(value: ref AsRef(source: in origin)));
        [Intrinsic] public static bool AreSame<T>(ref readonly T left, ref readonly T right) => (AsPointer(value: ref AsRef(source: in left)) == AsPointer(value: ref AsRef(source: in right)));
        [Intrinsic] public static bool IsNullRef<T>(ref readonly T source) => (AsPointer(value: ref AsRef(source: in source)) is null);
        [Intrinsic] public static ref T NullRef<T>() => ref AsRef<T>(source: null);
        [Intrinsic] public static T ReadUnaligned<T>(void* source) => ReadUnaligned<T>(source: source);
        [Intrinsic] public static void WriteUnaligned<T>(void* destination, T value) => WriteUnaligned(destination: destination, value: value);
        [Intrinsic] public static T ReadUnaligned<T>(ref readonly byte source) => ReadUnaligned<T>(source: in source);
        [Intrinsic] public static void WriteUnaligned<T>(ref byte destination, T value) => WriteUnaligned(destination: ref destination, value: value);
        [Intrinsic] public static void SkipInit<T>(out T value) => value = default;
    }

    // The state the compiler emits per type with a non-trivial static constructor. The cctor pointer
    // is zeroed before the .cctor runs so reentrant first-access during the .cctor does not recurse.
    public struct StaticClassConstructionContext {
        public nint cctorMethodAddress;
        public int initialized;
    }

    internal static unsafe partial class ClassConstructorRunner {
        internal static void CheckStaticClassConstruction(ref StaticClassConstructionContext context) {
            if (context.cctorMethodAddress == 0)
                return; // already run (single-threaded model)

            nint cctor = context.cctorMethodAddress;

            context.cctorMethodAddress = 0;
            ((delegate*<void>)cctor)();
        }

        // The non-GC twin of compat's CheckStaticClassConstructionReturnGCStaticBase.
        private static void* CheckStaticClassConstructionReturnNonGCStaticBase(ref StaticClassConstructionContext context, void* nonGcStaticBase) {
            CheckStaticClassConstruction(context: ref context);
            return nonGcStaticBase;
        }
    }
}
