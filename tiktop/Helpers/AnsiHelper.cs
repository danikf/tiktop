using System;
using System.Runtime.InteropServices;
using System.Text;

namespace tiktop.Helpers
{
    internal static class AnsiHelper
    {
        private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        public static void EnableVT()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            var handle = GetStdHandle(-11); // STD_OUTPUT_HANDLE
            if (GetConsoleMode(handle, out uint mode))
                SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
        }

        // Indexed by (int)ConsoleColor 0..15
        private static readonly string[] _fgCodes =
        {
            "\x1B[30m", "\x1B[34m", "\x1B[32m", "\x1B[36m",
            "\x1B[31m", "\x1B[35m", "\x1B[33m", "\x1B[37m",
            "\x1B[90m", "\x1B[94m", "\x1B[92m", "\x1B[96m",
            "\x1B[91m", "\x1B[95m", "\x1B[93m", "\x1B[97m",
        };

        private static readonly string[] _bgCodes =
        {
            "\x1B[40m",  "\x1B[44m",  "\x1B[42m",  "\x1B[46m",
            "\x1B[41m",  "\x1B[45m",  "\x1B[43m",  "\x1B[47m",
            "\x1B[100m", "\x1B[104m", "\x1B[102m", "\x1B[106m",
            "\x1B[101m", "\x1B[105m", "\x1B[103m", "\x1B[107m",
        };

        public const string Reset = "\x1B[0m";

        public static string Fg(ConsoleColor c) => _fgCodes[(int)c];
        public static string Bg(ConsoleColor c) => _bgCodes[(int)c];

        public static void AppendMove(StringBuilder sb, int row, int col = 0)
            => sb.Append("\x1B[").Append(row + 1).Append(';').Append(col + 1).Append('H');
    }
}
