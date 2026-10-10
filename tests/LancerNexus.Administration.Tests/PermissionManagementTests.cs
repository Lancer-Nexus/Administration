using LancerNexus.Administration.Api;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LancerNexus.Administration.Tests;

public sealed class PermissionManagementTests
{
    [Fact]
    public async Task EmptyActiveInstanceSetIsAcknowledgedWithoutOpeningDatabaseConnection()
    {
        var configuration = new ConfigurationBuilder().Build();
        var store = new PermissionManagementStore(configuration, TimeProvider.System);

        var acknowledged = await store.AreAcknowledgedAsync(12, Array.Empty<string>(), CancellationToken.None);

        Assert.True(acknowledged);
    }

    [Fact]
    public async Task NegativeRevisionIsRejectedWithoutOpeningDatabaseConnection()
    {
        var configuration = new ConfigurationBuilder().Build();
        var store = new PermissionManagementStore(configuration, TimeProvider.System);

        var acknowledged = await store.AreAcknowledgedAsync(-1, new[] { "li-01" }, CancellationToken.None);

        Assert.False(acknowledged);
    }

    [Fact]
    public async Task NullMutationRequestIsRejectedWithoutOpeningDatabaseConnection()
    {
        var store = new PermissionManagementStore(new ConfigurationBuilder().Build(), TimeProvider.System);

        var result = await store.MutateAsync(null!, CancellationToken.None);

        Assert.Equal("invalid", result.Status);
    }

    [Fact]
    public async Task MissingMutationPayloadIsRejectedWithoutOpeningDatabaseConnection()
    {
        var store = new PermissionManagementStore(new ConfigurationBuilder().Build(), TimeProvider.System);
        var request = new AuthorizedPermissionMutation(Guid.NewGuid(), "rest", Guid.NewGuid(), "key", null!);

        var result = await store.MutateAsync(request, CancellationToken.None);

        Assert.Equal("invalid", result.Status);
    }
}
