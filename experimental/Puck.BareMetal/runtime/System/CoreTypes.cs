// The core value and reference types. The object header (MethodTable pointer), the string layout
// (length + inline first char), and the array layout (length at offset 8) are fixed by the compiler.

using System.Runtime;
using System.Runtime.CompilerServices;

namespace System
{
    public unsafe partial class Object
    {
#pragma warning disable 169 // populated by the runtime; read through unsafe pointers
        // First field of every object: a pointer to its runtime type. Contract with the compiler.
        internal MethodTable* m_pMethodTable;
#pragma warning restore 169

        public Object() { }
        ~Object() { }
    }

    public abstract class ValueType { }
    public abstract class Enum : ValueType { }

    // Primitive value types. The bodies are empty: their storage is the intrinsic machine value the
    // ILC knows how to manipulate directly. Declaring them is what makes them usable as managed types.
    public struct Void { }
    public struct Boolean { }
    public struct Char { }
    public struct SByte { }
    public struct Byte { }
    public struct Int16 { }
    public struct UInt16 { }
    public struct Int32 { }
    public struct UInt32 { }
    public struct Int64 { }
    public struct UInt64 { }
    public struct IntPtr { }
    public struct UIntPtr { }
    public struct Single { }
    public struct Double { }

    public ref struct TypedReference { }
    public struct RuntimeArgumentHandle { }

    // The compiler materializes string literals against this layout: an int length, then the first
    // UTF-16 char of the inline character buffer.
    public sealed partial class String
    {
        [Intrinsic]
        public static readonly string Empty = "";

#pragma warning disable 169
        private readonly int _stringLength;
        private char _firstChar;
#pragma warning restore 169

        public int Length
        {
            [Intrinsic]
            get => _stringLength;
        }

        public unsafe char this[int index]
        {
            [Intrinsic]
            get { fixed (char* p = &_firstChar) return p[index]; }
        }

        // Pins the first character; `fixed (char* p = str)` lowers to this (GetPinnableReference).
        [Intrinsic]
        public ref readonly char GetPinnableReference() => ref _firstChar;
    }

    // Single-dimension array base. The element count lives immediately after the MethodTable pointer
    // (see StartupCodeHelpers.RhpNewArray), which Length reads via the ILC intrinsic.
    public abstract partial class Array
    {
#pragma warning disable 169
        private int _numComponents;
#pragma warning restore 169

        public int Length
        {
            [Intrinsic]
            get => _numComponents;
        }

        public int Rank => 1;
    }

    // The generic array type the ILC instantiates for T[] (its MethodTable is emitted as __Array<T>).
    // Collection-interface bodies are attached in compat/Array.Compat.cs (a partial of this type).
    public partial class Array<T> : Array { }

    public abstract partial class Delegate { }
    public abstract partial class MulticastDelegate : Delegate { }

    public struct RuntimeTypeHandle { }
    public struct RuntimeMethodHandle { }
    public struct RuntimeFieldHandle { }

    public abstract class Type
    {
        public static Type GetTypeFromHandle(RuntimeTypeHandle handle) => null;
    }

    // The concrete Type the runtime hands back; the JIT requires it as a builtin class.
    internal sealed class RuntimeType : Type { }

    public struct Nullable<T> where T : struct
    {
        private readonly bool _hasValue;
        private readonly T _value;

        public Nullable(T value) { _hasValue = true; _value = value; }
        public bool HasValue => _hasValue;
        public T Value => _value;
        public T GetValueOrDefault() => _value;
        public T GetValueOrDefault(T defaultValue) => _hasValue ? _value : defaultValue;

        public static implicit operator Nullable<T>(T value) => new Nullable<T>(value);
        public static explicit operator T(Nullable<T> value) => value._value;
    }

    public abstract class Attribute { }

    // Minimal exception type so `throw`/catch shapes compile; with no EH model these are not raised.
    public class Exception
    {
        public Exception() { }
        public Exception(string message) { Message = message; }
        public string Message { get; }
    }

    public class SystemException : Exception
    {
        public SystemException() { }
        public SystemException(string message) : base(message) { }
    }
}
