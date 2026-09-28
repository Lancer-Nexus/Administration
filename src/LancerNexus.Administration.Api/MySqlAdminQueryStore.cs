using System.Security.Cryptography;
using System.Text;
using LancerNexus.Protocol;
using LancerNexus.Cluster;
using MySqlConnector;

namespace LancerNexus.Administration.Api;

public sealed class MySqlAdminQueryStore(IConfiguration configuration, PermissionManagementStore permissions) : IAdminQueryStore
{
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Administration")
        ?? throw new InvalidOperationException("Administration SQL is not configured."));

    public async Task CheckReadyAsync(CancellationToken ct)
    {
        await GetScopesAsync(Guid.Empty, "status.read", ct);
        await using var connection = Connection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT audit_id FROM admin_audit LIMIT 0; SELECT fingerprint FROM admin_query_keys LIMIT 0";
        await using var reader = await command.ExecuteReaderAsync(ct);
    }

    public async Task<string[]> GetScopesAsync(Guid accountId, string permission, CancellationToken ct)
    {
        var snapshot = await permissions.LoadSnapshotAsync(ct);
        var service = new LancerNexus.Cluster.PermissionService(snapshot);
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        if (service.HasPermission(accountId, permission)) scopes.Add("*");
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (snapshot.Users.TryGetValue(accountId, out var user))
        {
            foreach (var membership in user.Groups)
                if (membership.InstanceId is not null) candidates.Add(membership.InstanceId);
            foreach (var node in user.Nodes)
                if (node.InstanceId is not null) candidates.Add(node.InstanceId);
            var pending = new Stack<string>(user.Groups.Select(g => g.GroupName));
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (pending.TryPop(out var name) && visited.Add(name) && snapshot.Groups.TryGetValue(name, out var group))
            {
                foreach (var node in group.Nodes) if (node.InstanceId is not null) candidates.Add(node.InstanceId);
                foreach (var parent in group.Inherits) pending.Push(parent);
            }
        }
        foreach (var instance in candidates)
            if (service.HasPermission(accountId, permission, new PermissionContext(instance))) scopes.Add(instance);
        return scopes.ToArray();
    }

    public async Task<bool> AuditAsync(AuthorizedAdminQueryRequest request, string result, CancellationToken ct)
    {
        await using var connection = Connection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{(int)request.Query.Kind}:{request.Query.TargetInstanceId}")));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@actor", request.AccountId.ToString());
        command.Parameters.AddWithValue("@key", request.Query.IdempotencyKey);
        command.Parameters.AddWithValue("@fingerprint", fingerprint);
        command.CommandText = """
            INSERT INTO admin_query_keys (account_id, idempotency_key, fingerprint) VALUES (@actor, @key, @fingerprint)
            ON DUPLICATE KEY UPDATE fingerprint = fingerprint;
            """;
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = "SELECT fingerprint FROM admin_query_keys WHERE account_id = @actor AND idempotency_key = @key FOR UPDATE";
        if (!string.Equals(await command.ExecuteScalarAsync(ct) as string, fingerprint, StringComparison.Ordinal))
        {
            result = "conflict";
        }
        command.CommandText = """
            INSERT INTO admin_audit (created_at_utc, account_id, correlation_id, source, command_kind, target_instance_id, result)
            VALUES (UTC_TIMESTAMP(6), @actor, @correlation, @source, @kind, @target, @result)
            """;
        command.Parameters.AddWithValue("@correlation", request.Query.CorrelationId.ToString());
        command.Parameters.AddWithValue("@source", request.Source);
        command.Parameters.AddWithValue("@kind", request.Query.Kind.ToString());
        command.Parameters.AddWithValue("@target", (object?)request.Query.TargetInstanceId ?? DBNull.Value);
        command.Parameters.AddWithValue("@result", result);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return result != "conflict";
    }
}
