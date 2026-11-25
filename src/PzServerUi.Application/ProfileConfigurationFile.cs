using System.Text;

namespace PzServerUi.Application;

public sealed class ProfileConfigurationConflictException(string message) : IOException(message);

// A form owns one snapshot. Comparing the entire file protects even settings the form does not edit.
internal sealed class ProfileConfigurationFile
{
    private const int MaxBytes = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly object SaveLock = new();
    private readonly string _path;
    private byte[] _original;

    private ProfileConfigurationFile(string path, byte[] bytes)
    {
        _path = path;
        _original = bytes;
        try { Text = Utf8.GetString(bytes); }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("Файл настроек не в UTF-8. Панель не будет его перезаписывать.", error);
        }
    }

    public string Text { get; private set; }

    public static ProfileConfigurationFile Open(string path) => new(path, Read(path));

    public bool Save(string text)
    {
        lock (SaveLock)
        {
            if (text == Text)
            {
                CheckOriginal(Read(_path));
                return false;
            }

            var changed = Utf8.GetBytes(text);
            if (changed.Length > MaxBytes)
                throw new IOException("Файл настроек слишком большой для безопасного редактирования.");
            var staging = _path + "." + Guid.NewGuid().ToString("N");
            var backup = staging + ".previous";
            try
            {
                using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(changed);
                    output.Flush(flushToDisk: true);
                }
                // Deny in-place writes on Windows. A writer using its own rename can still race;
                // check and retain the actual displaced bytes instead of discarding them.
                using var current = OpenRead(_path);
                CheckOriginal(Read(current));
                CheckOriginal(Read(_path));
                File.Replace(staging, _path, backup);
                if (!Read(backup).AsSpan().SequenceEqual(_original))
                    RestoreExternalEdit(backup, changed);
                TryDelete(backup);
                _original = changed;
                Text = text;
                return true;
            }
            finally
            {
                TryDelete(staging);
            }
        }
    }

    private void RestoreExternalEdit(string backup, byte[] changed)
    {
        var intervening = backup + ".intervening";
        try
        {
            using var replacement = OpenRead(_path);
            if (!Read(replacement).AsSpan().SequenceEqual(changed) || !Read(_path).AsSpan().SequenceEqual(changed))
                throw Conflict(backup);
            File.Replace(backup, _path, intervening);
            if (!Read(intervening).AsSpan().SequenceEqual(changed))
                throw Conflict(intervening);
            TryDelete(intervening);
        }
        catch (Exception error) when ((error is IOException or UnauthorizedAccessException)
                                      && error is not ProfileConfigurationConflictException)
        {
            throw Conflict(File.Exists(backup) ? backup : File.Exists(intervening) ? intervening : null);
        }
        throw Conflict(null);
    }

    private void CheckOriginal(byte[] current)
    {
        if (!current.AsSpan().SequenceEqual(_original))
            throw Conflict(null);
    }

    private static ProfileConfigurationConflictException Conflict(string? preservedCopy) => new(
        "Файл настроек изменился вне панели. Перечитай его перед сохранением."
        + (preservedCopy is null ? "" : $" Копия внешней правки: {preservedCopy}"));

    private static FileStream OpenRead(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Панель не редактирует файл настроек по ссылке.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
    }

    private static byte[] Read(string path)
    {
        using var file = OpenRead(path);
        return Read(file);
    }

    private static byte[] Read(FileStream file)
    {
        if (file.Length > MaxBytes)
            throw new IOException("Файл настроек слишком большой для безопасного редактирования.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1)
            throw Conflict(null);
        return bytes;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
