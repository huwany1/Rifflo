using System.Runtime.InteropServices;

internal static class WindowIdentity
{
    private static readonly PropertyKey AppId = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    public static void Set(nint window, string aumid)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, in iid, out var store));
        var value = new PropertyValue { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(aumid) };
        try
        {
            Marshal.ThrowExceptionForHR(store.SetValue(in AppId, in value));
            Marshal.ThrowExceptionForHR(store.Commit());
        }
        finally
        {
            Marshal.FreeCoTaskMem(value.Pointer);
            Marshal.ReleaseComObject(store);
        }
    }

    public static string? Get(nint window)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, in iid, out var store));
        try
        {
            Marshal.ThrowExceptionForHR(store.GetValue(in AppId, out var value));
            try
            {
                return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(nint window, in Guid iid, out IPropertyStore store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropertyValue value);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct PropertyKey(Guid Format, uint Id);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyValue
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(in PropertyKey key, out PropertyValue value);
        [PreserveSig] int SetValue(in PropertyKey key, in PropertyValue value);
        [PreserveSig] int Commit();
    }
}
