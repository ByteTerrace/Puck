#:project ../src/Puck.Maths/Puck.Maths.csproj

// Standalone exact verifier for the odd-characteristic field primitives promoted into Puck.Maths:
// PrimeField64, QuadraticExtensionField64, and NumberTheoryFunctions. Four independent checks:
//   (i)   random-case cross-check of every operation class against BigInteger reference arithmetic;
//   (ii)  recomputation of the finite-field Lerch identities (5),(8),(9),(13),(14) of
//         the relocated maths corpus (ByteTerrace/Temp/Maths/docs/polynomial-tail-hasse-lerch-quotient.md) on the hasse-lerch verifier's default box,
//         reproducing 3,525 good inert instances across 128 parameter pairs EXACTLY;
//   (iii) TrySqrt over random (p, a): a returned root squares back, else the value is a nonresidue;
//   (iv)  HenselLiftRoot of x^2 - Delta_c from mod l to mod l^6, verified by direct BigInteger arithmetic.
//
// Run as:
//   dotnet run --property:NuGetAudit=false tools/prime-field-verifier.cs

using System.Numerics;
using Puck.Maths;
using Element = Puck.Maths.QuadraticExtensionField64.Element;

const int OperationSamples = 100_000;
const int SqrtSamples = 10_000;

var rng = new Random(0x50726D46);   // deterministic seed

// A spread of prime moduli: small edge cases plus random large primes near the 2^62 ceiling that
// exercise the UInt128 reduction path.
var moduli = new List<ulong> { 3, 5, 7, 97, 65537, 2147483647 };
while (moduli.Count < 24)
{
    var candidate = (ulong)rng.NextInt64(1L << 40, 1L << 62) | 1UL;
    while (!PrimeField64.IsPrime(candidate))
    {
        candidate += 2;
    }

    moduli.Add(candidate);
}

// ============================ (i) BigInteger cross-check ============================
{
    var checks = 0L;
    for (var i = 0; i < OperationSamples; i++)
    {
        var p = moduli[rng.Next(moduli.Count)];
        var field = PrimeField64.Create(p);
        var big = new BigInteger(p);
        var a = (ulong)rng.NextInt64((long)p);
        var b = (ulong)rng.NextInt64((long)p);

        Require(field.Add(a, b) == (ulong)((a + (BigInteger)b) % big), "PrimeField64.Add");
        Require(field.Subtract(a, b) == (ulong)((((a - (BigInteger)b) % big) + big) % big), "PrimeField64.Subtract");
        Require(field.Multiply(a, b) == (ulong)(((BigInteger)a * b) % big), "PrimeField64.Multiply");
        Require(field.Negate(a) == (ulong)(((big - a) % big)), "PrimeField64.Negate");

        var e = (ulong)rng.NextInt64();
        Require(field.Pow(a, e) == (ulong)BigInteger.ModPow(a, e, big), "PrimeField64.Pow");

        if (a != 0)
        {
            var inv = field.Inverse(a);
            Require(inv == (ulong)BigInteger.ModPow(a, big - 2, big), "PrimeField64.Inverse");
            Require(field.Multiply(a, inv) == 1UL, "PrimeField64.Inverse round-trip");
        }

        checks += 6;
    }

    // Batch inversion vs the naive per-element inverse.
    for (var i = 0; i < 2_000; i++)
    {
        var p = moduli[rng.Next(moduli.Count)];
        var field = PrimeField64.Create(p);
        var n = 1 + rng.Next(600);
        var values = new ulong[n];
        var expected = new ulong[n];
        for (var j = 0; j < n; j++)
        {
            var v = (ulong)rng.NextInt64(1, (long)p);
            values[j] = v;
            expected[j] = field.Inverse(v);
        }

        field.BatchInverse(values);
        for (var j = 0; j < n; j++)
        {
            Require(values[j] == expected[j], "PrimeField64.BatchInverse");
        }

        checks += n;
    }

    // Extension-field operation classes.
    for (var i = 0; i < OperationSamples; i++)
    {
        var p = moduli[rng.Next(moduli.Count)];
        var field = PrimeField64.Create(p);
        var d = QuadraticExtensionField64.SmallestNonSquare(field);
        var ext = QuadraticExtensionField64.Create(field, d);
        var big = new BigInteger(p);
        var bigD = new BigInteger(d);

        var la = (ulong)rng.NextInt64((long)p);
        var lb = (ulong)rng.NextInt64((long)p);
        var ra = (ulong)rng.NextInt64((long)p);
        var rb = (ulong)rng.NextInt64((long)p);
        var left = new Element(la, lb);
        var right = new Element(ra, rb);

        var add = ext.Add(left, right);
        Require(add.A == (ulong)((la + (BigInteger)ra) % big) && add.B == (ulong)((lb + (BigInteger)rb) % big), "ext.Add");

        var mul = ext.Multiply(left, right);
        var expA = (ulong)((((BigInteger)la * ra) + ((BigInteger)lb * rb % big * bigD)) % big);
        var expB = (ulong)((((BigInteger)la * rb) + ((BigInteger)lb * ra)) % big);
        Require(mul.A == expA && mul.B == expB, "ext.Multiply");

        var norm = ext.Norm(left);
        var expNorm = (ulong)(((((BigInteger)la * la) - ((BigInteger)lb * lb % big * bigD)) % big + big) % big);
        Require(norm == expNorm, "ext.Norm");

        Require(ext.Trace(left) == (ulong)((2 * (BigInteger)la) % big), "ext.Trace");
        Require(ext.Frobenius(left) == new Element(la, lb == 0 ? 0 : p - lb), "ext.Frobenius");

        // Frobenius equals the base-characteristic power for every element.
        Require(ext.Pow(left, p) == ext.Frobenius(left), "ext.Frobenius == Pow(.,p)");

        if (norm != 0)
        {
            var eInv = ext.Inverse(left);
            Require(ext.Multiply(left, eInv) == ext.One, "ext.Inverse round-trip");
        }

        // Small-exponent path against a direct product; the huge-exponent path is covered by the
        // Frobenius == Pow(.,p) identity above (exponent p up to 2^62) and by (ii).
        var exp = (ulong)rng.NextInt64(300);
        var slow = ext.One;
        for (var k = 0UL; k < exp; k++)
        {
            slow = ext.Multiply(slow, left);
        }
        Require(ext.Pow(left, exp) == slow, "ext.Pow");

        checks += 7;
    }

    Console.WriteLine($"(i)   BigInteger cross-check PASSED: {checks:N0} operation checks across {moduli.Count} moduli.");
}

