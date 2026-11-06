using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using VSOnDemandSymbols.Core.Policy;

namespace VSOnDemandSymbols.Options;

/// <summary>
/// Provides configuration options in Visual Studio under Tools -> Options -> On-Demand Symbols -> General.
/// </summary>
[ComVisible(true)]
[Guid(PageGuidString)]
public class OnDemandSymbolsOptionPage : DialogPage
{
    public const string PageGuidString = "2071180e-2267-4023-aa18-5217b3f50219";

    /// <summary>
    /// Raised when the user clicks 'OK' or 'Apply' in Tools -> Options.
    /// </summary>
    public event Action? Applied;

    [Category("Break Mode")]
    [DisplayName("Break Symbol Loading")]
    [Description("Controls symbol loading when execution breaks or context changes:\n" +
                 "• LocalOnly: Automatically load locally available and cached symbols without querying remote symbol servers (default).\n" +
                 "• Off: Do not automatically load symbols on break.\n" +
                 "• All: Automatically load symbols querying remote symbol servers if needed.")]
    [DefaultValue(BreakSymbolLoadMode.LocalOnly)]
    public BreakSymbolLoadMode BreakSymbolLoadMode { get; set; } = BreakSymbolLoadModeExtensions.FromEnvironment();

    protected override void OnApply(PageApplyEventArgs e)
    {
        base.OnApply(e);
        if (e.ApplyBehavior == ApplyKind.Apply)
        {
            Applied?.Invoke();
        }
    }
}
