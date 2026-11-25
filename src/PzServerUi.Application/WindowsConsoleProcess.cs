using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PzServerUi.Application;

internal static class WindowsConsoleProcess
{
    // Start the relay in its own console even when the panel is launched from a terminal.
    // Inheriting console input would let concurrent relays steal one another's keystrokes.
    public static Process Start(ProcessStartInfo request)
    {
        var command = new StringBuilder($"\"{request.FileName}\"");
        foreach (var argument in request.ArgumentList)
            command.Append(' ').Append(argument); // EncodedCommand and fixed, space-free flags only.

        var environment = string.Join('\0', request.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var block = Marshal.StringToHGlobalUni(environment);
        try
        {
            const uint createNewConsole = 0x00000010;
            const uint createUnicodeEnvironment = 0x00000400;
            if (!CreateProcessW(request.FileName, command, IntPtr.Zero, IntPtr.Zero, false,
                createNewConsole | createUnicodeEnvironment, block, request.WorkingDirectory, ref startup, out var process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try { return Process.GetProcessById(process.ProcessId); }
            finally
            {
                CloseHandle(process.Thread);
                CloseHandle(process.Process);
            }
        }
        finally { Marshal.FreeHGlobal(block); }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startup,
        out ProcessInformation process);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }
}
