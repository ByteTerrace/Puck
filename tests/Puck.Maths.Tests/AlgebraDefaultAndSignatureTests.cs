using System.Numerics;
using Xunit;

using BigMonogenic = Puck.Maths.MonogenicAlgebra<System.Numerics.BigInteger>;

namespace Puck.Maths.Tests;

public sealed class AlgebraDefaultAndSignatureTests {
    [Theory]
    [InlineData(4, 0, 0)]
    [InlineData(0, 4, 0)]
    [InlineData(0, 0, 4)]
    [InlineData(1, 2, 1)]
    public void GeometricSignatureAdmissionAcceptsEveryExactCapacityPartition(
        int positiveCount,
        int negativeCount,
        int degenerateCount
    ) {
        var algebra = GeometricAlgebra.Create(
            positiveCount: positiveCount,
            negativeCount: negativeCount,
            degenerateCount: degenerateCount
        );

        Assert.Equal(expected: 4, actual: algebra.GeneratorCount);
        Assert.Equal(expected: Multivector.BladeCapacity, actual: algebra.BladeCount);
    }

    [Theory]
    [InlineData(int.MaxValue, 0, 0, "positiveCount")]
    [InlineData(0, int.MaxValue, 0, "negativeCount")]
    [InlineData(0, 0, int.MaxValue, "degenerateCount")]
    [InlineData(int.MaxValue, 1, 0, "positiveCount")]
    [InlineData(1, int.MaxValue, 1, "negativeCount")]
    [InlineData(4, 1, 0, "negativeCount")]
    [InlineData(2, 2, 1, "degenerateCount")]
    public void GeometricSignatureAdmissionRejectsOversizedAndOverflowingTotalsAgainstPublicParameters(
        int positiveCount,
        int negativeCount,
        int degenerateCount,
        string expectedParamName
    ) {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            testCode: () => GeometricAlgebra.Create(
                positiveCount: positiveCount,
                negativeCount: negativeCount,
                degenerateCount: degenerateCount
            )
        );

