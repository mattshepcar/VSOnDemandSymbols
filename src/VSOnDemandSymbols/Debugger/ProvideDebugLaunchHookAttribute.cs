using System;
using Microsoft.VisualStudio.Shell;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// Registers an <see cref="Microsoft.VisualStudio.Shell.Interop.IVsDebugLaunchHook"/> under
/// <c>[$RootKey$\Debugger\LaunchHooks110]</c> in the Visual Studio registry (ADR-15).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class ProvideDebugLaunchHookAttribute : RegistrationAttribute
{
    private readonly Type _hookType;
    private readonly string _hookName;

    public ProvideDebugLaunchHookAttribute(Type hookType, string hookName)
    {
        _hookType = hookType ?? throw new ArgumentNullException(nameof(hookType));
        _hookName = hookName ?? throw new ArgumentNullException(nameof(hookName));
    }

    public override void Register(RegistrationContext context)
    {
        using Key hookKey = context.CreateKey(@"Debugger\LaunchHooks110");
        hookKey.SetValue(_hookName, _hookType.GUID.ToString("B"));
    }

    public override void Unregister(RegistrationContext context)
    {
        context.RemoveValue(@"Debugger\LaunchHooks110", _hookName);
    }
}
