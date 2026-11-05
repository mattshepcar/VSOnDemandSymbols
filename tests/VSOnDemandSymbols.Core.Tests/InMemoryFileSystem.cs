using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VSOnDemandSymbols.Core.Abstractions;

namespace VSOnDemandSymbols.Core.Tests;

public sealed class InMemoryFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _timestamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    public bool FileExists(string path) => _files.ContainsKey(path);
    public bool DirectoryExists(string path) => _directories.Contains(path);

    public DateTime GetLastWriteTimeUtc(string path)
        => _timestamps.TryGetValue(path, out var dt) ? dt : DateTime.MinValue;

    public string ReadAllText(string path)
    {
        if (!_files.TryGetValue(path, out var content))
        {
            throw new FileNotFoundException("File not found", path);
        }
        return content;
    }

    public void WriteAllText(string path, string contents)
    {
        _files[path] = contents;
        _timestamps[path] = DateTime.UtcNow;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            _directories.Add(dir);
        }
    }

    public void DeleteFile(string path)
    {
        _files.Remove(path);
        _timestamps.Remove(path);
    }

    public void MoveFile(string sourceFileName, string destFileName)
    {
        if (!_files.TryGetValue(sourceFileName, out var content))
        {
            throw new FileNotFoundException("Source file not found", sourceFileName);
        }
        _files.Remove(sourceFileName);
        _files[destFileName] = content;

        if (_timestamps.TryGetValue(sourceFileName, out var dt))
        {
            _timestamps.Remove(sourceFileName);
            _timestamps[destFileName] = dt;
        }
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

    public IEnumerable<string> EnumerateDirectories(string path)
    {
        return _directories;
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
}
