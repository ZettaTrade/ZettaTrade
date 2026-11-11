namespace ZettaTrade.E2E.Tests;

using Xunit;

/// <summary>
/// The whole E2E lane rides one collection around one app process: the
/// flows stay sequential over a single live workspace (a second
/// concurrent launch would meet the per-profile single-instance claim),
/// and each flow leaves the state it found.
/// </summary>
[CollectionDefinition("E2E")]
public sealed class E2ECollection : ICollectionFixture<E2EAppFixture>
{
}
