// Internal.Runtime.CompilerHelpers.ThrowHelpers methods the stock .NET 10 ILC requires beyond the
// three Puck.Runtime ships (runtime/Internal/Stubs.cs). Part of Puck.BareMetal.
//
// The stock ILC:
//   * resolves these helpers BY NAME (TypeSystem GetHelperEntryPoint -> GetMethod(name, null)),
//     and roots e.g. ThrowInvalidProgramException up front, so they must simply EXIST; and
//   * when it must emit a "this type/method failed to load" throwing stub, it emits
//     `ldc.i4 <ExceptionStringID>` followed by one `ldstr` per message argument and then
//     calls the helper (see TypeSystemThrowingILEmitter). So the signatures must be
//     (int) + N strings to match what is pushed.
//
// Puck.Runtime has no exception machinery: each of these simply fails fast. The leading int is
// the ExceptionStringID; we ignore it.

using System;

namespace Internal.Runtime.CompilerHelpers
{
    internal static partial class ThrowHelpers
    {
        // 0 message args -> (int)
        internal static void ThrowInvalidProgramException(int id) => Environment.FailFast(null);
        internal static void ThrowBadImageFormatException(int id) => Environment.FailFast(null);
        internal static void ThrowMarshalDirectiveException(int id) => Environment.FailFast(null);
        internal static void ThrowAmbiguousMatchException(int id) => Environment.FailFast(null);

        // 1 message arg -> (int, string)
        internal static void ThrowInvalidProgramExceptionWithArgument(int id, string methodName) => Environment.FailFast(null);
        internal static void ThrowMissingMethodException(int id, string methodName) => Environment.FailFast(null);
        internal static void ThrowMissingFieldException(int id, string fieldName) => Environment.FailFast(null);
        internal static void ThrowFileNotFoundException(int id, string fileName) => Environment.FailFast(null);

        // 2 message args -> (int, string, string)
        internal static void ThrowTypeLoadException(int id, string className, string typeName) => Environment.FailFast(null);

        // 3 message args -> (int, string, string, string)
        internal static void ThrowTypeLoadExceptionWithArgument(int id, string className, string typeName, string messageArg) => Environment.FailFast(null);

        // Parameterless throw helpers the JIT routes arithmetic/array checks to (overflow
        // checks, null/rank/array-store checks). The stock ILC looks them up by name on this
        // type; Puck.Runtime ships IndexOutOfRange/DivideByZero/PlatformNotSupported (Stubs.cs),
        // the rest live here. No exceptions: fail fast.
        internal static void ThrowOverflowException() => Environment.FailFast(null);
        internal static void ThrowNullReferenceException() => Environment.FailFast(null);
        internal static void ThrowArrayTypeMismatchException() => Environment.FailFast(null);
        internal static void ThrowRankException() => Environment.FailFast(null);

        // Parameterless trimming / feature-switch helpers the stock ILC may root.
        internal static void ThrowBodyRemoved() => Environment.FailFast(null);
        internal static void ThrowFeatureBodyRemoved() => Environment.FailFast(null);
        internal static void ThrowInstanceBodyRemoved() => Environment.FailFast(null);
        internal static void ThrowUnavailableType() => Environment.FailFast(null);
        internal static void ThrowNotSupportedInlineArrayEqualsGetHashCode() => Environment.FailFast(null);
    }
}
