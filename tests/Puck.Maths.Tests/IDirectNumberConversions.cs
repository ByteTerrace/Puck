using System.Numerics;

namespace Puck.Maths.Tests;

// A derived interface can call INumberBase's protected hooks directly. Create* would fall back to the peer's
// opposite hook and hide an unsupported half of the contract.
internal interface IDirectNumberConversions<T> : INumberBase<T> where T : struct, INumberBase<T> {
    static bool FromChecked<TOther>(TOther value, out T result) where TOther : struct, INumberBase<TOther> =>
        T.TryConvertFromChecked(value, out result);
    static bool FromSaturating<TOther>(TOther value, out T result) where TOther : struct, INumberBase<TOther> =>
        T.TryConvertFromSaturating(value, out result);
    static bool FromTruncating<TOther>(TOther value, out T result) where TOther : struct, INumberBase<TOther> =>
        T.TryConvertFromTruncating(value, out result);
    static bool ToChecked<TOther>(T value, out TOther result) where TOther : struct, INumberBase<TOther> =>
        T.TryConvertToChecked(value, out result);
    static bool ToSaturating<TOther>(T value, out TOther result) where TOther : struct, INumberBase<TOther> =>
        T.TryConvertToSaturating(value, out result);
    static bool ToTruncating<TOther>(T value, out TOther result) where TOther : struct, INumberBase<TOther> =>
        T.TryConvertToTruncating(value, out result);
}
