#include <iostream>
#include "../CorePlugin/CorePlugin.h"
#include "../StaticMath/StaticMath.h"

int main()
{
    int value = ComputePluginValue(10);
    int total = StaticMath::Add(value, 5);
    std::cout << "Result: " << total << std::endl;
    return 0;
}

