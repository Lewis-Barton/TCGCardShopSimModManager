using System.Diagnostics;
using System.Text.Json;

namespace TCGCardShopSimModManager.Core;

internal sealed class AtomicJsonFile<T>
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private readonly string _path;
    private readonly JsonSerializerOptions _options;
    private readonly Func<T> _empty;
    private readonly bool _recoverCorrupt;

    public AtomicJsonFile(string path, JsonSerializerOptions options, Func<T> empty, bool recoverCorrupt)
    {
        _path = path;
        _options = options;
        _empty = empty;
        _recoverCorrupt = recoverCorrupt;
    }

    public T Read() => WithLock(ReadUnlocked);

    public void Write(T value) => WithLock(() => WriteUnlocked(value));

    public TResult Update<TResult>(Func<T, (T Value, TResult Result)> change)
    {
        return WithLock(() =>
        {
            var (value, result) = change(ReadUnlocked());
            WriteUnlocked(value);
            return result;
        });
    }

    public TResult UpdateIfChanged<TResult>(Func<T, (T Value, TResult Result, bool Changed)> change)
    {
        return WithLock(() =>
        {
            var (value, result, changed) = change(ReadUnlocked());
            if (changed)
                WriteUnlocked(value);
            return result;
        });
    }

    private T ReadUnlocked()
    {
        if (!File.Exists(_path))
            return _empty();

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(_path), _options) ?? _empty();
        }
        catch (JsonException) when (_recoverCorrupt)
        {
            BackUpCorrupt();
            return _empty();
        }
    }

    private void WriteUnlocked(T value)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, _options);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_path))
                File.Replace(temporaryPath, _path, _path + ".bak");
            else
                File.Move(temporaryPath, _path);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { /* best effort */ }
        }
    }

    private void BackUpCorrupt()
    {
        try
        {
            var corruptPath = _path + ".corrupt";
            File.Move(_path, corruptPath, overwrite: true);
        }
        catch
        {
            // Returning an empty journal is more useful than failing recovery.
        }
    }

    private TResult WithLock<TResult>(Func<TResult> action)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var lockPath = _path + ".lock";
        var stopwatch = Stopwatch.StartNew();

        using var fileLock = AcquireLock(lockPath, stopwatch);
        return action();
    }

    private static FileStream AcquireLock(string lockPath, Stopwatch stopwatch)
    {
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (stopwatch.Elapsed < LockTimeout)
            {
                Thread.Sleep(25);
            }
        }
    }

    private void WithLock(Action action) => WithLock(() =>
    {
        action();
        return true;
    });
}
