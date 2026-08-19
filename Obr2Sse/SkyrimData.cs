namespace Obr2Sse;

/// Reads vanilla assets from a Skyrim install: loose files win over archives, as in the engine.
public sealed class SkyrimData : IDisposable
{
    // We only ever pull meshes from here. Voices alone are 150k entries.
    private static readonly string[] Irrelevant = { "Voices", "Sounds", "Textures", "Animations" };

    private readonly string _dataPath;
    private readonly List<BsaArchive> _archives = new();

    public IReadOnlyList<BsaArchive> Archives => _archives;

    /// The Data folder, which is where Mutagen should read this same install's plugins from.
    public string DataFolder => _dataPath;

    public SkyrimData(string gamePath)
    {
        _dataPath = Path.Combine(gamePath, "Data");
        if (!Directory.Exists(_dataPath))
            throw new DirectoryNotFoundException($"No Data folder under {gamePath}");

        foreach (var file in Directory.EnumerateFiles(_dataPath, "*.bsa").OrderBy(f => f))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (Irrelevant.Any(skip => name.Contains(skip, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                _archives.Add(new BsaArchive(file));
            }
            catch (InvalidDataException)
            {
                // The asset may still be in another archive.
            }
        }
    }

    public byte[] Read(string virtualPath)
    {
        var loose = LoosePath(virtualPath);
        if (File.Exists(loose))
            return File.ReadAllBytes(loose);

        foreach (var archive in _archives)
        {
            if (archive.Contains(virtualPath))
                return archive.Extract(virtualPath);
        }

        throw new FileNotFoundException($"Not found in loose files or archives: {virtualPath}");
    }

    public string Describe(string virtualPath)
    {
        if (File.Exists(LoosePath(virtualPath)))
            return "loose file";

        foreach (var archive in _archives)
        {
            if (archive.Contains(virtualPath))
                return Path.GetFileName(archive.ArchivePath);
        }

        return "not found";
    }

    private string LoosePath(string virtualPath)
    {
        return Path.Combine(_dataPath, virtualPath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// Every mesh under a folder, across loose files and archives.
    public IEnumerable<string> FilesUnder(string prefix, string extension)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loose = Path.Combine(_dataPath, prefix);

        if (Directory.Exists(loose))
        {
            foreach (var file in Directory.EnumerateFiles(loose, "*" + extension, SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(_dataPath, file);
                if (seen.Add(relative))
                    yield return relative;
            }
        }

        foreach (var archive in _archives)
        {
            foreach (var path in archive.FilesUnder(prefix))
            {
                if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && seen.Add(path))
                    yield return path;
            }
        }
    }

    public void Dispose()
    {
        foreach (var archive in _archives)
            archive.Dispose();
    }
}