// ============================ (ii) Lerch identities, 3,525 / 128 ============================
{
    var checkedInstances = 0;
    var checkedParameters = 0;
    foreach (var p in Enumerable.Range(1, 12))
    {
        foreach (var r in Enumerable.Range(1, 12))
        {
            var disc = (long)p * p + 4L * r;
            var parameterChecks = 0;
            foreach (var prime in PrimesThrough(251))
            {
                if (Mod(2L * p * r * disc, prime) == 0 || Legendre(disc, prime) != -1)
                {
                    continue;
                }

                var field = PrimeField64.Create((ulong)prime);
                var ext = QuadraticExtensionField64.Create(field, (ulong)Mod(disc, prime));
                var one = ext.One;
                var surd = new Element(0, 1);                 // sqrt(disc)
                var half = ext.FromBase(field.Inverse(2));
                var pElem = ext.FromBase(field.Reduce((long)p));
                var lambda = ext.Multiply(ext.Add(pElem, surd), half);
                var mu = ext.Multiply(ext.Subtract(surd, pElem), half);
                var a = ext.Multiply(mu, ext.Inverse(surd));
                var x = ext.Negate(ext.Multiply(mu, ext.Inverse(lambda)));

                // Frobenius law (7).
                Require(ext.Pow(surd, (ulong)prime) == ext.Negate(surd), $"D Frobenius ({p},{r}) mod {prime}");
                Require(ext.Pow(a, (ulong)prime) == ext.Subtract(one, a), $"a Frobenius ({p},{r}) mod {prime}");
                Require(ext.Pow(x, (ulong)prime) == ext.Inverse(x), $"x Frobenius ({p},{r}) mod {prime}");

                // S_l via batch-inverted denominators (a + j), j = 0..prime-2.
                var m = prime - 1;
                var denom = new Element[m];
                var running = a;
                for (var j = 0; j < m; j++)
                {
                    denom[j] = running;
                    running = ext.Add(running, one);
                }

                ext.BatchInverse(denom);

                var sum = ext.Zero;
                var power = one;
                for (var j = 0; j < m; j++)
                {
                    sum = ext.Add(sum, ext.Multiply(power, denom[j]));
                    power = ext.Multiply(power, x);
                }

                // Identity (8): S_l^l = -x^2 S_l + (1-x)^2.
                var oneMinusX = ext.Subtract(one, x);
                var sumFrobenius = ext.Add(
                    ext.Negate(ext.Multiply(ext.Multiply(x, x), sum)),
                    ext.Multiply(oneMinusX, oneMinusX));
                Require(ext.Pow(sum, (ulong)prime) == sumFrobenius, $"(8) S_l Frobenius ({p},{r}) mod {prime}");

                // L_l = a S_l + 1/x. Identity (9): L_l^l = x L_l.
                var lerch = ext.Add(ext.Multiply(a, sum), ext.Inverse(x));
                Require(ext.Pow(lerch, (ulong)prime) == ext.Multiply(x, lerch), $"(9) quotient Frobenius ({p},{r}) mod {prime}");

                // Identity (5): M_l = mu L_l descends to F_l and h_l = M_l - p matches the recurrence.
                var kernel = ext.Multiply(mu, lerch);
                Require(kernel.B == 0, $"(5) kernel descent ({p},{r}) mod {prime}");
                var predicted = (int)Mod((long)kernel.A - p, prime);
                var recurrence = KernelIncrement(p, r, prime);
                Require(recurrence == predicted, $"(5) recurrence/Lerch ({p},{r}) mod {prime}: {recurrence} != {predicted}");

                // Independent Hasse polynomial: H = 2F1(2,2;a+2;y) truncated at l-2, y = a.
                var hValue = one;
                var hDerivative = ext.Zero;
                var coefficient = one;
                var yPower = one;
                var previousPower = one;
                var c = ext.Add(a, ext.FromBase(field.Reduce(2L)));
                for (var j = 1; j <= prime - 2; j++)
                {
                    coefficient = ext.Multiply(coefficient, ext.FromBase(field.Reduce((long)(j + 1))));
                    coefficient = ext.Multiply(coefficient, ext.FromBase(field.Reduce((long)(j + 1))));
                    coefficient = ext.Multiply(coefficient, ext.Inverse(ext.Add(c, ext.FromBase(field.Reduce((long)(j - 1))))));
                    coefficient = ext.Multiply(coefficient, ext.Inverse(ext.FromBase(field.Reduce((long)j))));
                    previousPower = yPower;
                    yPower = ext.Multiply(yPower, a);
                    hValue = ext.Add(hValue, ext.Multiply(coefficient, yPower));
                    hDerivative = ext.Add(hDerivative, ext.Multiply(ext.FromBase(field.Reduce((long)j)), ext.Multiply(coefficient, previousPower)));
                }

                // Identity (13): H(a) = (1+a)/(1-a).
                Require(hValue == ext.Multiply(ext.Add(one, a), ext.Inverse(ext.Subtract(one, a))), $"(13) closed Hasse value ({p},{r}) mod {prime}");

                // Identity (14): H'/H = (1 + a S_l)/(1-a) - 2/a, and (r/D) H'/H = h_l.
                var logDerivative = ext.Multiply(hDerivative, ext.Inverse(hValue));
                var identity14 = ext.Subtract(
                    ext.Multiply(ext.Add(one, ext.Multiply(a, sum)), ext.Inverse(ext.Subtract(one, a))),
                    ext.Multiply(ext.FromBase(field.Reduce(2L)), ext.Inverse(a)));
                Require(logDerivative == identity14, $"(14) log-derivative form ({p},{r}) mod {prime}");
                var jacobi = ext.Multiply(ext.Multiply(ext.FromBase(field.Reduce((long)r)), ext.Inverse(surd)), logDerivative);
                Require(jacobi.B == 0 && (int)jacobi.A == predicted, $"(14) Jacobi/Lerch ({p},{r}) mod {prime}");

                checkedInstances++;
                parameterChecks++;
            }

            if (parameterChecks > 0)
            {
                checkedParameters++;
            }
        }
    }

    Require(checkedInstances == 3525, $"expected 3,525 instances, got {checkedInstances}");
    Require(checkedParameters == 128, $"expected 128 parameter pairs, got {checkedParameters}");
    Console.WriteLine($"(ii)  Lerch identities (5),(8),(9),(13),(14) REPRODUCED: {checkedInstances:N0} good inert instances across {checkedParameters:N0} parameter pairs.");
}

