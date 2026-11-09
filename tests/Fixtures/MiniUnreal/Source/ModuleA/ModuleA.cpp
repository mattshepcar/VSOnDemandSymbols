#include "ModuleA.h"
#include "../ModuleB/ModuleB.h"

extern "C"
{
    MODULEA_API __declspec(noinline) int RunModuleAWork(int value)
    {
        int doubled = value * 2; // Line 8: Pre-launch breakpoint target inside ModuleA.cpp
        return doubled + 1;
    }

    MODULEA_API __declspec(noinline) int DispatchToModuleB(int seed)
    {
        int adjusted = seed + 10;
        return TriggerCrashInModuleB(adjusted); // Line 15: Calls into ModuleB
    }
}

