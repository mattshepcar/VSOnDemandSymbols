#include "ModuleB.h"
#include <intrin.h>

extern "C"
{
    MODULEB_API __declspec(noinline) int PassiveModuleBHelper(int value)
    {
        int computed = value + 100; // Line 8
        return computed;
    }

    MODULEB_API __declspec(noinline) int TriggerCrashInModuleB(int seed)
    {
        int localState = seed + 42;
        __debugbreak(); // Line 15: Crash / break target inside ModuleB.cpp
        return localState;
    }

    MODULEB_API __declspec(noinline) int TickModuleB(int iteration)
    {
        int step = iteration + 1; // Line 21: Mid-session breakpoint target inside ModuleB.cpp
        return step;
    }
}

