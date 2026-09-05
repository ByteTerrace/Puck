using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    public static BigInteger CombinatorialCount(int n, int k) {
        if (k > n) { return BigInteger.Zero; }
        k = Math.Min(k, n - k);
        var numerator = BigInteger.One;
        for (var i = 0; i < k; ++i) { numerator *= n - i; }
        return numerator / PermutationCount(k);
    }

    public static BigInteger PermutationCount(int n) {
        var result = BigInteger.One;
        for (var i = 2; i <= n; ++i) { result *= i; }
        return result;
    }

    public static BigInteger ColexRank(ReadOnlySpan<int> elements) {
        var rank = BigInteger.Zero;
        for (var i = 0; i < elements.Length; ++i) { rank += CombinatorialCount(elements[i], i + 1); }
        return rank;
    }

    public static BigInteger LexicographicRank(ReadOnlySpan<int> elements) {
        var rank = BigInteger.Zero;
        for (var i = 0; i < elements.Length; ++i) {
            var smaller = 0;
            for (var j = i + 1; j < elements.Length; ++j) { if (elements[j] < elements[i]) { ++smaller; } }
            rank += smaller * PermutationCount(elements.Length - i - 1);
        }
        return rank;
    }

    public static bool NextLexicographicPermutation(Span<int> elements) {
        var pivot = elements.Length - 2;
        while (pivot >= 0 && elements[pivot] >= elements[pivot + 1]) { --pivot; }
        if (pivot < 0) { return false; }
        var successor = elements.Length - 1;
        while (elements[successor] <= elements[pivot]) { --successor; }
        (elements[pivot], elements[successor]) = (elements[successor], elements[pivot]);
        elements[(pivot + 1)..].Reverse();
        return true;
    }
}