// ============================ (iii) TrySqrt ============================
{
    var squares = 0;
    var nonresidues = 0;
    for (var i = 0; i < SqrtSamples; i++)
    {
        var p = moduli[6 + rng.Next(moduli.Count - 6)];   // large primes only
        var field = PrimeField64.Create(p);
        var a = (ulong)rng.NextInt64((long)p);

        if (field.TrySqrt(a, out var root))
        {
            Require(field.Multiply(root, root) == a, $"TrySqrt root mod {p} for {a}");
            squares++;
        }
        else
        {
            Require(field.LegendreCharacter(a) == -1, $"TrySqrt rejected a residue mod {p} for {a}");
            nonresidues++;
        }
    }

    Console.WriteLine($"(iii) TrySqrt PASSED: {SqrtSamples:N0} random (p,a) — {squares:N0} roots verified, {nonresidues:N0} nonresidues confirmed.");
}

// ============================ (iv) HenselLiftRoot ============================
{
    var lifts = 0;
    for (var p = 1; p <= 12; p++)
    {
        for (var r = 1; r <= 12; r++)
        {
            var disc = (long)p * p + 4L * r;
            if (IsPerfectSquare(disc))
            {
                continue;
            }

            foreach (var prime in PrimesThrough(4000))
            {
                if (prime < 11 || Mod(disc, prime) == 0 || Legendre(disc, prime) != 1)
                {
                    continue;   // need Delta a nonzero quadratic residue for a root to exist mod l
                }

                var field = PrimeField64.Create((ulong)prime);
                if (!field.TrySqrt((ulong)Mod(disc, prime), out var rootModL))
                {
                    Require(false, $"Delta residue had no root mod {prime}");
                }

                var coefficients = new BigInteger[] { -disc, 0, 1 };   // x^2 - Delta
                var lifted = NumberTheoryFunctions.HenselLiftRoot(coefficients, rootModL, prime, 6);
                var modulus = BigInteger.Pow(prime, 6);
                Require((lifted * lifted - disc) % modulus == 0, $"Hensel lift x^2-{disc} mod {prime}^6");
                Require((lifted - rootModL) % prime == 0, $"Hensel lift base congruence mod {prime}");
                lifts++;

                if (lifts % 37 == 0)
                {
                    break;   // one representative prime per residue pattern keeps the pass brisk
                }
            }
        }
    }

    Console.WriteLine($"(iv)  HenselLiftRoot PASSED: {lifts:N0} roots of x^2-Delta_c lifted from mod l to mod l^6 and verified.");
}

