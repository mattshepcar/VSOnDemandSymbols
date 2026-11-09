#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <string>
#include "../ModuleA/ModuleA.h"
#include "../ModuleB/ModuleB.h"

static std::string ReadTestMode()
{
    char exePath[MAX_PATH] = {};
    GetModuleFileNameA(nullptr, exePath, MAX_PATH);
    std::string dir(exePath);
    size_t lastSlash = dir.find_last_of("\\/");
    if (lastSlash != std::string::npos)
    {
        dir = dir.substr(0, lastSlash + 1);
    }

    std::string modeFile = dir + "test-mode.txt";
    FILE* f = nullptr;
    if (fopen_s(&f, modeFile.c_str(), "r") == 0 && f != nullptr)
    {
        char buf[128] = {};
        if (fgets(buf, sizeof(buf), f) != nullptr)
        {
            fclose(f);
            std::string s(buf);
            while (!s.empty() && (s.back() == '\r' || s.back() == '\n' || s.back() == ' '))
            {
                s.pop_back();
            }
            return s;
        }
        fclose(f);
    }
    return "breakpoint_mod_a";
}

int main(int argc, char** argv)
{
    // Touch PassiveModuleBHelper so both UnrealEditor-ModuleA.dll and UnrealEditor-ModuleB.dll
    // are imported and mapped into the process at startup.
    int warmup = PassiveModuleBHelper(1);

    std::string mode = (argc > 1 && argv[1] != nullptr) ? argv[1] : ReadTestMode();

    if (mode == "crash_mod_b")
    {
        return DispatchToModuleB(warmup);
    }
    else if (mode == "wait_for_bp_mod_b")
    {
        int acc = 0;
        for (int i = 0; i < 300; ++i)
        {
            Sleep(50);
            acc += TickModuleB(i);
        }
        return acc;
    }
    else
    {
        return RunModuleAWork(warmup);
    }
}

