#pragma once

#ifdef MODULEA_EXPORTS
#define MODULEA_API __declspec(dllexport)
#else
#define MODULEA_API __declspec(dllimport)
#endif

extern "C"
{
    MODULEA_API int RunModuleAWork(int value);
    MODULEA_API int DispatchToModuleB(int seed);
}

