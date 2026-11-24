using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Rifflo.Audio;

internal sealed class KuGouExecutableTrust
{
    private const uint ProcessQueryAndSynchronize = 0x00100400;
    private const uint ProcessImageFileMapping = 44;
    private const uint GenericReadAndExecute = 0xA0000000;
    private const uint ShareRead = 1;
    private const uint OpenExisting = 3;
    private const uint WinTrustUiNone = 2;
    private const uint WinTrustChoiceFile = 1;
    private const uint WinTrustVerify = 1;
    private const uint WinTrustClose = 2;
    private const uint WinTrustCacheOnly = 0x1000;
    private const uint CertNameAttributeType = 3;
    private const string Organization = "Guangzhou KuGou Computer Technology Co., Ltd.";
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    private readonly Dictionary<string, CachedTrust> cache = new(StringComparer.OrdinalIgnoreCase);

    // Owned by the audio worker. Recheck the actual mapped file even when signature and
    // version information for that file identity are already cached.
    public bool IsTrusted(ProcessIdentity process)
    {
        if (process.CreatedAt == 0 || !Path.IsPathFullyQualified(process.ImagePath))
            return false;

        using var handle = OpenProcess(ProcessQueryAndSynchronize, false, process.Id);
        if (!ProcessSnapshot.IsCurrent(handle, process.CreatedAt))
            return false;

        // Deny writes and deletion while WinTrust and the resource reader share this handle.
        using var file = CreateFile(process.ImagePath, GenericReadAndExecute, ShareRead, 0, OpenExisting, 0, 0);
        if (file.IsInvalid || !IsMappedImage(handle, file))
            return false;

        try
        {
            return CheckFile(process.ImagePath, file) && ProcessSnapshot.IsCurrent(handle, process.CreatedAt);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (BadImageFormatException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static bool IsMappedImage(SafeProcessHandle process, SafeFileHandle file)
    {
        // QueryTime process paths can be stale after a rename. This native query compares the
        // opened file to the process's actual image mapping. Unsupported calls fail closed.
        var fileHandle = file.DangerousGetHandle();
        try { return NtQueryInformationProcess(process, ProcessImageFileMapping, in fileHandle, (uint)IntPtr.Size, 0) == 0; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private bool CheckFile(string path, SafeFileHandle file)
    {
        var now = Environment.TickCount64;
        bool cacheable = TryGetFileStamp(file, out var stamp);
        if (cacheable && cache.TryGetValue(path, out var existing) && stamp == existing.Stamp && now < existing.ExpiresAt)
            return existing.Trusted;

        bool trusted = HasTrustedPublisher(path, file) && SignedPeProduct.HasKuGouProduct(file);
        if (cacheable)
        {
            if (cache.Count >= 64 && !cache.ContainsKey(path))
                cache.Clear();
            cache[path] = new(stamp, trusted, Environment.TickCount64 + (trusted ? 300_000 : 20_000));
        }
        return trusted;
    }

    private static bool TryGetFileStamp(SafeFileHandle file, out FileStamp stamp)
    {
        stamp = default;
        if (!GetFileId(file, 18, out var id, (uint)Marshal.SizeOf<FileId>()) ||
            !GetFileTimes(file, 0, out var times, (uint)Marshal.SizeOf<FileTimes>()))
            return false;

        stamp = new(id.Volume, id.Low, id.High, times.Created, times.Written, times.Changed, RandomAccess.GetLength(file));
        return true;
    }

    private static bool HasTrustedPublisher(string path, SafeFileHandle file)
    {
        nint pathPointer = Marshal.StringToCoTaskMemUni(path);
        nint infoPointer = 0;
        var trust = new WinTrustData
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>(),
            UiChoice = WinTrustUiNone,
            Choice = WinTrustChoiceFile,
            StateAction = WinTrustVerify,
            ProviderFlags = WinTrustCacheOnly
        };
        try
        {
            var info = new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer,
                File = file.DangerousGetHandle()
            };
            infoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(info, infoPointer, false);
            trust.ChoiceInfo = infoPointer;
            if (WinVerifyTrust(-1, in GenericVerifyV2, ref trust) != 0)
                return false;

            var data = WTHelperProvDataFromStateData(trust.StateData);
            var signer = data == 0 ? 0 : WTHelperGetProvSignerFromChain(data, 0, false, 0);
            var providerCert = signer == 0 ? 0 : WTHelperGetProvCertFromChain(signer, 0);
            if (providerCert == 0)
                return false;

            var certificate = Marshal.PtrToStructure<ProviderCertificate>(providerCert).Certificate;
            return certificate != 0 &&
                string.Equals(ReadSubjectAttribute(certificate, "2.5.4.10"), Organization, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ReadSubjectAttribute(certificate, "2.5.4.6"), "CN", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                if (trust.StateData != 0)
                {
                    trust.StateAction = WinTrustClose;
                    WinVerifyTrust(-1, in GenericVerifyV2, ref trust);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(infoPointer);
                Marshal.FreeCoTaskMem(pathPointer);
                GC.KeepAlive(file);
            }
        }
    }

    private static string ReadSubjectAttribute(nint certificate, string oid)
    {
        var value = new StringBuilder(256);
        uint length = CertGetNameString(certificate, CertNameAttributeType, 0, oid, value, (uint)value.Capacity);
        return length > 1 && length < value.Capacity ? value.ToString() : "";
    }

    private readonly record struct FileStamp(ulong Volume, ulong IdLow, ulong IdHigh, long Created, long Written,
        long Changed, long Length);
    private sealed record CachedTrust(FileStamp Stamp, bool Trusted, long ExpiresAt);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId
    {
        public ulong Volume;
        public ulong Low;
        public ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTimes
    {
        public long Created;
        public long Accessed;
        public long Written;
        public long Changed;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public nint FilePath;
        public nint File;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint Choice;
        public nint ChoiceInfo;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificate
    {
        public uint Size;
        public nint Certificate;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint securityAttributes,
        uint creationDisposition, uint flags, nint template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileId(SafeFileHandle file, int informationClass, out FileId id, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileTimes(SafeFileHandle file, int informationClass, out FileTimes times, uint size);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, uint informationClass,
        in nint file, uint size, nint returned);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(nint window, in Guid action, ref WinTrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperProvDataFromStateData(nint state);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvSignerFromChain(nint data, uint signer,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvCertFromChain(nint signer, uint certificate);

    [DllImport("crypt32.dll", EntryPoint = "CertGetNameStringW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CertGetNameString(nint certificate, uint type, uint flags,
        [MarshalAs(UnmanagedType.LPStr)] string typeParameter, StringBuilder value, uint length);
}
