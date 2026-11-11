namespace ZettaTrade.E2E.Tests;

using System;
using Xunit;

/// <summary>
/// The E2E lane's Windows gate (T-176): the facts drive the real app
/// through Windows UI Automation, so off-Windows hosts skip them instead
/// of failing - the headless pins keep their lane everywhere, the
/// real-process tier runs where the app actually shows windows.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "The FlaUI lane drives Windows UI Automation (T-176): Windows only.";
        }
    }
}
