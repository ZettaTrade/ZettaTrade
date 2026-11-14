namespace ZettaTrade.App.Tests.DependencyInjection;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// The startup walk's store batch (T-226): the SQLite stores'
/// <see cref="IAsyncInitializable"/> migrations are mutually independent
/// database files, so the walk starts the whole batch CONCURRENTLY at the
/// earliest store's walk position and awaits it there — every later walk
/// step still sees every store ready while the batch's wall time becomes
/// the slowest store instead of the sum. The facts prove the mechanics on
/// the production wiring itself (the real <see cref="CompositionRoot.StoreInitBatchTypes"/>
/// list and the real call-site predicate) with substituted stores.
/// </summary>
public sealed class CompositionRootWalkTests
{
    /// <summary>
    /// The batch runs its stores concurrently: the chat store's
    /// initialization parks on the order store's START — a serialized walk
    /// would deadlock there and time the fact out, so the walk completing
    /// at all IS the concurrency proof; the recorded order pins the overlap
    /// ("order:start" precedes "chat:finish") and the batch's barrier (the
    /// post-batch guest initializes only after the whole batch finished).
    /// The later batched descriptor (the order store's own walk position)
    /// re-enters the batch machinery and drops through the once-per-instance
    /// dedup — no store initializes twice.
    /// </summary>
    [Fact]
    public async Task TheWalk_BatchesTheStores_ConcurrentlyAtTheEarliestStore()
    {
        var order = new ConcurrentQueue<string>();
        var orderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The initializable face leads the substitute pair: the walk's
        // batch machinery calls InitializeAsync through it, while the store
        // face is what the registration (and the predicate) resolves.
        var chat = Substitute.For<IAsyncInitializable, IChatHistoryStore>();
        chat.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo => ParkUntilTheSecondStoreStartsAsync(
                order,
                orderStarted.Task,
                callInfo.Arg<CancellationToken>()));

        var orderStore = Substitute.For<IAsyncInitializable, IOrderHistoryStore>();
        orderStore.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                order.Enqueue("order:start");
                orderStarted.SetResult();
                order.Enqueue("order:finish");
                return Task.CompletedTask;
            });

        // A post-batch walk step: not a store, so the regular walk
        // initializes it AFTER the batch's barrier.
        var guest = Substitute.For<IAsyncInitializable>();
        guest.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                order.Enqueue("guest:start");
                return Task.CompletedTask;
            });

        var services = new ServiceCollection();
        services.AddSingleton(typeof(IChatHistoryStore), chat);
        services.AddSingleton(typeof(IOrderHistoryStore), orderStore);
        services.AddSingleton(guest);

        using var provider = services.BuildServiceProvider();

        Func<Task> act = () => CompositionRoot.InitializeRegisteredServicesAsync(
            provider,
            services,
            CancellationToken.None,
            static serviceType => CompositionRoot.StoreInitBatchTypes.Contains(serviceType));

        // A serialized walk parks the chat store on the order store's start
        // and dies in the timeout below — completing at all is the proof.
        await act.Should().NotThrowAsync(
            "the batch's stores initialize concurrently, never one after another");

        order.Should().ContainInOrder(
            new List<string> { "chat:start", "order:start", "chat:finish", "guest:start" },
            "the stores overlap (the order store started before the chat store finished) and the post-batch guest follows the whole batch");

        order.Should().HaveCount(5, "no store initializes twice — the later batched descriptor dedups");
    }

    /// <summary>
    /// The batch's fail-fast contract (BUG-019) survives the concurrency: a
    /// store whose migration faults surfaces the exception through the
    /// batch's await exactly as a serialized walk would, while the sibling
    /// store still STARTED (the batch starts the whole list before awaiting).
    /// </summary>
    [Fact]
    public async Task TheWalk_AFaultingStore_PropagatesThroughTheBatch()
    {
        var order = new ConcurrentQueue<string>();

        var chat = Substitute.For<IAsyncInitializable, IChatHistoryStore>();
        chat.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(_ => FaultAsync(order));

        var orderStore = Substitute.For<IAsyncInitializable, IOrderHistoryStore>();
        orderStore.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                order.Enqueue("order:start");
                return Task.CompletedTask;
            });

        var services = new ServiceCollection();
        services.AddSingleton(typeof(IChatHistoryStore), chat);
        services.AddSingleton(typeof(IOrderHistoryStore), orderStore);

        using var provider = services.BuildServiceProvider();

        Func<Task> act = () => CompositionRoot.InitializeRegisteredServicesAsync(
            provider,
            services,
            CancellationToken.None,
            static serviceType => CompositionRoot.StoreInitBatchTypes.Contains(serviceType));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("the chat migration failed");

        order.Should().Contain("order:start", "the batch started every store before awaiting the fault");
    }

    /// <summary>
    /// The production batch list pins the five SQLite stores, the chat
    /// history first: its registration sits at the earliest store's walk
    /// position, so the batch awaits there and everything after it — the
    /// chat ViewModel's restore included — sees every store ready.
    /// </summary>
    [Fact]
    public void TheBatch_CoversExactlyTheFiveSqliteStores()
    {
        CompositionRoot.StoreInitBatchTypes.Should().Equal(
            typeof(IChatHistoryStore),
            typeof(IOrderHistoryStore),
            typeof(IPortfolioValueStore),
            typeof(ICandleCache),
            typeof(IChartDrawingStore));
    }

    private static async Task ParkUntilTheSecondStoreStartsAsync(
        ConcurrentQueue<string> order,
        Task secondStarted,
        CancellationToken ct)
    {
        order.Enqueue("chat:start");

        // The timeout keeps a REGRESSED (serialized) walk from deadlocking
        // the fact forever: the wait faults instead.
        await secondStarted.WaitAsync(TimeSpan.FromSeconds(2), ct);

        order.Enqueue("chat:finish");
    }

    private static async Task FaultAsync(ConcurrentQueue<string> order)
    {
        order.Enqueue("chat:start");

        // The fault must land INSIDE the task, not at the call: a call-time
        // throw aborts the batch's enumeration before the sibling starts.
        await Task.Yield();

        throw new InvalidOperationException("the chat migration failed");
    }
}
