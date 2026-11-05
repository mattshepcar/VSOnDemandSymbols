using System.Collections.Generic;

namespace VSOnDemandSymbols.Core.Modules;

public interface IModuleBinaryNaming
{
    IReadOnlyList<string> GetCandidateFileNames(ModuleKey key);
    ModuleKey? TryParseLoadedModule(string modulePath);
}

