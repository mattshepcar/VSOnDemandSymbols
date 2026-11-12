using System;
using System.Collections.Generic;
using System.IO;

namespace VSOnDemandSymbols.Core.Abstractions;

public interface IFileLease : IDisposable
{
    void Update(string contents);
}

public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    string ReadAllText(string path);
    string ReadSharedText(string path);
    void WriteAllText(string path, string contents);
    void DeleteFile(string path);
    bool TryDeleteFile(string path);
    IFileLease? TryAcquireLease(string path, string contents);
    void ReplaceFile(string sourceFileName, string destinationFileName, string? destinationBackupFileName);
    IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption);
    void CreateDirectory(string path);
}

public sealed class PhysicalFileSystem : IFileSystem
{
    public static readonly PhysicalFileSystem Instance = new();

    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public string ReadAllText(string path) => File.ReadAllText(path);

    public string ReadSharedText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);
    public void DeleteFile(string path) => File.Delete(path);

    public bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            // File is locked by another process (sharing violation)
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public IFileLease? TryAcquireLease(string path, string contents)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir!))
            {
                Directory.CreateDirectory(dir!);
            }

            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            var lease = new PhysicalFileLease(stream, path);
            lease.Update(contents);
            return lease;
        }
        catch
        {
            return null;
        }
    }

    public void ReplaceFile(string sourceFileName, string destinationFileName, string? destinationBackupFileName)
    {
        if (File.Exists(destinationFileName))
        {
            File.Replace(sourceFileName, destinationFileName, destinationBackupFileName, ignoreMetadataErrors: true);
        }
        else
        {
            if (destinationBackupFileName != null && File.Exists(destinationBackupFileName))
            {
                File.Delete(destinationBackupFileName);
            }
            File.Move(sourceFileName, destinationFileName);
        }
    }

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption)
        => Directory.EnumerateFiles(path, searchPattern, searchOption);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    private sealed class PhysicalFileLease : IFileLease
    {
        private readonly FileStream _stream;
        private readonly string _path;
        private bool _disposed;

        public PhysicalFileLease(FileStream stream, string path)
        {
            _stream = stream;
            _path = path;
        }

        public void Update(string contents)
        {
            if (_disposed) return;
            _stream.Seek(0, SeekOrigin.Begin);
            _stream.SetLength(0);
            using var writer = new StreamWriter(_stream, System.Text.Encoding.UTF8, 1024, leaveOpen: true);
            writer.Write(contents);
            writer.Flush();
            _stream.Flush();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stream.Dispose();
            try
            {
                File.Delete(_path);
            }
            catch
            {
                // Ignore cleanup error on dispose
            }
        }
    }
}
