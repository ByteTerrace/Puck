namespace System.Runtime.CompilerServices;

/// <summary>
/// Polyfills the marker type the C# compiler requires for <c>init</c>-only members and positional records.
/// netstandard2.0 predates this type (it shipped in the .NET 5 reference assemblies); the compiler only needs its
/// metadata TOKEN to exist somewhere in the compilation, so an empty internal type here is the whole of the fix.
/// </summary>
internal static class IsExternalInit {
}