Console.WriteLine();
Console.WriteLine("PRIME-FIELD PRIMITIVES VERIFIED: all four checks passed.");

static int KernelIncrement(long p, long r, int prime)
{
    var constant0 = 1;
    var constant1 = 0;
    var linear0 = 0;
    var linear1 = 1;
    for (var n = 0; n < prime; n++)
    {
        var b = r * (n + 2L) * (n + 2L);
        var a = p * (n + 2L);
        var nextConstant = (int)Mod(b * constant0 - a * constant1, prime);
        var nextLinear = (int)Mod(b * linear0 - a * linear1, prime);
        constant0 = constant1;
        constant1 = nextConstant;
        linear0 = linear1;
        linear1 = nextLinear;
    }

    for (var increment = 0; increment < prime; increment++)
    {
        if (Mod(constant0 + (long)increment * linear0, prime) == 0 &&
            Mod(constant1 + (long)increment * linear1, prime) == 0)
        {
            return increment;
        }
    }

    return -1;
}

static IEnumerable<int> PrimesThrough(int bound)
{
    for (var candidate = 3; candidate <= bound; candidate += 2)
    {
        var isPrime = true;
        for (var divisor = 3; divisor * divisor <= candidate; divisor += 2)
        {
            if (candidate % divisor == 0)
            {
                isPrime = false;
                break;
            }
        }

        if (isPrime)
        {
            yield return candidate;
        }
    }
}

static bool IsPerfectSquare(long value)
{
    if (value < 0)
    {
        return false;
    }

    var root = (long)Math.Sqrt(value);
    for (var candidate = Math.Max(0, root - 2); candidate <= root + 2; candidate++)
    {
        if (candidate * candidate == value)
        {
            return true;
        }
    }

    return false;
}

static long Mod(long value, int prime)
{
    var result = value % prime;
    return result < 0 ? result + prime : result;
}

static int Legendre(long value, int prime)
{
    var residue = (int)Mod(value, prime);
    if (residue == 0)
    {
        return 0;
    }

    return BigInteger.ModPow(residue, (prime - 1) / 2, prime) == 1 ? 1 : -1;
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
