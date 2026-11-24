using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

public static class RiffloDesktopDuplication
{
    private const int NotFound = unchecked((int)0x887A0002);
    private const int WaitTimeout = unchecked((int)0x887A0027);
    private static readonly Guid FactoryId = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid Output1Id = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid TextureId = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    public sealed class Sample
    {
        public long Timestamp { get; set; }
        public uint RgbHash { get; set; }
        public uint AccumulatedFrames { get; set; }
    }

    public sealed class Result
    {
        public long Start { get; set; }
        public long End { get; set; }
        public uint TimeoutCount { get; set; }
        public string GpuAdapterLuid { get; set; }
        public Sample[] Samples { get; set; }
    }

    public static Result Read(string display, int x, int y, int width, int height, double seconds)
    {
        var factoryId = FactoryId;
        Check(CreateDXGIFactory1(ref factoryId, out var factoryPointer), "CreateDXGIFactory1");
        using var factory = new ComReference(factoryPointer);
        var enumerateAdapters = Method<Enumerate>(factory, 12); // IDXGIFactory1::EnumAdapters1
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            var adapterStatus = enumerateAdapters(factory, adapterIndex, out var adapterPointer);
            if (adapterStatus == NotFound)
                break;
            Check(adapterStatus, "EnumAdapters1");
            using var adapter = new ComReference(adapterPointer);
            var enumerateOutputs = Method<Enumerate>(adapter, 7); // IDXGIAdapter::EnumOutputs
            for (uint outputIndex = 0; ; outputIndex++)
            {
                var outputStatus = enumerateOutputs(adapter, outputIndex, out var outputPointer);
                if (outputStatus == NotFound)
                    break;
                Check(outputStatus, "EnumOutputs");
                using var output = new ComReference(outputPointer);
                Check(Method<GetOutputDescription>(output, 7)(output, out var description), "IDXGIOutput::GetDesc");
                if (!description.Attached || !string.Equals(description.Device, display, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (description.Rotation != 1 || x < description.Left || y < description.Top ||
                    x + width > description.Right || y + height > description.Bottom)
                    throw new InvalidOperationException("Desktop duplication requires the crop inside an unrotated output.");
                Check(Method<GetAdapterDescription>(adapter, 10)(adapter, out var adapterDescription), "IDXGIAdapter1::GetDesc1");
                var luid = $"0x{adapterDescription.HighLuid:x8}_0x{adapterDescription.LowLuid:x8}";
                return ReadOutput(adapter, output, x - description.Left, y - description.Top, width, height, seconds, luid);
            }
        }
        throw new InvalidOperationException("Display for the overlay was not enumerated by DXGI.");
    }

    private static Result ReadOutput(ComReference adapter, ComReference output, int x, int y, int width, int height, double seconds, string luid)
    {
        Check(D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7,
            out var devicePointer, out _, out var contextPointer), "D3D11CreateDevice");
        using var device = new ComReference(devicePointer);
        using var context = new ComReference(contextPointer);
        using var output1 = output.Query(Output1Id);
        Check(Method<Duplicate>(output1, 22)(output1, device, out var duplicatePointer), "IDXGIOutput1::DuplicateOutput");
        using var duplication = new ComReference(duplicatePointer);
        var texture = new TextureDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
            Format = 87, SampleCount = 1, Usage = 3, CpuAccessFlags = 0x20000,
        };
        Check(Method<CreateTexture>(device, 5)(device, ref texture, IntPtr.Zero, out var targetPointer), "CreateTexture2D");
        using var target = new ComReference(targetPointer);
        var copy = Method<CopyRegion>(context, 46);
        var map = Method<MapTexture>(context, 14);
        var unmap = Method<UnmapTexture>(context, 15);
        var acquire = Method<AcquireFrame>(duplication, 8);
        var release = Method<ReleaseFrame>(duplication, 14);
        var region = new Region { Left = (uint)x, Top = (uint)y, Right = (uint)(x + width), Bottom = (uint)(y + height), Back = 1 };
        var samples = new List<Sample>();
        var pixels = new int[width];
        var start = Stopwatch.GetTimestamp();
        uint timeouts = 0;
        while ((Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency < seconds)
        {
            var status = acquire(duplication, 100, out var frame, out var resourcePointer);
            if (status == WaitTimeout)
            {
                timeouts++;
                continue;
            }
            Check(status, "AcquireNextFrame");
            try
            {
                using var resource = new ComReference(resourcePointer);
                if (frame.LastPresent == 0)
                    continue;
                using var source = resource.Query(TextureId);
                copy(context, target, 0, 0, 0, 0, source, 0, ref region);
                Check(map(context, target, 0, 1, 0, out var mapped), "Map desktop region");
                try
                {
                    var hash = 2166136261u;
                    for (var row = 0; row < height; row++)
                    {
                        Marshal.Copy(mapped.Data + row * (int)mapped.RowPitch, pixels, 0, width);
                        foreach (var pixel in pixels)
                            hash = unchecked((hash ^ ((uint)pixel & 0x00FFFFFFu)) * 16777619u);
                    }
                    samples.Add(new Sample { Timestamp = frame.LastPresent, RgbHash = hash, AccumulatedFrames = frame.AccumulatedFrames });
                }
                finally { unmap(context, target, 0); }
            }
            finally { Check(release(duplication), "ReleaseFrame"); }
        }
        return new Result { Start = start, End = Stopwatch.GetTimestamp(), TimeoutCount = timeouts, GpuAdapterLuid = luid, Samples = samples.ToArray() };
    }