        Assert.Equal(expected: expectedParamName, actual: exception.ParamName);
    }

    [Fact]
    public void DefaultGeometricDescriptorIsTheCanonicalScalarAlgebraAcrossItsSemanticSurface() {
        var algebra = default(GeometricAlgebra);
        var canonical = GeometricAlgebra.Create(positiveCount: 0, negativeCount: 0, degenerateCount: 0);
        var left = Multivector.Scalar(value: FixedQ4816.FromRawBits(value: -196609L));
        var right = Multivector.Scalar(value: FixedQ4816.FromRawBits(value: 32769L));
        var one = Multivector.Scalar(value: FixedQ4816.One);
        var zero = default(Multivector);

        Assert.Equal(expected: canonical.PositiveCount, actual: algebra.PositiveCount);
        Assert.Equal(expected: canonical.NegativeCount, actual: algebra.NegativeCount);
        Assert.Equal(expected: canonical.DegenerateCount, actual: algebra.DegenerateCount);
        Assert.Equal(expected: canonical.GeneratorCount, actual: algebra.GeneratorCount);
        Assert.Equal(expected: canonical.BladeCount, actual: algebra.BladeCount);
        Assert.Equal(expected: canonical.GeometricProduct(left: left, right: right), actual: algebra.GeometricProduct(left: left, right: right));
        Assert.Equal(expected: canonical.Reverse(value: left), actual: algebra.Reverse(value: left));
        Assert.Equal(expected: canonical.GradeProjection(value: left, grade: 0), actual: algebra.GradeProjection(value: left, grade: 0));
        Assert.Equal(expected: canonical.IsEven(value: left), actual: algebra.IsEven(value: left));
        Assert.Equal(expected: canonical.Exponential(bivector: zero), actual: algebra.Exponential(bivector: zero));
        Assert.Equal(
            expected: canonical.SandwichTransform(motor: one, vector: left),
            actual: algebra.SandwichTransform(motor: one, vector: left)
        );

        Assert.Equal(
            expected: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => canonical.Square(generatorIndex: 0)).ParamName,
            actual: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => algebra.Square(generatorIndex: 0)).ParamName
        );
    }

    [Fact]
    public void GeometricSemanticOperationsRejectEveryNonzeroLaneOutsideTheReceiverSignature() {
        var algebra = GeometricAlgebra.Create(positiveCount: 1, negativeCount: 0, degenerateCount: 0);
        var one = Multivector.Scalar(value: FixedQ4816.One);
        var foreign = Multivector.FromCoefficients(
            coefficients: [FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.One]
        );

        AssertArgumentException(
            expectedParamName: "left",
            action: () => algebra.GeometricProduct(left: foreign, right: one)
        );
        AssertArgumentException(
            expectedParamName: "right",
            action: () => algebra.GeometricProduct(left: one, right: foreign)
        );
        AssertArgumentException(expectedParamName: "value", action: () => algebra.Reverse(value: foreign));
        AssertArgumentException(
            expectedParamName: "value",
            action: () => algebra.GradeProjection(value: foreign, grade: 0)
        );
        AssertArgumentException(expectedParamName: "value", action: () => algebra.IsEven(value: foreign));
        AssertArgumentException(expectedParamName: "bivector", action: () => algebra.Exponential(bivector: foreign));
        AssertArgumentException(
            expectedParamName: "motor",
            action: () => algebra.SandwichTransform(motor: foreign, vector: one)
        );
        AssertArgumentException(
            expectedParamName: "vector",
            action: () => algebra.SandwichTransform(motor: one, vector: foreign)
        );
    }

    [Fact]
    public void GeometricGradeProjectionValidatesTheSignatureGradeRange() {
        var algebra = GeometricAlgebra.Create(positiveCount: 1, negativeCount: 1, degenerateCount: 0);
        var value = Multivector.Scalar(value: FixedQ4816.One);

        Assert.Equal(
            expected: "grade",
            actual: Assert.Throws<ArgumentOutOfRangeException>(
                testCode: () => algebra.GradeProjection(value: value, grade: -1)
            ).ParamName
        );
        Assert.Equal(
            expected: "grade",
            actual: Assert.Throws<ArgumentOutOfRangeException>(
                testCode: () => algebra.GradeProjection(value: value, grade: 3)
            ).ParamName
        );
    }

    [Fact]
    public void DefaultMonogenicDescriptorDeliberatelyRejectsItsWholePublicSemanticSurface() {
        var algebra = default(BigMonogenic);
        var element = default(BigMonogenic.Element);
        var window = default(BigMonogenic.Projective);

        Assert.Throws<InvalidOperationException>(testCode: () => _ = algebra.Degree);
        Assert.Throws<InvalidOperationException>(testCode: () => _ = algebra.Modulus.Length);
        Assert.Throws<InvalidOperationException>(testCode: () => _ = algebra.One);
        Assert.Throws<InvalidOperationException>(testCode: () => _ = algebra.Root);
        Assert.Throws<InvalidOperationException>(testCode: () => _ = algebra.Zero);
        Assert.Throws<InvalidOperationException>(testCode: () => algebra.FromCoordinates(coordinates: [BigInteger.Zero]));
        Assert.Throws<InvalidOperationException>(testCode: () => algebra.FromWindow(window: [BigInteger.Zero]));
        Assert.Throws<InvalidOperationException>(testCode: () => algebra.Add(left: element, right: element));
        Assert.Throws<InvalidOperationException>(testCode: () => algebra.Subtract(left: element, right: element));
        Assert.Throws<InvalidOperationException>(testCode: () => algebra.Negate(value: element));
        Assert.Throws<InvalidOperationException>(testCode: () => algebra.Multiply(left: element, right: element));
        Assert.Throws<InvalidOperationException>(testCode: () => algebra.MultiplyByRoot(value: element));
        Assert.Throws<InvalidOperationException>(testCode: () => algebra.CompanionPower(exponent: 0));
        Assert.Throws<InvalidOperationException>(testCode: () => algebra.ProjectiveStep(window: window));
        Assert.Throws<InvalidOperationException>(testCode: () => algebra.Trace(value: element));
        Assert.Throws<InvalidOperationException>(testCode: () => algebra.Norm(value: element));
        Assert.Throws<InvalidOperationException>(testCode: () => algebra.CharacteristicDiscriminant());
    }

    [Fact]
    public void DefaultMonogenicNestedValuesDeliberatelyRejectEveryAccessor() {
        var element = default(BigMonogenic.Element);
        var window = default(BigMonogenic.Projective);

        Assert.Throws<InvalidOperationException>(testCode: () => _ = element.Coordinates.Length);
        Assert.Throws<InvalidOperationException>(testCode: () => _ = element.Dimension);
        Assert.Throws<InvalidOperationException>(testCode: () => _ = element[0]);
        Assert.Throws<InvalidOperationException>(testCode: () => _ = window.Coordinates.Length);
        Assert.Throws<InvalidOperationException>(testCode: () => _ = window.Dimension);
        Assert.Throws<InvalidOperationException>(testCode: () => _ = window[0]);
    }

    [Fact]
    public void MonogenicElementConsumersRejectDefaultShortAndLongOperandsBeforeComputing() {
        var receiver = BigMonogenic.Create(monicModulus: [BigInteger.One, BigInteger.One]);
        var valid = receiver.FromCoordinates(coordinates: [new BigInteger(2), new BigInteger(3)]);
        var shortValue = BigMonogenic.Create(monicModulus: [BigInteger.One])
            .FromCoordinates(coordinates: [new BigInteger(5)]);
        var longValue = BigMonogenic.Create(monicModulus: [BigInteger.One, BigInteger.One, BigInteger.One])
            .FromCoordinates(coordinates: [new BigInteger(7), new BigInteger(11), new BigInteger(13)]);

        AssertElementConsumersReject(receiver: receiver, malformed: default, valid: valid);
        AssertElementConsumersReject(receiver: receiver, malformed: shortValue, valid: valid);
        AssertElementConsumersReject(receiver: receiver, malformed: longValue, valid: valid);
    }

    [Fact]
    public void MonogenicProjectiveConsumerRejectsDefaultShortAndLongWindowsBeforeComputing() {
        var receiver = BigMonogenic.Create(monicModulus: [BigInteger.One, BigInteger.One]);
        var shortWindow = BigMonogenic.Create(monicModulus: [BigInteger.One])
            .FromWindow(window: [new BigInteger(5)]);
        var longWindow = BigMonogenic.Create(monicModulus: [BigInteger.One, BigInteger.One, BigInteger.One])
            .FromWindow(window: [new BigInteger(7), new BigInteger(11), new BigInteger(13)]);

        AssertArgumentException(
            expectedParamName: "window",
            action: () => receiver.ProjectiveStep(window: default)
        );
        AssertArgumentException(
            expectedParamName: "window",
            action: () => receiver.ProjectiveStep(window: shortWindow)
        );
        AssertArgumentException(
            expectedParamName: "window",
            action: () => receiver.ProjectiveStep(window: longWindow)
        );
    }

    [Fact]
    public void MonogenicReceiverIntentionallyReinterpretsSameDimensionForeignCoordinates() {
        var receiver = BigMonogenic.Create(monicModulus: [BigInteger.One, new BigInteger(2)]);
        var foreign = BigMonogenic.Create(monicModulus: [new BigInteger(5), new BigInteger(7)]);
        var coordinates = new BigInteger[] { new(11), new(13) };
        var localElement = receiver.FromCoordinates(coordinates: coordinates);
        var foreignElement = foreign.FromCoordinates(coordinates: coordinates);
        var localWindow = receiver.FromWindow(window: coordinates);
        var foreignWindow = foreign.FromWindow(window: coordinates);

        AssertElementEqual(
            expected: receiver.Add(left: localElement, right: localElement),
            actual: receiver.Add(left: foreignElement, right: foreignElement)
        );
        AssertElementEqual(
            expected: receiver.Subtract(left: localElement, right: localElement),
            actual: receiver.Subtract(left: foreignElement, right: foreignElement)
        );
        AssertElementEqual(expected: receiver.Negate(value: localElement), actual: receiver.Negate(value: foreignElement));
        AssertElementEqual(
            expected: receiver.Multiply(left: localElement, right: localElement),
            actual: receiver.Multiply(left: foreignElement, right: foreignElement)
        );
        AssertElementEqual(
            expected: receiver.MultiplyByRoot(value: localElement),
            actual: receiver.MultiplyByRoot(value: foreignElement)
        );
        Assert.Equal(expected: receiver.Trace(value: localElement), actual: receiver.Trace(value: foreignElement));
        Assert.Equal(expected: receiver.Norm(value: localElement), actual: receiver.Norm(value: foreignElement));
        AssertWindowEqual(
            expected: receiver.ProjectiveStep(window: localWindow),
            actual: receiver.ProjectiveStep(window: foreignWindow)
        );
    }

    private static void AssertElementConsumersReject(
        BigMonogenic receiver,
        BigMonogenic.Element malformed,
        BigMonogenic.Element valid
    ) {
        AssertArgumentException(expectedParamName: "left", action: () => receiver.Add(left: malformed, right: valid));
        AssertArgumentException(expectedParamName: "right", action: () => receiver.Add(left: valid, right: malformed));
        AssertArgumentException(expectedParamName: "left", action: () => receiver.Subtract(left: malformed, right: valid));
        AssertArgumentException(expectedParamName: "right", action: () => receiver.Subtract(left: valid, right: malformed));
        AssertArgumentException(expectedParamName: "value", action: () => receiver.Negate(value: malformed));
        AssertArgumentException(expectedParamName: "left", action: () => receiver.Multiply(left: malformed, right: valid));
        AssertArgumentException(expectedParamName: "right", action: () => receiver.Multiply(left: valid, right: malformed));
        AssertArgumentException(expectedParamName: "value", action: () => receiver.MultiplyByRoot(value: malformed));
        AssertArgumentException(expectedParamName: "value", action: () => receiver.Trace(value: malformed));
        AssertArgumentException(expectedParamName: "value", action: () => receiver.Norm(value: malformed));
    }

    private static void AssertArgumentException(string expectedParamName, Action action) {
        var exception = Assert.Throws<ArgumentException>(testCode: action);

        Assert.Equal(expected: expectedParamName, actual: exception.ParamName);
    }

    private static void AssertElementEqual(BigMonogenic.Element expected, BigMonogenic.Element actual) =>
        Assert.Equal(expected: expected.Coordinates.ToArray(), actual: actual.Coordinates.ToArray());

    private static void AssertWindowEqual(BigMonogenic.Projective expected, BigMonogenic.Projective actual) =>
        Assert.Equal(expected: expected.Coordinates.ToArray(), actual: actual.Coordinates.ToArray());
}
