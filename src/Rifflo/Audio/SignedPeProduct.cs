using System.Buffers.Binary;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Rifflo.Audio;

internal static class SignedPeProduct
{
    private const uint VersionResource = 16;
    private const uint SubdirectoryFlag = 0x80000000;
    private const int DirectoryHeaderSize = 16;
    private const int DirectoryEntrySize = 8;
    private const int VersionHeaderSize = 6;

    public static bool HasKuGouProduct(SafeFileHandle file)
    {
        // Read the PID-bound, verified handle directly. FileVersionInfo may merge external
        // MUI resources and cannot establish that ProductName came from the signed image.
        using var stream = new FileStream(new SafeFileHandle(file.DangerousGetHandle(), ownsHandle: false), FileAccess.Read);
        using var image = new PEReader(stream);
        var header = image.PEHeaders.PEHeader;
        if (header is null)
            return false;

        var resources = header.ResourceTableDirectory;
        var certTable = header.CertificateTableDirectory;
        if (resources.Size is < DirectoryHeaderSize or > 32 * 1024 * 1024 ||
            certTable.Size <= 0 || (long)certTable.RelativeVirtualAddress + certTable.Size > stream.Length ||
            !InSignedSection(image.PEHeaders, resources.RelativeVirtualAddress, resources.Size))
            return false;

        var directory = image.GetSectionData(resources.RelativeVirtualAddress).GetContent(0, resources.Size).AsSpan();
        if (!TryReadDirectory(directory, 0, out var types))
            return false;

        bool found = false;
        for (int offset = 0; offset < types.Length; offset += DirectoryEntrySize)
        {
            if (ReadUInt(types, offset) != VersionResource)
                continue;

            uint namesOffset = ReadUInt(types, offset + 4);
            if (!TryReadSubdirectory(directory, namesOffset, out var names))
                return false;

            for (int name = 0; name < names.Length; name += DirectoryEntrySize)
            {
                uint languagesOffset = ReadUInt(names, name + 4);
                if (!TryReadSubdirectory(directory, languagesOffset, out var languages))
                    return false;

                for (int language = 0; language < languages.Length; language += DirectoryEntrySize)
                {
                    uint dataOffset = ReadUInt(languages, language + 4);
                    if (dataOffset > directory.Length - 16)
                        return false;

                    int rva = (int)ReadUInt(directory, (int)dataOffset);
                    int size = (int)ReadUInt(directory, (int)dataOffset + 4);
                    if (size is < VersionHeaderSize or > ushort.MaxValue || !InSignedSection(image.PEHeaders, rva, size))
                        return false;

                    var version = image.GetSectionData(rva).GetContent(0, size).AsSpan();
                    if (!IsKuGouVersion(version))
                        return false;
                    found = true;
                }
            }
        }

        GC.KeepAlive(file);
        return found;
    }

    private static bool InSignedSection(PEHeaders headers, int rva, int size)
    {
        int sectionIndex = headers.GetContainingSectionIndex(rva);
        if (sectionIndex < 0 || headers.PEHeader is not { } header)
            return false;

        var section = headers.SectionHeaders[sectionIndex];
        long offset = (long)rva - section.VirtualAddress;
        long end = (long)section.PointerToRawData + offset + size;
        // Authenticode hashes section bytes and excludes the certificate table, file checksum
        // and certificate directory fields. Require the complete value in a section before it.
        return offset >= 0 && offset + size <= Math.Min(section.SizeOfRawData, section.VirtualSize) &&
            section.PointerToRawData >= header.SizeOfHeaders && end <= header.CertificateTableDirectory.RelativeVirtualAddress;
    }

    private static bool TryReadSubdirectory(ReadOnlySpan<byte> data, uint entry, out ReadOnlySpan<byte> entries)
    {
        entries = default;
        return (entry & SubdirectoryFlag) != 0 && TryReadDirectory(data, entry & ~SubdirectoryFlag, out entries);
    }

    private static bool TryReadDirectory(ReadOnlySpan<byte> data, uint offset, out ReadOnlySpan<byte> entries)
    {
        entries = default;
        if (offset > data.Length - DirectoryHeaderSize)
            return false;

        int start = (int)offset + DirectoryHeaderSize;
        int length = (ReadWord(data, start - 4) + ReadWord(data, start - 2)) * DirectoryEntrySize;
        if (length > data.Length - start)
            return false;

        entries = data.Slice(start, length);
        return true;
    }

    private static bool IsKuGouVersion(ReadOnlySpan<byte> data)
    {
        if (!TryReadBlock(data, 0, data.Length, out var version) || version.Key != "VS_VERSION_INFO")
            return false;

        bool found = false;
        for (int childOffset = version.Children; childOffset < version.End;)
        {
            if (!TryReadBlock(data, childOffset, version.End, out var child))
                return false;
            childOffset = Align(child.End);
            if (child.Key != "StringFileInfo")
                continue;

            for (int tableOffset = child.Children; tableOffset < child.End;)
            {
                if (!TryReadBlock(data, tableOffset, child.End, out var table))
                    return false;
                tableOffset = Align(table.End);

                for (int attributeOffset = table.Children; attributeOffset < table.End;)
                {
                    if (!TryReadBlock(data, attributeOffset, table.End, out var attribute))
                        return false;
                    attributeOffset = Align(attribute.End);
                    if (attribute.Key != "ProductName")
                        continue;

                    var name = Encoding.Unicode.GetString(data.Slice(attribute.Value, attribute.ValueLength)).TrimEnd('\0');
                    if (!attribute.Text || !string.Equals(name, "KuGou", StringComparison.OrdinalIgnoreCase))
                        return false;
                    found = true;
                }
            }
        }

        return found;
    }

    private static bool TryReadBlock(ReadOnlySpan<byte> data, int offset, int limit, out VersionBlock block)
    {
        block = default;
        if (limit - offset < VersionHeaderSize)
            return false;

        int length = ReadWord(data, offset);
        if (length < VersionHeaderSize || length > limit - offset)
            return false;

        int end = offset + length;
        int key = offset + VersionHeaderSize;
        int keyEnd = key;
        while (keyEnd + 1 < end && ReadWord(data, keyEnd) != 0)
            keyEnd += 2;
        if (keyEnd + 1 >= end)
            return false;

        int value = Align(keyEnd + 2);
        bool text = ReadWord(data, offset + 4) == 1;
        int valueLength = ReadWord(data, offset + 2) * (text ? 2 : 1);
        if (valueLength > 0 && valueLength > end - value)
            return false;

        block = new(Encoding.Unicode.GetString(data[key..keyEnd]), text, value, valueLength, Align(value + valueLength), end);
        return true;
    }

    private static int Align(int position) => (position + 3) & ~3;
    private static uint ReadUInt(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    private static ushort ReadWord(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);

    private readonly record struct VersionBlock(string Key, bool Text, int Value, int ValueLength, int Children, int End);
}
