namespace ZettaTrade.App.ViewModels;

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using ReactiveUI;

/// <summary>
/// The TwoWay SelectedItem binding race's shared defense (T-225): the
/// pickers write null into their bound selection while their items are
/// still empty or coercing (the bind/init race), and a null is never a
/// user choice — the setters keep the standing option, and one deferred
/// raise (below the render priority) pushes it back into the picker once
/// its items binding has settled and the option (records compare by
/// value) can match. The coalescing bound holds per view model and
/// property: one in-flight re-assert each, never a raise storm.
/// </summary>
internal static class SelectionReAssert
{
    /// <summary>
    /// The in-flight re-asserts, per view model (the entry dies with it)
    /// and property name. The setters and the posted closures both ride
    /// the UI thread in the app, and the tests' serial dispatcher
    /// collection drains before the next fact touches the set, so the
    /// sets need no lock.
    /// </summary>
    private static readonly ConditionalWeakTable<ReactiveObject, HashSet<string>> InFlight = new();

    /// <summary>
    /// Post one deferred re-assert of <paramref name="propertyName"/> on
    /// <paramref name="source"/>; skipped while an equal raise is already
    /// in flight (the coalescing bound).
    /// </summary>
    public static void Later(ReactiveObject source, string propertyName)
    {
        if (!InFlight.GetOrCreateValue(source).Add(propertyName))
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                InFlight.GetOrCreateValue(source).Remove(propertyName);
                source.RaisePropertyChanged(propertyName);
            },
            DispatcherPriority.Background);
    }
}
