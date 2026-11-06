#define COREPLUGIN_EXPORTS
#include "CorePlugin.h"
#include "../StaticMath/StaticMath.h"

extern "C" COREPLUGIN_API int ComputePluginValue(int x)
{
    return StaticMath::Add(x, 42);
}

