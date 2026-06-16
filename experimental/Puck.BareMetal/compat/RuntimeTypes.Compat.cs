// Marker / well-known types the stock .NET 10 ILC looks up by name (GetKnownType) during
// scanning and feature substitution. Declaring the types is enough for ILC. Part of Puck.BareMetal.

namespace System.Runtime.InteropServices
{
    // Looked up by ILScanResults.GetBodyAndFieldSubstitutions, which also resolves the
    // static GetDynamicInterfaceImplementation helper by name. Puck.Runtime has no dynamic
    // interface dispatch, so the helper fails fast (it is never reached by the samples).
    public unsafe partial interface IDynamicInterfaceCastable
    {
        internal static nint GetDynamicInterfaceImplementation(IDynamicInterfaceCastable instance, System.Runtime.MethodTable* interfaceType, ushort slot)
        {
            Environment.FailFast(null);
            return 0;
        }
    }
}
