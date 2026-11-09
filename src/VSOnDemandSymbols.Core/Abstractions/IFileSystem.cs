using System.Collections.Generic;
using System.IO;

namespace VSOnDemandSymbols.Core.Abstractions;

public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string contents);
    void DeleteFile(string path);
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
    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);
    public void DeleteFile(string path) => File.Delete(path);
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
}


