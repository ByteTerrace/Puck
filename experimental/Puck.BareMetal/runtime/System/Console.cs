// Platform-agnostic console output. Write(char) is provided per platform; everything else builds on
// it (no string allocation: numbers are emitted digit by digit).

namespace System {
    public static partial class Console {
        public static void Write(string value) {
            if (value is null)
                return;
            for (int i = 0; (i < value.Length); i++)
                Write(value: value[i]);
        }
        public static void Write(bool value) => Write(value: (value ? "True" : "False"));
        public static void Write(int value) => Write(value: (long)value);
        public static void Write(long value) {
            if (value < 0) {
                Write(value: '-');
                Write(value: (ulong)(-value));
            } else {
                Write(value: (ulong)value);
            }
        }
        public static void Write(uint value) => Write(value: (ulong)value);
        public static void Write(ulong value) {
            if (value >= 10)
                Write(value: (value / 10));
            Write(value: (char)('0' + (int)(value % 10)));
        }
        public static void WriteLine() {
            Write(value: '\r');
            Write(value: '\n');
        }
        public static void WriteLine(string value) { Write(value: value); WriteLine(); }
        public static void WriteLine(bool value) { Write(value: value); WriteLine(); }
        public static void WriteLine(char value) { Write(value: value); WriteLine(); }
        public static void WriteLine(int value) { Write(value: value); WriteLine(); }
        public static void WriteLine(long value) { Write(value: value); WriteLine(); }
        public static void WriteLine(uint value) { Write(value: value); WriteLine(); }
        public static void WriteLine(ulong value) { Write(value: value); WriteLine(); }
    }
    public enum ConsoleColor {
        Black = 0, DarkBlue, DarkGreen, DarkCyan, DarkRed, DarkMagenta, DarkYellow, Gray,
        DarkGray, Blue, Green, Cyan, Red, Magenta, Yellow, White
    }
    public enum ConsoleKey { None = 0, LeftArrow = 37, UpArrow = 38, RightArrow = 39, DownArrow = 40 }
    public readonly struct ConsoleKeyInfo {
        public ConsoleKeyInfo(char keyChar, ConsoleKey key, bool shift, bool alt, bool control) {
            KeyChar = keyChar;
            Key = key;
        }

        public ConsoleKey Key { get; }
        public char KeyChar { get; }
    }
}
