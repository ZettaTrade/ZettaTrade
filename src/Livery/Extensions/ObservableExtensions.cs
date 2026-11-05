namespace Livery.Extensions;

using System;
using System.Reactive.Linq;
using Avalonia.Threading;

/// <summary>
/// Extension methods for observables to work with Avalonia UI thread.
/// Does not depend on ReactiveUI or Avalonia.ReactiveUI packages.
/// </summary>
public static class ObservableExtensions
{
    /// <summary>
    /// Marshals observable notifications onto the Avalonia UI thread.
    /// Uses <see cref="Dispatcher.UIThread"/> directly, so it works in both
    /// Avalonia 11 and Avalonia 12 without extra dependencies.
    /// </summary>
    public static IObservable<T> ObserveOnUiThread<T>(this IObservable<T> source) =>
        Observable.Create<T>(observer => source.Subscribe(
            value => RunOnUiThread(() => observer.OnNext(value)),
            error => RunOnUiThread(() => observer.OnError(error)),
            () => RunOnUiThread(observer.OnCompleted)));

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
