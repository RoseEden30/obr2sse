using System.IO.Compression;
using System.Text;
using K4os.Compression.LZ4.Streams;

namespace Obr2Sse;

/// <summary>
/// Reader for Bethesda BSA archives, version 104 (Skyrim LE / Fallout 3 / NV) and 105 (Skyrim SE).
/// Only supports what we need: list the contents and extract a file by its virtual path.
///
/// Paths are resolved through the archive's own name tables rather than Bethesda's hash function.
/// Every vanilla archive stores both tables, and iterating them is exact where a reimplemented
/// hash would be one subtle bug away from silently missing files.
/// </summary>
public sealed class BsaArchive : IDisposable
{
    private const uint FlagIncludeDirectoryNames = 0x001;
    private const uint FlagIncludeFileNames = 0x002;
    private const uint FlagCompressedByDefault = 0x004;
    private const uint FlagEmbedFileNames = 0x100;

    // A file's size field carries a bit that inverts the archive-wide compression setting.
    private const uint FileCompressionToggle = 0x40000000;
    private const uint FileSizeMask = 0x3FFFFFFF;

    private readonly record struct Entry(long Offset, uint StoredSize, bool Compressed);

    private readonly FileStream _stream;
    private readonly BinaryReader _reader;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly uint _version;
    private readonly bool _embedFileNames;

    public string ArchivePath { get; }
    public uint Version => _version;
    public uint ArchiveFlags { get; }
    public IReadOnlyCollection<string> Files => _entries.Keys;

    public BsaArchive(string path)
    {
        ArchivePath = path;
        _stream = File.OpenRead(path);
        _reader = new BinaryReader(_stream, Encoding.ASCII);

        var magic = _reader.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != 'B' || magic[1] != 'S' || magic[2] != 'A' || magic[3] != 0)
            throw new InvalidDataException($"Not a BSA archive: {path}");

        _version = _reader.ReadUInt32();
        if (_version != 104 && _version != 105)
            throw new InvalidDataException($"Unsupported BSA version {_version}: {path}");

        uint folderRecordOffset = _reader.ReadUInt32();
        ArchiveFlags = _reader.ReadUInt32();
        uint folderCount = _reader.ReadUInt32();
        uint fileCount = _reader.ReadUInt32();
        _reader.ReadUInt32(); // total folder name length
        uint totalFileNameLength = _reader.ReadUInt32();
        _reader.ReadUInt32(); // content type flags, unused here

        if ((ArchiveFlags & FlagIncludeDirectoryNames) == 0 || (ArchiveFlags & FlagIncludeFileNames) == 0)
            throw new InvalidDataException($"Archive omits its name tables, cannot resolve paths: {path}");

        _embedFileNames = (ArchiveFlags & FlagEmbedFileNames) != 0;
        bool compressedByDefault = (ArchiveFlags & FlagCompressedByDefault) != 0;

        // Folder records. The layout grew in 105: an extra padding field and a 64-bit offset.
        _stream.Position = folderRecordOffset;
        var folderFileCounts = new uint[folderCount];
        for (int i = 0; i < folderCount; i++)
        {
            _reader.ReadUInt64(); // name hash
            folderFileCounts[i] = _reader.ReadUInt32();
            if (_version == 105)
            {
                _reader.ReadUInt32(); // padding
                _reader.ReadUInt64(); // file record block offset
            }
            else
            {
                _reader.ReadUInt32(); // file record block offset
            }
        }

        // Folder name + file record blocks, contiguous and in folder-record order.
        var folderNames = new string[folderCount];
        var rawFiles = new List<(int Folder, uint Size, uint Offset)>((int)fileCount);

        for (int i = 0; i < folderCount; i++)
        {
            byte nameLength = _reader.ReadByte();
            folderNames[i] = ReadFixedString(nameLength).TrimEnd('\0');

            for (uint j = 0; j < folderFileCounts[i]; j++)
            {
                _reader.ReadUInt64(); // name hash
                uint size = _reader.ReadUInt32();
                uint offset = _reader.ReadUInt32();
                rawFiles.Add((i, size, offset));
            }
        }

        // File name block: every file name, null terminated, in the same order as the records above.
        var nameBlock = _reader.ReadBytes((int)totalFileNameLength);
        var fileNames = new List<string>((int)fileCount);
        int start = 0;
        for (int i = 0; i < nameBlock.Length; i++)
        {
            if (nameBlock[i] != 0) continue;
            fileNames.Add(Encoding.ASCII.GetString(nameBlock, start, i - start));
            start = i + 1;
        }

        if (fileNames.Count != rawFiles.Count)
            throw new InvalidDataException(
                $"Name table mismatch in {path}: {rawFiles.Count} records, {fileNames.Count} names");

        for (int i = 0; i < rawFiles.Count; i++)
        {
            var (folder, size, offset) = rawFiles[i];
            bool compressed = compressedByDefault ^ ((size & FileCompressionToggle) != 0);
            string key = folderNames[folder].Length == 0
                ? fileNames[i]
                : folderNames[folder] + "\\" + fileNames[i];

            _entries[Normalize(key)] = new Entry(offset, size & FileSizeMask, compressed);
        }
    }

    public bool Contains(string virtualPath) => _entries.ContainsKey(Normalize(virtualPath));

    public byte[] Extract(string virtualPath)
    {
        if (!_entries.TryGetValue(Normalize(virtualPath), out var entry))
            throw new FileNotFoundException($"Not present in {ArchivePath}: {virtualPath}");

        _stream.Position = entry.Offset;
        uint remaining = entry.StoredSize;

        if (_embedFileNames)
        {
            byte length = _reader.ReadByte();
            _stream.Position += length;
            remaining -= (uint)(length + 1);
        }

        if (!entry.Compressed)
            return _reader.ReadBytes((int)remaining);

        uint originalSize = _reader.ReadUInt32();
        remaining -= 4;

        using var source = new MemoryStream(_reader.ReadBytes((int)remaining));
        using Stream decoder = _version == 105
            ? LZ4Stream.Decode(source)
            : new ZLibStream(source, CompressionMode.Decompress);

        var result = new byte[originalSize];
        decoder.ReadExactly(result);
        return result;
    }

    private string ReadFixedString(int length)
    {
        return Encoding.ASCII.GetString(_reader.ReadBytes(length));
    }

    private static string Normalize(string path)
    {
        return path.Replace('/', '\\').Trim('\\');
    }

    public IEnumerable<string> FilesUnder(string prefix)
    {
        var normalized = Normalize(prefix);

        foreach (var key in _entries.Keys)
        {
            if (key.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                yield return key;
        }
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}
