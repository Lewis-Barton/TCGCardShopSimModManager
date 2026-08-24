using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace TCGCardShopSimModManager.Core;

/// <summary>
/// Reads non-ZIP mod archives through SharpCompress while applying the same
/// containment, file-type and extraction-size rules as the ZIP path.
/// </summary>
public sealed class MultiFormatArchiveExtractor : IArchiveExtractor
{
    public MultiFormatArchiveExtractor(string fileExtension)
    {
        FileExtension = fileExtension;
    }

    public string FileExtension { get; }

    public ExtractionResult Extract(
        string archivePath,
        string destinationDirectory,
        ArchiveProtectionSettings settings)
    {
        var writer = new ProtectedEntryWriter(destinationDirectory, settings);
        if (FileExtension.Equals(".7z", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                if (!writer.Process(reader.Entry, reader.OpenEntryStream, archivePath))
                    break;
            }
        }
        else
        {
            using var reader = ReaderFactory.OpenReader(archivePath);
            while (reader.MoveToNextEntry())
            {
                if (!writer.Process(reader.Entry, reader.OpenEntryStream, archivePath))
                    break;
            }
        }

        return writer.Result();
    }

    private sealed class ProtectedEntryWriter
    {
        private readonly string _destinationDirectory;
        private readonly ArchiveProtectionSettings _settings;
        private readonly List<ExtractedSource> _sources = new();
        private readonly List<string> _rejected = new();
        private long _totalBytes;
        private int _entryCount;
        private bool _truncated;

        public ProtectedEntryWriter(string destinationDirectory, ArchiveProtectionSettings settings)
        {
            _destinationDirectory = destinationDirectory;
            _settings = settings;
        }

        public bool Process(IEntry entry, Func<Stream> openStream, string archivePath)
        {
            _entryCount++;
            if (_entryCount > _settings.MaxEntries)
            {
                _rejected.Add("Entry limit exceeded; extraction stopped.");
                _truncated = true;
                return false;
            }

            if (entry.IsDirectory)
                return true;

            var relativePath = (entry.Key ?? Path.GetFileNameWithoutExtension(archivePath))
                .Replace('\\', '/');
            if (!string.IsNullOrEmpty(entry.LinkTarget))
            {
                _rejected.Add($"{relativePath}: symbolic-link entry rejected");
                return true;
            }
            if (entry.IsEncrypted)
            {
                _rejected.Add($"{relativePath}: encrypted entry rejected");
                return true;
            }
            if (!IsSafeRelativePath(relativePath))
            {
                _rejected.Add($"{relativePath}: unsafe path rejected");
                return true;
            }

            var extension = Path.GetExtension(relativePath);
            if (_settings.RejectedFileExtensions.Contains(extension))
            {
                _rejected.Add($"{relativePath}: rejected file type '{extension}'");
                return true;
            }
            if (entry.Size > _settings.MaxSingleFileBytes)
            {
                _rejected.Add($"{relativePath}: single file too large ({entry.Size} bytes)");
                return true;
            }
            if (entry.Size > _settings.MaxTotalBytes - _totalBytes)
            {
                _rejected.Add("Total extracted size exceeds limit; extraction stopped.");
                _truncated = true;
                return false;
            }

            var destinationPath = Path.Combine(_destinationDirectory, relativePath);
            var destinationCreated = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                destinationCreated = true;
                using var input = openStream();
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += read;
                    if (written > _settings.MaxSingleFileBytes)
                    {
                        output.Close();
                        File.Delete(destinationPath);
                        _rejected.Add($"{relativePath}: single file too large while extracting");
                        return true;
                    }
                    if (written > _settings.MaxTotalBytes - _totalBytes)
                    {
                        output.Close();
                        File.Delete(destinationPath);
                        _rejected.Add("Total extracted size exceeds limit; extraction stopped.");
                        _truncated = true;
                        return false;
                    }
                    output.Write(buffer, 0, read);
                }

                _totalBytes += written;
                _sources.Add(new ExtractedSource(relativePath, destinationPath));
                return true;
            }
            catch (IOException) when (!destinationCreated)
            {
                _rejected.Add($"{relativePath}: duplicate or conflicting entry rejected");
                return true;
            }
            catch
            {
                if (destinationCreated && File.Exists(destinationPath))
                    File.Delete(destinationPath);
                throw;
            }
        }

        public ExtractionResult Result() => new(_sources, _rejected, _truncated);

        private static bool IsSafeRelativePath(string relativePath)
        {
            if (Path.IsPathRooted(relativePath))
                return false;

            foreach (var segment in relativePath.Split('/'))
            {
                if (segment is "." or ".." or "" || !IsSafeWindowsSegment(segment))
                    return false;
            }

            return true;
        }

        private static bool IsSafeWindowsSegment(string segment)
        {
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
                return false;
            if (segment.Any(character => character < 32 || "<>:\"|?*".Contains(character)))
                return false;

            var stem = segment.Split('.')[0];
            return !stem.Equals("CON", StringComparison.OrdinalIgnoreCase) &&
                   !stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) &&
                   !stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) &&
                   !stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) &&
                   !Enumerable.Range(1, 9).Any(number =>
                       stem.Equals($"COM{number}", StringComparison.OrdinalIgnoreCase) ||
                       stem.Equals($"LPT{number}", StringComparison.OrdinalIgnoreCase));
        }
    }
}
