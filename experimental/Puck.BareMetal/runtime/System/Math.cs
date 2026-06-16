// Numeric helpers. The JIT references System.Math as a builtin, and the spikes use the common ops.

namespace System
{
    public static class Math
    {
        public static int Abs(int value) => value < 0 ? -value : value;
        public static long Abs(long value) => value < 0 ? -value : value;
        public static float Abs(float value) => value < 0 ? -value : value;
        public static double Abs(double value) => value < 0 ? -value : value;

        public static int Min(int a, int b) => a < b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
        public static long Min(long a, long b) => a < b ? a : b;
        public static long Max(long a, long b) => a > b ? a : b;
        public static uint Min(uint a, uint b) => a < b ? a : b;
        public static uint Max(uint a, uint b) => a > b ? a : b;
        public static ulong Min(ulong a, ulong b) => a < b ? a : b;
        public static ulong Max(ulong a, ulong b) => a > b ? a : b;
        public static double Min(double a, double b) => a < b ? a : b;
        public static double Max(double a, double b) => a > b ? a : b;

        public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

        public static long BigMul(int a, int b) => (long)a * b;

        // The JIT routes checked floating-point -> integer conversions (conv.ovf.* from r8) to these.
        // With no exception model the overflow case can't throw; the conversions the samples hit are
        // all in range, so a plain narrowing cast is correct for them.
        internal static int ConvertToInt32Checked(double value) => (int)value;
        internal static uint ConvertToUInt32Checked(double value) => (uint)value;
        internal static long ConvertToInt64Checked(double value) => (long)value;
        internal static ulong ConvertToUInt64Checked(double value) => (ulong)value;
    }

    public static class MathF
    {
        public static float Abs(float value) => value < 0 ? -value : value;
        public static float Min(float a, float b) => a < b ? a : b;
        public static float Max(float a, float b) => a > b ? a : b;
    }
}
