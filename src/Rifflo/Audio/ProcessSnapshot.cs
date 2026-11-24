using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Rifflo.Audio;

internal static class ProcessSnapshot
{
    private const uint QueryLimited = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitTimeout = 258;
    private const int InsufficientBuffer = 122;
    private static readonly Guid PropertyStoreId = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PropertyKey AppIdKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    public static Dictionary<uint, ProcessIdentity> Read(string source, IReadOnlyList<ActiveAudioSession> audio,
        IReadOnlyList<string>? competitors = null)
    {
        var snapshotStarted = DateTime.UtcNow.ToFileTimeUtc();
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        var entries = new Dictionary<uint, ProcessEntry>();
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (Process32First(snapshot, ref entry))
        {
            do { entries[entry.ProcessId] = entry; }
            while (Process32Next(snapshot, ref entry));
        }

        var windows = ReadWindowAppIds();
        var result = new Dictionary<uint, ProcessIdentity>();
        var visited = new HashSet<uint>();
        var relevantSources = new HashSet<string>(competitors ?? [], StringComparer.OrdinalIgnoreCase) { source };
        var executables = relevantSources.Select(AudioSourceMatcher.ExecutableName).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var session in audio)
            AddAncestry(session.ProcessId);
        foreach (var (id, ids) in windows)
        {
            if (ids.Overlaps(relevantSources))
                AddAncestry(id);
        }
        if (executables.Count != 0)
        {
            foreach (var process in entries.Values)
            {
                if (executables.Contains(process.Executable))
                    AddAncestry(process.ProcessId);
            }
        }

        return result;

        void AddAncestry(uint id)
        {
            while (entries.TryGetValue(id, out var process) && visited.Add(id))
            {
                IReadOnlyList<string> ids = windows.TryGetValue(id, out var foundIds) ? foundIds.ToArray() : [];
                var identity = new ProcessIdentity(id, process.ParentId, 0, "", "", "", ids, process.Executable);
                using var handle = OpenProcess(QueryLimited | Synchronize, false, id);
                if (!handle.IsInvalid && WaitForSingleObject(handle, 0) == WaitTimeout &&
                    GetProcessTimes(handle, out var created, out _, out _, out _) && created <= snapshotStarted)
                {
                    var image = ReadImagePath(handle);
                    if (string.Equals(System.IO.Path.GetFileName(image), process.Executable, StringComparison.OrdinalIgnoreCase))
                    {
                        identity = identity with
                        {
                            CreatedAt = created,
                            ImagePath = image,
                            PackageFamily = ReadAppModelString(handle, GetPackageFamilyName),
                            AppId = ReadAppModelString(handle, GetApplicationUserModelId)
                        };
                    }
                }

                result[id] = identity;
                id = process.ParentId;
            }
        }
    }

    public static SafeProcessHandle OpenVerified(ProcessIdentity identity)
    {
        var handle = OpenProcess(QueryLimited | Synchronize, false, identity.Id);
        if (IsCurrent(handle, identity.CreatedAt) &&
            string.Equals(ReadImagePath(handle), identity.ImagePath, StringComparison.OrdinalIgnoreCase))
            return handle;

        handle.Dispose();
        return new SafeProcessHandle();
    }

    public static bool IsCurrent(SafeProcessHandle handle, long created) => !handle.IsInvalid && !handle.IsClosed &&
        WaitForSingleObject(handle, 0) == WaitTimeout &&
        GetProcessTimes(handle, out var actual, out _, out _, out _) && actual == created;

    private static string ReadImagePath(SafeProcessHandle handle)
    {
        int capacity = 1024;
        var buffer = new StringBuilder(capacity);
        if (QueryFullProcessImageName(handle, 0, buffer, ref capacity))
            return buffer.ToString();

        if (Marshal.GetLastWin32Error() != InsufficientBuffer)
            return "";

        capacity = 32768;
        buffer.EnsureCapacity(capacity);
        return QueryFullProcessImageName(handle, 0, buffer, ref capacity) ? buffer.ToString() : "";
    }

    private delegate int AppModelQuery(SafeProcessHandle process, ref uint length, StringBuilder? buffer);

    private static string ReadAppModelString(SafeProcessHandle handle, AppModelQuery query)
    {
        uint length = 0;
        if (query(handle, ref length, null) != InsufficientBuffer || length is 0 or > 1024)
            return "";

        var buffer = new StringBuilder((int)length);
        return query(handle, ref length, buffer) == 0 ? buffer.ToString() : "";
    }

    private static Dictionary<uint, HashSet<string>> ReadWindowAppIds()
    {
        var identities = new Dictionary<uint, HashSet<string>>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var id);
            if (id != 0 && ReadWindowAppId(window) is { Length: > 0 } appId)
            {
                if (!identities.TryGetValue(id, out var ids))
                    identities[id] = ids = new(StringComparer.OrdinalIgnoreCase);
                ids.Add(appId);
            }
            return true;
        }, 0);
        return identities;
    }

    private static string ReadWindowAppId(IntPtr window)
    {
        IPropertyStore? store = null;
        var value = default(PropertyValue);
        try
        {
            if (SHGetPropertyStoreForWindow(window, in PropertyStoreId, out store) < 0 || store is null ||
                store.GetValue(in AppIdKey, out value) < 0 || value.Type != 31)
                return "";

            return Marshal.PtrToStringUni(value.Pointer) ?? "";
        }
        catch (COMException) { return ""; }
        finally
        {
            PropVariantClear(ref value);
            if (store is not null)
                Marshal.ReleaseComObject(store);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeap;
        public uint ModuleId;
        public uint Threads;
        public uint ParentId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PropertyKey(Guid FormatId, uint PropertyId);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyValue
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(in PropertyKey key, out PropertyValue value);
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle handle, out long created, out long exited, out long kernel, out long user);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle handle, uint flags, StringBuilder image, ref int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint length, StringBuilder? appId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint length, StringBuilder? packageFamily);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr context);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr window, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropertyValue value);
}
