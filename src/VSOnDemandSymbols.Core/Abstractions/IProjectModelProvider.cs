using System.Collections.Generic;

namespace VSOnDemandSymbols.Core.Abstractions;

public interface IProjectModelProvider
{
    IReadOnlyList<string> GetOutputDirectories();
    IReadOnlyList<string> ResolveSourceFileToOutputs(string sourceFilePath);
}