    private sealed class ComReference : IDisposable
    {
        private readonly IntPtr _pointer;
        public ComReference(IntPtr pointer) => _pointer = pointer;
        public static implicit operator IntPtr(ComReference instance) => instance._pointer;
        public void Dispose() => Marshal.Release(_pointer);
        public ComReference Query(Guid id)
        {
            Check(Marshal.QueryInterface(_pointer, in id, out var pointer), "QueryInterface");
            return new ComReference(pointer);
        }
    }

    // The offsets are the interface methods' positions in the Windows SDK COM vtables.
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    private static void Check(int result, string function)
    {
        if (result < 0)
            throw new COMException($"{function} failed (0x{result:x8}).", result);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OutputDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        public int Left, Top, Right, Bottom;
        [MarshalAs(UnmanagedType.Bool)] public bool Attached;
        public uint Rotation;
        public IntPtr Handle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubsystemId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LowLuid;
        public int HighLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)] private struct TextureDescription { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct Region { public uint Left, Top, Front, Right, Bottom, Back; }
    [StructLayout(LayoutKind.Sequential)] private struct MappedTexture { public IntPtr Data; public uint RowPitch, DepthPitch; }
    [StructLayout(LayoutKind.Sequential)] private struct FrameInfo
    {
        public long LastPresent, LastMouseUpdate;
        public uint AccumulatedFrames;
        public int RectsCoalesced, ProtectedContentMasked, PointerX, PointerY, PointerVisible;
        public uint TotalMetadataBufferSize, PointerShapeBufferSize;
    }

    private delegate int Enumerate(IntPtr instance, uint index, out IntPtr result);
    private delegate int GetOutputDescription(IntPtr instance, out OutputDescription description);
    private delegate int GetAdapterDescription(IntPtr instance, out AdapterDescription description);
    private delegate int Duplicate(IntPtr output, IntPtr device, out IntPtr duplicatedOutput);
    private delegate int CreateTexture(IntPtr device, ref TextureDescription description, IntPtr initial, out IntPtr texture);
    private delegate int AcquireFrame(IntPtr duplication, uint timeoutMs, out FrameInfo frame, out IntPtr resource);
    private delegate int ReleaseFrame(IntPtr duplication);
    private delegate void CopyRegion(IntPtr context, IntPtr target, uint targetSubresource, uint x, uint y, uint z,
        IntPtr source, uint sourceSubresource, ref Region region);
    private delegate int MapTexture(IntPtr context, IntPtr texture, uint subresource, uint mode, uint flags, out MappedTexture mapped);
    private delegate void UnmapTexture(IntPtr context, IntPtr texture, uint subresource);

    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid id, out IntPtr factory);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software,
        uint flags, IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out uint featureLevel, out IntPtr context);
}
