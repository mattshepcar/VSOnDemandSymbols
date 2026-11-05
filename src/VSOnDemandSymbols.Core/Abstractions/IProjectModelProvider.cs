using System.Collections.Generic;

namespace VSOnDemandSymbols.Core.Abstractions;

public interface IProjectModelProvider
{
    IReadOnlyList<string> ResolveSourceFileToOutputs(string sourceFilePath);
}
