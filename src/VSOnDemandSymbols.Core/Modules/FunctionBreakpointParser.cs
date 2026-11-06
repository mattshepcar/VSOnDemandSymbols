using System;

namespace VSOnDemandSymbols.Core.Modules;

/// <summary>
/// Extracts module qualifiers from Visual Studio function breakpoint expressions (SPEC §5.1 BP-4).
/// </summary>
public static class FunctionBreakpointParser
{
    /// <summary>
    /// Attempts to extract the module name from a module-qualified function breakpoint expression:
    /// <list type="bullet">
    /// <item><description><c>UnrealEditor-MyGame.dll!AMyActor::Tick</c></description></item>
    /// <item><description><c>{,,UnrealEditor-MyGame.dll}AMyActor::Tick</c></description></item>
    /// </list>
    /// Returns <c>false</c> for unqualified function breakpoints (e.g. <c>AMyActor::Tick</c>).
    /// </summary>
    public static bool TryExtractModuleName(string? functionExpression, out string? moduleName)
    {
        moduleName = null;
        if (string.IsNullOrWhiteSpace(functionExpression))
        {
            return false;
        }

        string trimmed = functionExpression!.Trim();

        // Format 1: UnrealEditor-MyGame.dll!AMyActor::Tick
        int bangIndex = trimmed.IndexOf('!');
        if (bangIndex > 0)
        {
            string prefix = trimmed.Substring(0, bangIndex).Trim();
            if (!prefix.StartsWith("{", StringComparison.Ordinal) && prefix.Length > 0)
            {
                moduleName = prefix;
                return true;
            }
        }

        // Format 2: {,,UnrealEditor-MyGame.dll}AMyActor::Tick
        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            int closeBrace = trimmed.IndexOf('}');
            if (closeBrace > 1)
            {
                string inside = trimmed.Substring(1, closeBrace - 1);
                string[] parts = inside.Split(',');
                if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]))
                {
                    moduleName = parts[2].Trim();
                    return true;
                }
            }
        }

        return false;
    }
}

