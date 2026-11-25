using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PzServerUi.Application;

public sealed record ExternalServerScan(bool Available, IReadOnlyDictionary<string, ExternalServerStatus> Profiles)
{
    public ExternalServerStatus GetStatus(string profile) => Profiles.GetValueOrDefault(profile);
}

public static class WindowsExternalServers
{
    private static readonly ExternalServerScan Unavailable = new(false,
        new Dictionary<string, ExternalServerStatus>(StringComparer.OrdinalIgnoreCase));

    public static Task<ExternalServerScan> ScanAsync(string installationFolder) =>
        Task.Run(() => OperatingSystem.IsWindows() ? ScanWindows(installationFolder) : Unavailable);

    [SupportedOSPlatform("windows")]
    private static ExternalServerScan ScanWindows(string installationFolder)
    {
        try
        {
            using var user = WindowsIdentity.GetCurrent();
            if (user.User is not { } sid)
                return Unavailable;

            var allowedImages = ExternalServerDiscovery.Executables(installationFolder);
            var candidates = new List<DedicatedProcessCandidate>();
            foreach (var image in new[] { "java", "javaw" })
            {
                foreach (var process in Process.GetProcessesByName(image))
                {
                    using (process)
                    {
                        var candidate = ReadCandidate(process.Id, sid, allowedImages);
                        if (candidate is not null)
                            candidates.Add(candidate);
                    }
                }
            }
            return new(true, ExternalServerDiscovery.Match(installationFolder, candidates));
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return Unavailable;
        }
    }

    [SupportedOSPlatform("windows")]
    private static DedicatedProcessCandidate? ReadCandidate(int pid, SecurityIdentifier sid, HashSet<string> allowedImages)
    {
        const uint processQueryLimitedInformation = 0x1000;
        const uint tokenQuery = 0x0008;
        using var handle = OpenProcess(processQueryLimitedInformation, false, pid);
        if (handle.IsInvalid)
            return null;

        var image = new StringBuilder(32_768);
        var length = image.Capacity;
        if (!QueryFullProcessImageNameW(handle, 0, image, ref length))
            return null;
        var path = image.ToString();
        if (!allowedImages.Contains(path) || !OpenProcessToken(handle, tokenQuery, out var token))
            return null;

        using (token)
        {
            try
            {
                using var owner = new WindowsIdentity(token.DangerousGetHandle());
                if (owner.User != sid)
                    return null;
            }
            catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null;
            }
        }

        // Reads one specific OS metadata record, not arbitrary process memory.
        var commandLine = ReadCommandLine(handle);
        return commandLine is null ? null : new(path, Split(commandLine));
    }

    private static string? ReadCommandLine(SafeProcessHandle process)
    {
        const int processCommandLineInformation = 60;
        _ = NtQueryInformationProcess(process, processCommandLineInformation, IntPtr.Zero, 0, out var required);
        if (required < Marshal.SizeOf<UnicodeString>() || required > 131_072)
            return null;

        var buffer = Marshal.AllocHGlobal(required);
        try
        {
            if (NtQueryInformationProcess(process, processCommandLineInformation, buffer, required, out _) != 0)
                return null; // Access denied, process exited, or unsupported: do not guess.
            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            var offset = value.Buffer.ToInt64() - buffer.ToInt64();
            if (value.Length == 0 || value.Length % 2 != 0 || offset < Marshal.SizeOf<UnicodeString>()
                || offset > required - value.Length)
                return null;
            return Marshal.PtrToStringUni(value.Buffer, value.Length / 2);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string[] Split(string commandLine)
    {
        var arguments = CommandLineToArgvW(commandLine, out var count);
        if (arguments == IntPtr.Zero)
            return [];
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, i * IntPtr.Size)) ?? "";
            return result;
        }
        finally { LocalFree(arguments); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass,
        IntPtr buffer, int bufferLength, out int returnLength);

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
