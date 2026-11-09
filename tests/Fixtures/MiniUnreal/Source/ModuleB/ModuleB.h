#pragma once

#ifdef MODULEB_EXPORTS
#define MODULEB_API __declspec(dllexport)
#else
#define MODULEB_API __declspec(dllimport)
#endif

extern "C"
{
    MODULEB_API int PassiveModuleBHelper(int value);
    MODULEB_API int TriggerCrashInModuleB(int seed);
    MODULEB_API int TickModuleB(int iteration);
}

