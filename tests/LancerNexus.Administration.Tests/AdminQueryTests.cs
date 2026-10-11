using LancerNexus.Administration.Api;
using LancerNexus.Protocol;
using Xunit;

namespace LancerNexus.Administration.Tests;

public sealed class AdminQueryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static AuthorizedAdminQueryRequest Request(AdminQueryKind kind = AdminQueryKind.Instances, string? target = null) =>
        new()
        {
            AccountId = Guid.NewGuid(),
            Source = "ingame:li-01",
            Query = new AdminQuery
            {
                CorrelationId = Guid.NewGuid(),
                IdempotencyKey = "test",
                Kind = kind,
                TargetInstanceId = target
            }
        };
    private static ClusterInstance Instance(string id, int age = 0) =>
        new(id, "li01", ["li01", "li02"], true, false, true, true, 3, 1, 200, Now.AddSeconds(-age));

    [Fact]
    public async Task UnauthorizedCallerNeverReadsClusterData()
    {
        var source = new Source([Instance("li-01")]);
        var store = new Store([]);
        var response = await new AdminQueryDispatcher(store, source, new Clock()).DispatchAsync(Request(), default);
        Assert.Equal("denied", response.Status);
        Assert.Equal(0, source.Reads);
        Assert.Equal("denied", store.Audited);
    }

    [Fact]
    public async Task InstanceScopeFiltersOtherGroupsAndPrivateEndpointsAreAbsent()
    {
        var response = await new AdminQueryDispatcher(new Store(["li-01"]),
            new Source([Instance("li-01"), Instance("ku-01")]), new Clock()).DispatchAsync(Request(), default);
        Assert.Contains(response.Lines, l => l.StartsWith("li-01:"));
        Assert.DoesNotContain(response.Lines, l => l.Contains("ku-01") || l.Contains("127.0.0.1"));
    }

    [Fact]
    public async Task ExplicitTargetCannotEscapeScope()
    {
        var source = new Source([Instance("ku-01")]);
        var response = await new AdminQueryDispatcher(new Store(["li-01"]), source, new Clock())
            .DispatchAsync(Request(AdminQueryKind.Instance, "ku-01"), default);
        Assert.Equal("denied", response.Status);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task MultiSystemCapacityCountedOnceAndStaleExcluded()
    {
        var response = await new AdminQueryDispatcher(new Store(["*"]),
            new Source([Instance("li-01"), Instance("ku-01", 16)]), new Clock())
            .DispatchAsync(Request(AdminQueryKind.Status), default);
        Assert.Contains(response.Lines, l => l.Contains("Kapazität: 200"));
        Assert.Contains(response.Lines, l => l.Contains(
            "Aktive Spielverbindungen laut frischen Coordinator-Heartbeats: 3; Datenfrische: 1/2 Instanzen; " +
            "nicht nach Konten oder Charakteren dedupliziert."));
    }

    [Fact]
    public async Task StatusReportsUnknownWhenNoInstanceHeartbeatIsFresh()
    {
        var response = await new AdminQueryDispatcher(new Store(["*"]),
            new Source([Instance("li-01", 16)]), new Clock()).DispatchAsync(Request(AdminQueryKind.Status), default);
        Assert.Contains(response.Lines, l => l.Contains(
            "Spielverbindungen: unbekannt (kein frischer Coordinator-Heartbeat)."));
    }

    [Fact]
    public async Task AuditFailureDoesNotExposeData()
    {
        var response = await new AdminQueryDispatcher(new Store(["*"]) { FailAudit = true },
            new Source([Instance("li-01")]), new Clock()).DispatchAsync(Request(), default);
        Assert.Equal("unavailable", response.Status);
        Assert.DoesNotContain(response.Lines, l => l.Contains("li-01"));
    }

    [Fact]
    public async Task RevocationAlsoAppliesToRepeatedIdempotencyKeys()
    {
        var store = new Store(["*"]);
        var dispatcher = new AdminQueryDispatcher(store, new Source([Instance("li-01")]), new Clock());
        var request = Request();
        Assert.Equal("ok", (await dispatcher.DispatchAsync(request, default)).Status);
        store.Scopes = [];
        Assert.Equal("denied", (await dispatcher.DispatchAsync(request, default)).Status);
    }

    [Fact]
    public async Task ConflictingKeyDoesNotExposeData()
    {
        var response = await new AdminQueryDispatcher(new Store(["*"]) { Conflict = true },
            new Source([Instance("li-01")]), new Clock()).DispatchAsync(Request(), default);
        Assert.Equal("conflict", response.Status);
        Assert.DoesNotContain(response.Lines, l => l.Contains("li-01"));
    }

    [Fact]
    public void FutureHeartbeatCannotBeReady() => Assert.Equal("unknown",
        AdminQueryDispatcher.State(Instance("li-01") with { LastHeartbeatUtc = Now.AddMinutes(1) }, Now));

    [Fact]
    public async Task MissingStatusSourceIsAuditedWithoutFabricatedHealth()
    {
        var store = new Store(["*"]);
        var result = await new AdminQueryDispatcher(store, new MissingSource(), new Clock()).DispatchAsync(Request(), default);
        Assert.Equal("unavailable", result.Status);
        Assert.Equal("unavailable", store.Audited);
    }

    [Fact]
    public async Task CallerCancellationPropagatesWithoutAttemptingAnAuditWrite()
    {
        var store = new Store(["*"]);
        var dispatcher = new AdminQueryDispatcher(store, new Source([Instance("li-01")]), new Clock());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.DispatchAsync(Request(), cancellation.Token));

        Assert.Null(store.Audited);
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Store(string[] scopes) : IAdminQueryStore
    {
        public string[] Scopes = scopes;
        public bool FailAudit, Conflict;
        public string? Audited;
        public Task<string[]> GetScopesAsync(Guid actor, string permission, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Scopes);
        }
        public Task<bool> AuditAsync(AuthorizedAdminQueryRequest request, string result, CancellationToken ct)
        {
            if (FailAudit) throw new InvalidOperationException("Audit unavailable");
            Audited = result;
            return Task.FromResult(!Conflict);
        }
    }
    private sealed class Source(ClusterInstance[] instances) : IClusterStatusSource
    {
        public int Reads;
        public Task<ClusterInstance[]> ReadAsync(CancellationToken ct) { Reads++; return Task.FromResult(instances); }
    }
    private sealed class MissingSource : IClusterStatusSource
    {
        public Task<ClusterInstance[]> ReadAsync(CancellationToken ct) => throw new HttpRequestException("Offline");
    }
}
