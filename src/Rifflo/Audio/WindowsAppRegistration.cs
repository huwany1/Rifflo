using System.IO;
using System.Runtime.InteropServices;

namespace Rifflo.Audio;

internal sealed class WindowsAppRegistration
{
    private const int RefreshMilliseconds = 5000;
    private static readonly Guid AppsFolderId = new("1E87508D-89C2-42F0-8A7E-645A0F50CA58");
    private static readonly Guid EnumItemsHandler = new("94F60519-2850-4924-AA5A-D15E84868039");
    private static readonly Guid EnumItemsId = typeof(IEnumShellItems).GUID;
    private static readonly Guid ShellItemId = typeof(IShellItem).GUID;
    private static readonly Guid PropertyStoreId = typeof(IPropertyStore).GUID;
    private static readonly PropertyKey AppIdKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    private static readonly PropertyKey TargetPathKey = new(new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);
    private readonly Dictionary<string, Registration> cache = new(StringComparer.OrdinalIgnoreCase);

    // Owned by the audio worker. Keep both positive and negative results briefly, so an
    // installation, moved shortcut, or upgrade can be discovered during a later match.
    public IReadOnlyList<string> GetExecutableTargets(string appId)
    {
        var now = Environment.TickCount64;
        if (cache.TryGetValue(appId, out var cached) && now < cached.ExpiresAt && cached.Paths.All(File.Exists))
            return cached.Paths;

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadAppsFolder(appId, targets);
        if (targets.Count == 0)
        {
            ReadShortcuts(Environment.SpecialFolder.Programs, appId, targets);
            ReadShortcuts(Environment.SpecialFolder.CommonPrograms, appId, targets);
        }

        if (cache.Count >= 32 && cached is null)
            cache.Clear();
        var paths = targets.ToArray();
        cache[appId] = new(paths, Environment.TickCount64 + RefreshMilliseconds);
        return paths;
    }

    private static void ReadAppsFolder(string appId, HashSet<string> targets)
    {
        IShellItem? folder = null;
        IEnumShellItems? entries = null;
        try
        {
            if (SHGetKnownFolderItem(in AppsFolderId, 0, 0, in ShellItemId, out folder) < 0 || folder is null ||
                folder.BindToHandler(0, in EnumItemsHandler, in EnumItemsId, out entries) < 0 || entries is null)
                return;

            while (entries.Next(1, out var item, out var fetched) >= 0 && fetched == 1 && item is not null)
            {
                IPropertyStore? store = null;
                try
                {
                    if (item is IShellItem2 properties &&
                        properties.GetPropertyStore(0, in PropertyStoreId, out store) >= 0 && store is not null)
                        AddTarget(store, appId, targets);
                }
                catch (COMException) { }
                finally
                {
                    Release(store);
                    Release(item);
                }
            }
        }
        catch (COMException) { }
        finally
        {
            Release(entries);
            Release(folder);
        }
    }

    private static void ReadShortcuts(Environment.SpecialFolder folder, string appId, HashSet<string> targets)
    {
        var directory = Environment.GetFolderPath(folder);
        if (directory.Length == 0)
            return;

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var shortcut in Directory.EnumerateFiles(directory, "*.lnk", options))
            {
                IPropertyStore? store = null;
                try
                {
                    if (SHGetPropertyStoreFromParsingName(shortcut, 0, 0, in PropertyStoreId, out store) >= 0 && store is not null)
                        AddTarget(store, appId, targets);
                }
                catch (COMException) { }
                finally { Release(store); }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void AddTarget(IPropertyStore store, string appId, HashSet<string> targets)
    {
        if (!string.Equals(ReadString(store, in AppIdKey), appId, StringComparison.OrdinalIgnoreCase))
            return;

        var path = ReadString(store, in TargetPathKey);
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            return;

        if (File.Exists(path))
            targets.Add(Path.GetFullPath(path));
    }

    private static string ReadString(IPropertyStore store, in PropertyKey key)
    {
        var value = default(PropertyValue);
        try
        {
            return store.GetValue(in key, out value) >= 0 && value.Type == 31
                ? Marshal.PtrToStringUni(value.Pointer) ?? ""
                : "";
        }
        finally { PropVariantClear(ref value); }
    }

    private static void Release(object? value)
    {
        if (value is not null)
            Marshal.ReleaseComObject(value);
    }

    private sealed record Registration(string[] Paths, long ExpiresAt);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid format, uint id)
    {
        public readonly Guid Format = format;
        public readonly uint Id = id;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyValue
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint context, in Guid handler, in Guid iid, out IEnumShellItems? result);
    }

    [ComImport, Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        // IShellItem2 extends the five IShellItem slots before its first method.
        [PreserveSig] int BindToHandler(nint context, in Guid handler, in Guid iid, out nint result);
        [PreserveSig] int GetParent(out nint result);
        [PreserveSig] int GetDisplayName(uint type, out nint result);
        [PreserveSig] int GetAttributes(uint mask, out uint result);
        [PreserveSig] int Compare(nint other, uint hint, out int result);
        [PreserveSig] int GetPropertyStore(uint flags, in Guid iid, out IPropertyStore? result);
    }

    [ComImport, Guid("70629033-E363-4A28-A567-0DB78006E6D7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, out IShellItem? item, out uint fetched);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(in PropertyKey key, out PropertyValue value);
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderItem(in Guid folder, uint flags, nint token,
        in Guid iid, out IShellItem? result);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, nint context, uint flags,
        in Guid iid, out IPropertyStore? result);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropertyValue value);
}
