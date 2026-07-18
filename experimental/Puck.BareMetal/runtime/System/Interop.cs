// P/Invoke and layout attributes the C# compiler binds to, plus MemoryMarshal.

using System.Runtime.CompilerServices;

namespace System.Runtime.InteropServices {
    public enum CallingConvention { Winapi = 1, Cdecl = 2, StdCall = 3, ThisCall = 4, FastCall = 5 }
    public enum CharSet { None = 1, Ansi = 2, Unicode = 3, Auto = 4 }
    public enum LayoutKind { Sequential = 0, Explicit = 2, Auto = 3 }
    public enum UnmanagedType {
        Bool = 2, I1 = 3, U1 = 4, I2 = 5, U2 = 6, I4 = 7, U4 = 8, I8 = 9, U8 = 10,
        R4 = 11, R8 = 12, LPStr = 20, LPWStr = 21, ByValArray = 30, SysInt = 31, SysUInt = 32,
        FunctionPtr = 38, LPArray = 42, LPUTF8Str = 48
    }
    public sealed class DllImportAttribute : Attribute {
        public DllImportAttribute(string dllName) { Value = dllName; }

        public string Value { get; }

        public string EntryPoint;
        public CharSet CharSet;
        public CallingConvention CallingConvention;
        public bool SetLastError;
        public bool ExactSpelling;
        public bool PreserveSig;
        public bool BestFitMapping;
        public bool ThrowOnUnmappableChar;
    }
    public sealed class StructLayoutAttribute : Attribute {
        public StructLayoutAttribute(LayoutKind layoutKind) { Value = layoutKind; }

        public LayoutKind Value { get; }

        public int Pack;
        public int Size;
        public CharSet CharSet;
    }
    public sealed class FieldOffsetAttribute : Attribute {
        public FieldOffsetAttribute(int offset) { Value = offset; }

        public int Value { get; }
    }
    public sealed class MarshalAsAttribute : Attribute {
        public MarshalAsAttribute(UnmanagedType unmanagedType) { Value = unmanagedType; }

        public UnmanagedType Value { get; }

        public int SizeConst;
        public UnmanagedType ArraySubType;
    }
    public sealed class SuppressGCTransitionAttribute : Attribute { }
    public sealed class InAttribute : Attribute { }
    public sealed class OutAttribute : Attribute { }
    public sealed class OptionalAttribute : Attribute { }
    public sealed class UnmanagedCallersOnlyAttribute : Attribute {
        public Type[] CallConvs;
        public string EntryPoint;
    }
    public sealed class UnmanagedFunctionPointerAttribute : Attribute {
        public UnmanagedFunctionPointerAttribute(CallingConvention callingConvention) { }
    }
    public sealed class DefaultDllImportSearchPathsAttribute : Attribute {
        public DefaultDllImportSearchPathsAttribute(DllImportSearchPath paths) { }
    }
    public enum DllImportSearchPath { System32 = 2048, AssemblyDirectory = 2 }
    public static unsafe class MemoryMarshal {
        // The array's element storage begins right after the object header and length slot.
        private sealed class RawArrayData { public nint Length; public byte Data; }

        public static ref T GetArrayDataReference<T>(T[] array)
            => ref Unsafe.As<byte, T>(source: ref Unsafe.As<RawArrayData>(value: array).Data);
        public static ref T GetReference<T>(Span<T> span) => ref span._reference;
        public static ref T GetReference<T>(ReadOnlySpan<T> span) => ref span._reference;
        public static Span<TTo> Cast<TFrom, TTo>(Span<TFrom> span)
            => new Span<TTo>(ref Unsafe.As<TFrom, TTo>(source: ref span._reference),
                             (int)(((long)span.Length * Unsafe.SizeOf<TFrom>()) / Unsafe.SizeOf<TTo>()));
        public static Span<T> CreateSpan<T>(ref T reference, int length) => new Span<T>(length: length, reference: ref reference);
        public static ReadOnlySpan<T> CreateReadOnlySpan<T>(ref readonly T reference, int length)
            => new ReadOnlySpan<T>(ref Unsafe.AsRef(source: in reference), length);
        public static Span<byte> AsBytes<T>(Span<T> span)
            => new Span<byte>(ref Unsafe.As<T, byte>(source: ref span._reference), (span.Length * Unsafe.SizeOf<T>()));
    }
}
