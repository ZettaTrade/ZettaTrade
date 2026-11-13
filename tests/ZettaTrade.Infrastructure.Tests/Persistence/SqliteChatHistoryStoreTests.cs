namespace ZettaTrade.Infrastructure.Tests.Persistence;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Tests for the SQLite chat history store (T-206): the picker's list
/// (newest update first, the append bump riding it), the transcript replay
/// in insertion order, the round-trip across a second store instance, the
/// honest drop of an append to an unknown conversation, and the rename's
/// title-only discipline.
/// </summary>
public sealed class SqliteChatHistoryStoreTests
{
    #region Test Constants

    private static readonly DateTimeOffset BaseTime = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    #endregion Test Constants

    #region Helpers

    private static async Task WithTempDatabaseAsync(Func<(string Directory, string File), Task> testBody)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"zettatrade-chat-history-{Guid.NewGuid():N}");
        var file = Path.Combine(directory, "sub", "chat-history.db");
        try
        {
            await testBody((directory, file));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static SqliteChatHistoryStore CreateStore(string databaseFile) => new(
        new DbContextOptionsBuilder<ChatHistoryDbContext>()
            // Pooling=false: after each context disposes, its SQLite
            // connection closes, so the temp database file is unlocked.
            .UseSqlite($"Data Source={databaseFile};Pooling=false")
            .Options,
        NullLogger<SqliteChatHistoryStore>.Instance);

    /// <summary>A raw context over the store's own database, for the inspection reads.</summary>
    private static ChatHistoryDbContext OpenDatabase(string databaseFile) => new(
        new DbContextOptionsBuilder<ChatHistoryDbContext>()
            .UseSqlite($"Data Source={databaseFile};Pooling=false")
            .Options);

    #endregion Helpers

    #region Tests

    [Fact]
    public async Task Initialize_CreatesTheMissingDirectory_AndAppliesMigrations()
    {
        await WithTempDatabaseAsync(async paths =>
        {
            Directory.Exists(paths.Directory).Should().BeFalse("the store owns its clean-room setup.");

            var store = CreateStore(paths.File);
            await store.InitializeAsync();

            Directory.Exists(paths.Directory).Should().BeTrue("the store must not assume a prepared data directory.");
            await using var database = OpenDatabase(paths.File);
            (await database.Database.CanConnectAsync()).Should().BeTrue("the schema migrated.");
        });
    }

    [Fact]
    public async Task Calls_BeforeInitializeAsync_SelfHealTheSchema()
    {
        await WithTempDatabaseAsync(async paths =>
        {
            // The cold-start shape the startup walk must not own: a chat
            // that loads first never waits for an init nobody ran yet.
            var store = CreateStore(paths.File);
            var created = await store.CreateConversationAsync();

            await store.AppendMessageAsync(
                created.Id,
                new ChatHistoryMessage(ChatHistoryMessage.UserKind, "hello", BaseTime));

            (await store.LoadMessagesAsync(created.Id))
                .Select(message => (message.Kind, message.Content))
                .Should().Equal([(ChatHistoryMessage.UserKind, "hello")],
                    "the first chat call carries the one-time migration itself.");
        });
    }

    [Fact]
    public async Task ConcurrentFirstCalls_ShareTheOneTimeMigration()
    {
        await WithTempDatabaseAsync(async paths =>
        {
            var store = CreateStore(paths.File);
            var create = store.CreateConversationAsync();
            var sibling = store.ListConversationsAsync();

            // Both calls race the readiness gate on a never-initialized
            // store; the serialized migration leaves exactly one schema and
            // both callers served.
            await Task.WhenAll(create, sibling);

            (await store.LoadMessagesAsync((await create).Id))
                .Should().BeEmpty("the concurrent winner's row rides the migrated schema.");
        });
    }

    [Fact]
    public async Task List_AnEmptyStore_ReturnsEmpty()
    {
        await WithTempDatabaseAsync(async paths =>
        {
            var store = CreateStore(paths.File);
            await store.InitializeAsync();

            (await store.ListConversationsAsync())
                .Should().BeEmpty("a fresh installation's picker is empty, never null.");
        });
    }

    [Fact]
    public async Task CreateThenList_OrdersNewestFirst_AndRoundTripsAcrossASecondStore()
    {
        await WithTempDatabaseAsync(async paths =>
        {
            var first = CreateStore(paths.File);
            await first.InitializeAsync();
            var older = await first.CreateConversationAsync();
            await Task.Delay(50);
            var newer = await first.CreateConversationAsync();

            // The picker reads the newest first — and the homecoming read
            // through a fresh store instance replays the exact same order
            // and records (the persistence acceptance).
            var second = CreateStore(paths.File);
            await second.InitializeAsync();
            var listed = await second.ListConversationsAsync();

            listed.Select(conversation => conversation.Id)
                .Should().Equal([newer.Id, older.Id],
                    "the picker's newest-first order is the store's own answer.");
            listed.Select(conversation => conversation.Title)
                .Should().OnlyContain(title => title == null,
                    "a fresh conversation is untitled until its first message.");
            listed.Single(conversation => conversation.Id == older.Id).CreatedUtc
                .Should().Be(older.CreatedUtc, "the creation moment round-trips.");
            listed.Single(conversation => conversation.Id == older.Id).UpdatedUtc
                .Should().Be(older.UpdatedUtc, "the update moment round-trips.");
        });
    }

    [Fact]
    public async Task Append_BumpsTheConversationToTheTop_AndTheTranscriptReplaysInInsertionOrder()
    {
        await WithTempDatabaseAsync(async paths =>
        {
            var first = CreateStore(paths.File);
            await first.InitializeAsync();
            var quiet = await first.CreateConversationAsync();
            await Task.Delay(50);
            var louder = await first.CreateConversationAsync();

            await first.AppendMessageAsync(
                quiet.Id,
                new ChatHistoryMessage(ChatHistoryMessage.UserKind, "hello", BaseTime));
            await first.AppendMessageAsync(
                quiet.Id,
                new ChatHistoryMessage(ChatHistoryMessage.AssistantKind, "hi there", BaseTime.AddMinutes(2)));
            await first.AppendMessageAsync(
                quiet.Id,
                new ChatHistoryMessage(ChatHistoryMessage.ErrorKind, "boom", BaseTime.AddMinutes(4)));

            // The bump: the appended conversation outranks the sibling that
            // was created later but never spoke.
            (await first.ListConversationsAsync())
                .Select(conversation => conversation.Id)
                .Should().Equal([quiet.Id, louder.Id],
                    "an append lifts the conversation to the list's top.");

            // The homecoming: a fresh store instance replays the transcript
            // in the order it was spoken, kinds and moments included.
            var second = CreateStore(paths.File);
            await second.InitializeAsync();
            var transcript = await second.LoadMessagesAsync(quiet.Id);

            transcript.Select(message => (message.Kind, message.Content))
                .Should().Equal(
                    (ChatHistoryMessage.UserKind, "hello"),
                    (ChatHistoryMessage.AssistantKind, "hi there"),
                    (ChatHistoryMessage.ErrorKind, "boom"));
            transcript.Select(message => message.TimestampUtc)
                .Should().Equal(
                    [
                        BaseTime,
                        BaseTime.AddMinutes(2),
                        BaseTime.AddMinutes(4),
                    ],
                    "the spoken moments round-trip exactly.");
        });
    }

    [Fact]
    public async Task Append_UnknownConversation_IsADroppedNoOp()
    {
        await WithTempDatabaseAsync(async paths =>
        {
            var store = CreateStore(paths.File);
            await store.InitializeAsync();
            var created = await store.CreateConversationAsync();

            await store.AppendMessageAsync(
                Guid.NewGuid(),
                new ChatHistoryMessage(ChatHistoryMessage.UserKind, "lost", BaseTime));

            (await store.LoadMessagesAsync(created.Id))
                .Should().BeEmpty("the drop touched nobody's transcript.");
            (await store.ListConversationsAsync())
                .Should().ContainSingle("the unknown conversation's append created no row.");
        });
    }

    [Fact]
    public async Task Rename_WritesTheTitle_WithoutReordering()
    {
        await WithTempDatabaseAsync(async paths =>
        {
            var store = CreateStore(paths.File);
            await store.InitializeAsync();
            var older = await store.CreateConversationAsync();
            await Task.Delay(50);
            var newer = await store.CreateConversationAsync();

            await store.RenameConversationAsync(older.Id, "supply notes");

            // A rename titles the conversation and never reorders: the
            // bumped sibling keeps its lead.
            var listed = await store.ListConversationsAsync();
            listed.Select(conversation => conversation.Id)
                .Should().Equal([newer.Id, older.Id],
                    "a rename is a title write, not an update bump.");
            listed.Single(conversation => conversation.Id == older.Id).Title
                .Should().Be("supply notes", "the auto-title's rename wrote the title.");

            // The title can also clear: the restore falls back to its
            // localized default for a null.
            await store.RenameConversationAsync(older.Id, null);
            (await store.ListConversationsAsync())
                .Single(conversation => conversation.Id == older.Id).Title
                .Should().BeNull("the title clears to the untitled state.");
        });
    }

    #endregion Tests
}
