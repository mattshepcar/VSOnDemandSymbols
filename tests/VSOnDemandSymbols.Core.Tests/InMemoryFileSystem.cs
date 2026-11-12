using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using VSOnDemandSymbols.Core.Abstractions;

namespace VSOnDemandSymbols.Core.Tests;

public sealed class InMemoryFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _lockedFiles = new(StringComparer.OrdinalIgnoreCase);

    public bool FileExists(string path) => _files.ContainsKey(path);
    public bool DirectoryExists(string path) => _directories.Contains(path);

    public string ReadAllText(string path)
    {
        if (!_files.TryGetValue(path, out var content))
        {
            throw new FileNotFoundException("File not found", path);
        }
        return content;
    }

    public string ReadSharedText(string path) => ReadAllText(path);

    public void WriteAllText(string path, string contents)
    {
        _files[path] = contents;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            _directories.Add(dir);
        }
    }

    public void DeleteFile(string path)
    {
        _files.Remove(path);
        _lockedFiles.Remove(path);
    }

    public bool TryDeleteFile(string path)
    {
        if (_lockedFiles.Contains(path))
        {
            // Simulated sharing violation (file locked by active lease)
            return false;
        }

        _files.Remove(path);
        return true;
    }

    public IFileLease? TryAcquireLease(string path, string contents)
    {
        if (_lockedFiles.Contains(path))
        {
            return null;
        }

        WriteAllText(path, contents);
        _lockedFiles.Add(path);
        return new InMemoryFileLease(this, path);
    }

    public void ReplaceFile(string sourceFileName, string destinationFileName, string? destinationBackupFileName)
    {
        if (!_files.TryGetValue(sourceFileName, out var content))
        {
            throw new FileNotFoundException("Source file not found", sourceFileName);
        }

        if (destinationBackupFileName != null && _files.TryGetValue(destinationFileName, out var existing))
        {
            _files[destinationBackupFileName] = existing;
        }

        _files.Remove(sourceFileName);
        _files[destinationFileName] = content;
    }

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption)
    {
        string normPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var patternRegex = PatternToRegex(searchPattern);

        var list = new List<string>();
        foreach (var key in _files.Keys)
        {
            string normKey = Path.GetFullPath(key);
            string? dir = Path.GetDirectoryName(normKey)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            bool inScope;
            if (searchOption == SearchOption.TopDirectoryOnly)
            {
                inScope = string.Equals(dir, normPath, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                inScope = normKey.StartsWith(normPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }

            if (inScope)
            {
                string fn = Path.GetFileName(normKey);
                if (patternRegex.IsMatch(fn))
                {
                    list.Add(key);
                }
            }
        }
        return list;
    }

    public void CreateDirectory(string path)
    {
        _directories.Add(path);
    }

    private static Regex PatternToRegex(string pattern)
    {
        if (pattern == "*.*" || pattern == "*")
        {
            return new Regex(".*", RegexOptions.IgnoreCase);
        }

        string escaped = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase);
    }

    private sealed class InMemoryFileLease : IFileLease
    {
        private readonly InMemoryFileSystem _fs;
        private readonly string _path;
        private bool _disposed;

        public InMemoryFileLease(InMemoryFileSystem fs, string path)
        {
            _fs = fs;
            _path = path;
        }

        public void Update(string contents)
        {
            if (_disposed) return;
            _fs.WriteAllText(_path, contents);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _fs._lockedFiles.Remove(_path);
            _fs._files.Remove(_path);
        }
    }
}
