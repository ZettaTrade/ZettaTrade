namespace ZettaTrade.UI.Extensions;

using System;
using System.Reactive.Linq;
using System.Threading.Tasks;

/// <summary>
/// Task-flavored subscription surface for observables that trigger async
/// work: every notification spawns its awaited continuation serialized in
/// emission order (each run starts only after the previous one completed,
/// dependent steps never interleave), and a stream failure lands in a
/// single error callback — the Subscribe(onError) discipline without
/// async void at the call site. Callers that need cross-thread healing
/// combine it with <see cref="ObservableExtensions.ObserveOnUiThread{T}"/>
/// first.
/// </summary>
public static class ObservableAsyncExtensions
{
    /// <summary>
    /// Subscribe a source whose notifications launch awaited tasks. Runs
    /// go strictly one at a time in emission order (BUG-025): while a run
    /// is pending, later notifications queue, and each queued run starts
    /// the moment its predecessor completes — the idle pipeline starts
    /// its run on the emission thread, a queued run resumes on the thread
    /// that completed the predecessor. The stream keeps the subscription
    /// alive until the returned handle is disposed; an unhandled stream
    /// or run error is routed to <paramref name="onError"/> (subsequent
    /// emissions end).
    /// </summary>
    /// <typeparam name="T">The notification payload type.</typeparam>
    /// <param name="source">The source stream of notifications.</param>
    /// <param name="run">The awaited work per notification.</param>
    /// <param name="onError">Final error sink of the subscription.</param>
    /// <returns>The subscription handle.</returns>
    public static IDisposable SubscribeAsync<T>(
        this IObservable<T> source,
        Func<T, Task> run,
        Action<Exception> onError) =>
        source
            .Select(item => Observable.FromAsync(() => run(item)))
            .Concat()
            .Subscribe(
                _ => { },
                onError);
}
