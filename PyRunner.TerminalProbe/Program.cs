using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

if (args.Contains("--child", StringComparer.OrdinalIgnoreCase))
{
    Thread.Sleep(Timeout.Infinite);
    return 0;
}

if (args.Contains("--spawn-child", StringComparer.OrdinalIgnoreCase))
{
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--child")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    }) ?? throw new InvalidOperationException("Unable to start child probe");

    Console.WriteLine($"CHILD_PID={child.Id}");
    Console.ReadLine();
    return 0;
}

// ConPTY transports UTF-8. Set both the managed writer and the native console
// code pages explicitly so Console.Write does not let conhost substitute
// non-ASCII characters with U+FFFD on machines using a legacy OEM code page.
SetConsoleCP(65001);
SetConsoleOutputCP(65001);
Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
EnableVirtualTerminalOutput();
var ready = "\u001b[32mREADY 中文\u001b[0m\r\nINPUT>";
WriteConsoleW(GetStdHandle(-11), ready, (uint)ready.Length, out _, IntPtr.Zero);
var value = Console.ReadLine();
Console.WriteLine($"ECHO={value}");
return 7;

static void EnableVirtualTerminalOutput()
{
    const int StdOutputHandle = -11;
    const uint EnableVirtualTerminalProcessing = 0x0004;
    var handle = GetStdHandle(StdOutputHandle);
    if (handle != IntPtr.Zero && handle != new IntPtr(-1) && GetConsoleMode(handle, out var mode))
        SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
}

[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr GetStdHandle(int nStdHandle);

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool SetConsoleCP(uint wCodePageID);

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool SetConsoleOutputCP(uint wCodePageID);

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool WriteConsoleW(
    IntPtr hConsoleOutput,
    string lpBuffer,
    uint nNumberOfCharsToWrite,
    out uint lpNumberOfCharsWritten,
    IntPtr lpReserved);
