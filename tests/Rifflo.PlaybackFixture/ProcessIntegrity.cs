using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class ProcessIntegrity
{
    public static object Read(int pid)
    {
        using var process = Process.GetProcessById(pid);
        if (!OpenProcessToken(process.SafeHandle, 8, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        using (token)
        {
            var elevation = ReadToken(token, 20, pointer => Marshal.ReadInt32(pointer) != 0);
            var integrityRid = ReadToken(token, 25, pointer =>
            {
                var sid = Marshal.ReadIntPtr(pointer);
                var count = Marshal.ReadByte(sid, 1);
                return Marshal.ReadInt32(sid, 8 + (count - 1) * sizeof(int));
            });
            return new { pid, process.SessionId, elevated = elevation, integrityRid, integritySid = $"S-1-16-{integrityRid}" };
        }
    }

    private static T ReadToken<T>(SafeAccessTokenHandle token, int tokenClass, Func<nint, T> read)
    {
        GetTokenInformation(token, tokenClass, 0, 0, out int size);
        if (size == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, tokenClass, buffer, size, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return read(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, int access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int tokenClass, nint buffer,
        int size, out int requiredSize);
}
