using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace VSOnDemandSymbols.Core.Policy;

public sealed class WildcardPattern
{
    private static readonly char[] PathSeparators = { '\\', '/' };
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> KnownSystemDlls = new(StringComparer.OrdinalIgnoreCase)
    {
        "ntdll.dll",
        "kernel32.dll",
        "kernelbase.dll",
        "user32.dll",
        "win32u.dll",
        "gdi32.dll",
        "gdi32full.dll",
        "mswsock.dll",
        "ws2_32.dll",
        "wsock32.dll",
        "dxgi.dll",
        "d3d11.dll",
        "d3d12.dll",
        "combase.dll",
        "ole32.dll",
        "oleaut32.dll",
        "rpcrt4.dll",
        "shell32.dll",
        "shlwapi.dll",
        "advapi32.dll",
        "sechost.dll",
        "crypt32.dll",
        "bcrypt.dll",
        "bcryptprimitives.dll",
        "imm32.dll",
        "cfgmgr32.dll",
        "wshtcpip.dll",
        "wship6.dll",
        "nsi.dll",
        "dnsapi.dll",
        "iphlpapi.dll",
        "setupapi.dll",
        "msvcp140.dll",
        "msvcp140_1.dll",
        "msvcp140_2.dll",
        "msvcp140_atomic_wait.dll",
        "msvcp140_codecvt_ids.dll",
        "vcruntime140.dll",
        "vcruntime140_1.dll",
        "ucrtbase.dll",
        "dbghelp.dll",
        "dbgcore.dll"
    };

    private static readonly string WindowsDirectoryPrefix = GetWindowsDirectoryPrefix();

    private static string GetWindowsDirectoryPrefix()
    {
        try
        {
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(winDir))
            {
                return winDir.TrimEnd('\\', '/') + "\\";
            }
        }
        catch
        {
        }

        return "C:\\Windows\\";
    }

    private readonly Regex _regex;

    public string Pattern { get; }
    public string RawPattern => Pattern;

    public WildcardPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ArgumentException("Pattern cannot be empty or whitespace.", nameof(pattern));
        }

        Pattern = pattern.Trim();
        _regex = RegexCache.GetOrAdd(Pattern, BuildRegex);
    }

    public bool IsMatch(string fileNameOrPath)
    {
        if (string.IsNullOrEmpty(fileNameOrPath)) return false;

        string fileName = ExtractFileNameSafe(fileNameOrPath);
        if (fileName.Length == 0) return false;
        return _regex.IsMatch(fileName);
    }

    public bool IsMatchFileName(string fileNameOrPath) => IsMatch(fileNameOrPath);

    /// <summary>
    /// Strips any embedded NUL characters and trims whitespace.
    /// Never throws on invalid path characters.
    /// </summary>
    public static string SanitizePathString(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        int nullIndex = value!.IndexOf('\0');
        if (nullIndex >= 0)
        {
            value = value.Substring(0, nullIndex);
        }

        return value.Trim();
    }

    /// <summary>
    /// Extracts the file name segment from a path or file name without calling <see cref="Path.GetFileName(string)"/>
    /// (which throws <see cref="ArgumentException"/> in .NET Framework when the string contains <c>\0</c> or other invalid characters).
    /// </summary>
    public static string ExtractFileNameSafe(string? fileNameOrPath)
    {
        string clean = SanitizePathString(fileNameOrPath);
        if (clean.Length == 0)
        {
            return string.Empty;
        }

        int lastSep = clean.LastIndexOfAny(PathSeparators);
        return lastSep >= 0 ? clean.Substring(lastSep + 1) : clean;
    }

    /// <summary>
    /// Normalizes a raw stack frame module string (which may contain address offsets, bang suffixes,
    /// or lack the .dll extension) into a candidate binary file name.
    /// Returns null for external code, unknown symbols, or invalid input.
    /// </summary>
    public static string? NormalizeFrameModuleName(string? rawModuleName)
    {
        if (string.IsNullOrWhiteSpace(rawModuleName))
        {
            return null;
        }

        string trimmed = rawModuleName!.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal) || trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            return null;
        }

        int bangIdx = trimmed.IndexOf('!');
        if (bangIdx >= 0)
        {
            trimmed = trimmed.Substring(0, bangIdx).Trim();
        }

        string fileName = ExtractFileNameSafe(trimmed);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        if (fileName.IndexOf('.') < 0)
        {
            return fileName + ".dll";
        }

        return fileName;
    }

    /// <summary>
    /// Determines whether the specified module file name or path belongs to the Windows OS
    /// or standard runtime libraries (e.g. ntdll, kernel32, or located in C:\Windows\System32).
    /// </summary>
    public static bool IsSystemModule(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return false;
        }

        string clean = SanitizePathString(fileNameOrPath);
        if (clean.Length == 0)
        {
            return false;
        }

        if (clean.StartsWith(WindowsDirectoryPrefix, StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("C:\\Windows\\", StringComparison.OrdinalIgnoreCase) ||
            clean.IndexOf("\\Windows\\System32\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
            clean.IndexOf("/Windows/System32/", StringComparison.OrdinalIgnoreCase) >= 0 ||
            clean.IndexOf("\\Windows\\SysWOW64\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
            clean.IndexOf("/Windows/SysWOW64/", StringComparison.OrdinalIgnoreCase) >= 0 ||
            clean.IndexOf("\\Windows\\WinSxS\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
            clean.IndexOf("/Windows/WinSxS/", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        string fileName = ExtractFileNameSafe(clean);
        int bangIdx = fileName.IndexOf('!');
        if (bangIdx >= 0)
        {
            fileName = fileName.Substring(0, bangIdx).Trim();
        }

        if (fileName.IndexOf('.') < 0)
        {
            fileName += ".dll";
        }

        return KnownSystemDlls.Contains(fileName);
    }

    /// <summary>
    /// Determines whether the specified module file name or path represents user-code
    /// (e.g. project or engine binaries like UnrealEditor-*.dll or third-party libraries outside Windows).
    /// </summary>
    public static bool IsUserCodeModule(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return false;
        }

        string? norm = NormalizeFrameModuleName(fileNameOrPath);
        if (string.IsNullOrWhiteSpace(norm))
        {
            return false;
        }

        return !IsSystemModule(fileNameOrPath) && !IsSystemModule(norm);
    }

    public static bool MatchesFileName(string rawPattern, string fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(rawPattern) || string.IsNullOrEmpty(fileNameOrPath))
        {
            return false;
        }

        string fileName = ExtractFileNameSafe(fileNameOrPath);
        if (fileName.Length == 0)
        {
            return false;
        }

        var regex = RegexCache.GetOrAdd(rawPattern.Trim(), BuildRegex);
        return regex.IsMatch(fileName);
    }

    public static bool MatchesAnyFileName(IEnumerable<WildcardPattern>? patterns, string? fileNameOrPath)
    {
        if (patterns == null || string.IsNullOrEmpty(fileNameOrPath))
        {
            return false;
        }

        string fileName = ExtractFileNameSafe(fileNameOrPath);
        if (fileName.Length == 0)
        {
            return false;
        }

        foreach (var p in patterns)
        {
            if (p._regex.IsMatch(fileName))
            {
                return true;
            }
        }

        return false;
    }

    private static Regex BuildRegex(string pattern)
    {
        string escaped = Regex.Escape(pattern);
        string regexPattern = "^" + escaped.Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public override string ToString() => Pattern;
}

