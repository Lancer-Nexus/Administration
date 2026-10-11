using System.Net.Http.Headers;
using System.Net.Http.Json;
using LancerNexus.Protocol;

namespace LancerNexus.Administration.Api;

public interface IAdminQueryStore
{
    Task<string[]> GetScopesAsync(Guid accountId, string permission, CancellationToken ct);
    Task<bool> AuditAsync(AuthorizedAdminQueryRequest request, string result, CancellationToken ct);
}

public interface IClusterStatusSource
{
    Task<ClusterInstance[]> ReadAsync(CancellationToken ct);
}

public sealed record ClusterInstance(string InstanceId, string SystemId, string[]? SystemIds,
    bool IsReady, bool IsDraining, bool IsAlive, bool AgentIsAlive,
    int CurrentPlayers, int ReservedPlayers, int MaxPlayers, DateTimeOffset LastHeartbeatUtc);

public sealed class CoordinatorStatusSource(HttpClient http, IConfiguration configuration) : IClusterStatusSource
{
    public async Task<ClusterInstance[]> ReadAsync(CancellationToken ct)
    {
        if (!Uri.TryCreate(configuration["Administration:CoordinatorBaseUrl"], UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || configuration["Administration:CoordinatorApiKey"] is not { Length: >= 32 } key)
            throw new InvalidOperationException("Coordinator status is not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "/internal/v1/registry"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var registry = await response.Content.ReadFromJsonAsync<Registry>(ct)
            ?? throw new InvalidDataException("Coordinator returned no registry.");
        if (registry.Instances is null || registry.Instances.Length > 4096 ||
            registry.Instances.Any(i => !AdminQuery.ValidIdentifier(i.InstanceId) || i.MaxPlayers < 0 ||
                i.CurrentPlayers < 0 || i.ReservedPlayers < 0 || i.LastHeartbeatUtc == default ||
                !AdminQuery.ValidIdentifier(i.SystemId) ||
                (i.SystemIds is not null && (i.SystemIds.Length > 128 || i.SystemIds.Any(s => !AdminQuery.ValidIdentifier(s))))) ||
            registry.Instances.Select(i => i.InstanceId).Distinct(StringComparer.Ordinal).Count() != registry.Instances.Length)
            throw new InvalidDataException("Coordinator returned an invalid registry.");
        return registry.Instances;
    }
    private sealed record Registry(ClusterInstance[] Instances);
}

public sealed class AdminQueryDispatcher(IAdminQueryStore store, IClusterStatusSource source, TimeProvider clock)
{
    public async Task<AdminQueryResponse> DispatchAsync(AuthorizedAdminQueryRequest request, CancellationToken ct)
    {
        var query = request.Query;
        if (query is null || !query.IsValid() || request.AccountId == Guid.Empty ||
            request.Source is not { Length: > 0 and <= 128 })
            return Reply(query?.CorrelationId ?? Guid.Empty, "invalid", "Ungültiger Administrationsbefehl.");
        try
        {
            var permission = query.Kind switch
            { AdminQueryKind.Help => "admin.help", AdminQueryKind.Status => "status.read", _ => "instance.read" };
            var scopes = await store.GetScopesAsync(request.AccountId, permission, ct);
            AdminQueryResponse result;
            if (scopes.Length == 0 || (query.TargetInstanceId is not null &&
                !scopes.Contains("*") && !scopes.Contains(query.TargetInstanceId, StringComparer.Ordinal)))
                result = Reply(query.CorrelationId, "denied", "Keine Berechtigung für diesen Befehl oder diese Instanz.");
            else if (query.Kind == AdminQueryKind.Help)
                result = Reply(query.CorrelationId, "ok", "/admin help | status | instances | instance <ID>",
                    "Statusabfragen werden privat beantwortet. Rechte gelten gemäß MySQL-Rolle und Instanzbereich.");
            else
            {
                var instances = (await source.ReadAsync(ct)).Where(i => scopes.Contains("*") ||
                    scopes.Contains(i.InstanceId, StringComparer.Ordinal)).OrderBy(i => i.InstanceId).ToArray();
                var now = clock.GetUtcNow();
                if (query.Kind == AdminQueryKind.Status)
                {
                    var freshInstances = instances.Where(i => HasFreshHeartbeat(i, now)).ToArray();
                    var connectionLine = freshInstances.Length == 0
                        ? "Spielverbindungen: unbekannt (kein frischer Coordinator-Heartbeat)."
                        : $"Aktive Spielverbindungen laut frischen Coordinator-Heartbeats: " +
                          $"{freshInstances.Sum(i => (long)i.CurrentPlayers)}; " +
                          $"Datenfrische: {freshInstances.Length}/{instances.Length} Instanzen; " +
                          "nicht nach Konten oder Charakteren dedupliziert.";
                    result = Reply(query.CorrelationId, "ok", $"Clusterstatus ({now:O}, zugewiesener Bereich):",
                        $"Instanzen: {instances.Length}, bereit: {instances.Count(i => State(i, now) == "ready")}, " +
                        $"Kapazität: {instances.Where(i => State(i, now) is "ready" or "full").Sum(i => (long)i.MaxPlayers)}",
                        connectionLine,
                        "MySQL: verfügbar; Coordinator: erreichbar.");
                }
                else
                {
                    if (query.Kind == AdminQueryKind.Instance)
                        instances = instances.Where(i => i.InstanceId == query.TargetInstanceId).ToArray();
                    result = instances.Length == 0
                        ? Reply(query.CorrelationId, "not_found", "Keine passende registrierte Instanz im berechtigten Bereich.")
                        : new AdminQueryResponse
                        {
                            CorrelationId = query.CorrelationId,
                            Status = "ok",
                            Lines = instances.Take(64).Select(i => Format(i, now))
                                .Append(instances.Length > 64 ? "Weitere Instanzen: /admin instance <ID>." : $"Stand: {now:O}")
                                .ToArray()
                        };
                }
            }
            // Never expose status before the durable audit succeeds. Replays get fresh permissions and data.
            if (!await store.AuditAsync(request, result.Status, ct))
                return Reply(query.CorrelationId, "conflict", "Idempotenzschlüssel wurde für einen anderen Befehl verwendet.");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is MySqlConnector.MySqlException or HttpRequestException or
            InvalidOperationException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException)
        {
            try { await store.AuditAsync(request, "unavailable", ct); }
            catch (Exception e) when (e is MySqlConnector.MySqlException or InvalidOperationException or OperationCanceledException) { }
            return Reply(query.CorrelationId, "unavailable", "Administration/Clusterstatus derzeit nicht verfügbar (Datenquelle oder Audit).");
        }
    }

    public static string State(ClusterInstance i, DateTimeOffset now) =>
        i.LastHeartbeatUtc > now.AddSeconds(5) ? "unknown" :
        !HasFreshHeartbeat(i, now) ? "stale" :
        i.IsDraining ? "draining" : !i.IsReady ? "unavailable" :
        (long)i.CurrentPlayers + i.ReservedPlayers >= i.MaxPlayers ? "full" : "ready";

    private static bool HasFreshHeartbeat(ClusterInstance i, DateTimeOffset now) =>
        i.IsAlive && i.AgentIsAlive && i.LastHeartbeatUtc <= now.AddSeconds(5) &&
        now - i.LastHeartbeatUtc <= TimeSpan.FromSeconds(15);

    private static string Format(ClusterInstance i, DateTimeOffset now) =>
        $"{i.InstanceId}: {State(i, now)}; Verbindungen {i.CurrentPlayers}/{i.MaxPlayers}, reserviert {i.ReservedPlayers}; " +
        $"Systeme {string.Join(",", (i.SystemIds is { Length: > 0 } ? i.SystemIds : [i.SystemId]).Take(128))}; " +
        $"Heartbeat {i.LastHeartbeatUtc:O}";

    private static AdminQueryResponse Reply(Guid id, string status, params string[] lines) =>
        new() { CorrelationId = id, Status = status, Lines = lines };
}
