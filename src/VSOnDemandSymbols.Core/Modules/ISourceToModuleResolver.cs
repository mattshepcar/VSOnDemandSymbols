using System.Collections.Generic;

namespace VSOnDemandSymbols.Core.Modules;

public interface ISourceToModuleResolver
{
    IReadOnlyList<ModuleKey> Resolve(string sourceFilePath);
}
