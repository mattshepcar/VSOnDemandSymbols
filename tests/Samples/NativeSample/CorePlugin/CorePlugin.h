#pragma once

#ifdef COREPLUGIN_EXPORTS
#define COREPLUGIN_API __declspec(dllexport)
#else
#define COREPLUGIN_API __declspec(dllimport)
#endif

extern "C" COREPLUGIN_API int ComputePluginValue(int x);

