using MySqlConnector;
using LancerNexus.Cluster;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LancerNexus.Administration.Api;

public enum PermissionMutationKind { UpsertGroup, DeleteGroup, AssignGroup, UnassignGroup, SetUserNode, RemoveUserNode, PromoteGroup, DemoteGroup }

/// <summary>Closed administrative mutation model. Empty fields are not interpreted as SQL or shell input.</summary>
public sealed record PermissionMutation(
    PermissionMutationKind Kind,
    string? GroupName = null,
    string? ParentGroup = null,
    string[]? Parents = null,
    int Rank = 0,
    string? Ladder = null,
    string? Prefix = null,
    string? Suffix = null,
    Guid? AccountId = null,
    int Position = 0,
    string? Pattern = null,
    PermissionPatternKind PatternKind = PermissionPatternKind.Wildcard,
    bool Allowed = true,
    string? SystemId = null,
    string? InstanceId = null,
    DateTimeOffset? ExpiresUtc = null,
    PermissionNode[]? Nodes = null);

public sealed record AuthorizedPermissionMutation(Guid ActorAccountId, string Source, Guid CorrelationId,
    string IdempotencyKey, PermissionMutation Mutation);
public sealed record PermissionAuthorizationCheck(Guid AccountId, string Permission,
    string? InstanceId = null, string? SystemId = null);
public sealed record PermissionMutationResult(string Status, long? Revision = null);

public sealed class PermissionManagementStore(IConfiguration configuration, TimeProvider clock)
{
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Administration")
        ?? throw new InvalidOperationException("Administration SQL is not configured."));

    public async Task<PermissionSnapshot> LoadSnapshotAsync(CancellationToken ct)
    {
        await using var connection = Connection();
        await connection.OpenAsync(ct);
        return await ReadSnapshotAsync(connection, null, ct);
    }

    public async Task<bool> HasPermissionAsync(Guid accountId, string permission, string? instanceId,
        string? systemId, CancellationToken ct)
    {
        var snapshot = await LoadSnapshotAsync(ct);
        return new PermissionService(snapshot).HasPermission(accountId, permission,
            new PermissionContext(instanceId, systemId));
    }

    public async Task<bool> AcknowledgeAsync(string instanceId, long revision, CancellationToken ct)
    {
        if (instanceId is not { Length: > 0 and <= 96 } || revision < 0) return false;
        await using var connection = Connection();
        await connection.OpenAsync(ct);
        await using (var current = connection.CreateCommand())
        {
            current.CommandText = "SELECT revision FROM permission_state WHERE singleton_id=1";
            if (Convert.ToInt64(await current.ExecuteScalarAsync(ct)) != revision) return false;
        }
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO permission_instance_ack(instance_id,applied_revision,acknowledged_at_utc) SELECT @instance,@revision,UTC_TIMESTAMP(6) FROM permission_state WHERE singleton_id=1 AND revision=@revision ON DUPLICATE KEY UPDATE applied_revision=GREATEST(applied_revision,VALUES(applied_revision)),acknowledged_at_utc=IF(VALUES(applied_revision)>=applied_revision,VALUES(acknowledged_at_utc),acknowledged_at_utc)";
        command.Parameters.AddWithValue("@instance", instanceId);
        command.Parameters.AddWithValue("@revision", revision);
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = "SELECT applied_revision FROM permission_instance_ack WHERE instance_id=@instance";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) >= revision;
    }

    public async Task<(Guid EventId, long Revision, DateTimeOffset CreatedUtc)[]> ReadPendingEventsAsync(CancellationToken ct)
    {
        await using var connection = Connection(); await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_id,revision,created_at_utc FROM permission_outbox WHERE published_at_utc IS NULL ORDER BY revision LIMIT 32";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<(Guid, long, DateTimeOffset)>();
        while (await reader.ReadAsync(ct))
        {
            if (!Guid.TryParse(ReadDatabaseText(reader, 0), out var eventId))
                throw new InvalidDataException("Permission outbox event ID is not a GUID.");
            rows.Add((eventId, reader.GetInt64(1),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc))));
        }
        return rows.ToArray();
    }

    public async Task MarkPublishedAsync(Guid eventId, CancellationToken ct)
    {
        await using var connection = Connection(); await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE permission_outbox SET published_at_utc=UTC_TIMESTAMP(6) WHERE event_id=@event";
        command.Parameters.AddWithValue("@event", eventId.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> AreAcknowledgedAsync(long revision, IReadOnlyCollection<string> activeInstances, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activeInstances);
        if (revision < 0) return false;
        if (activeInstances.Count == 0) return true;
        await using var connection = Connection(); await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var names = new List<string>(activeInstances.Count);
        var index = 0;
        foreach (var instance in activeInstances.Distinct(StringComparer.Ordinal))
        {
            var name = "@instance" + index++;
            names.Add(name); command.Parameters.AddWithValue(name, instance);
        }
        command.Parameters.AddWithValue("@revision", revision);
        command.CommandText = $"SELECT COUNT(DISTINCT instance_id) FROM permission_instance_ack WHERE applied_revision>=@revision AND instance_id IN ({string.Join(',', names)})";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == names.Count;
    }

    public async Task<PermissionMutationResult> MutateAsync(AuthorizedPermissionMutation request, CancellationToken ct)
    {
        if (!Valid(request)) return new("invalid");
        await using var connection = Connection();
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request.Mutation))));
        try
        {
            // Serialize permission mutations and evaluate the actor against the same SQL revision
            // that this transaction will edit, so a concurrent revocation cannot race this write.
            await using (var state = connection.CreateCommand())
            {
                state.Transaction = tx;
                state.CommandText = "SELECT revision FROM permission_state WHERE singleton_id=1 FOR UPDATE";
                if (await state.ExecuteScalarAsync(ct) is null) throw new InvalidOperationException("Permission revision state is missing.");
            }
            var currentSnapshot = await ReadSnapshotAsync(connection, tx, ct);
            if (!new PermissionService(currentSnapshot).HasPermission(request.ActorAccountId, "permissions.manage",
                    new PermissionContext(request.Mutation.InstanceId, request.Mutation.SystemId), clock.GetUtcNow()))
            {
                await InsertMutationAuditAsync(connection, tx, request, currentSnapshot.Revision,
                    "denied", "permissions.manage_missing", "denied", ct);
                await tx.CommitAsync(ct);
                return new("denied");
            }

            await using (var existing = connection.CreateCommand())
            {
                existing.Transaction = tx;
                existing.CommandText = "SELECT fingerprint,result,revision FROM permission_mutation_keys WHERE actor_account_id=@actor AND idempotency_key=@key FOR UPDATE";
                existing.Parameters.AddWithValue("@actor", request.ActorAccountId.ToString());
                existing.Parameters.AddWithValue("@key", request.IdempotencyKey);
                await using var reader = await existing.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var same = string.Equals(reader.GetString(0), fingerprint, StringComparison.Ordinal);
                    var old = new PermissionMutationResult(reader.GetString(1), reader.GetInt64(2));
                    await reader.DisposeAsync();
                    await tx.CommitAsync(ct);
                    return same ? old : new("conflict");
                }
            }
            await ApplyMutationAsync(connection, tx, request.Mutation, ct);
            // Validate the complete candidate graph and every matcher before committing any change.
            try { _ = await ReadSnapshotAsync(connection, tx, ct); }
            catch (ArgumentException) { throw new PermissionMutationConflictException(); }
            await using (var bump = connection.CreateCommand())
            {
                bump.Transaction = tx;
                bump.CommandText = "UPDATE permission_state SET revision = revision + 1 WHERE singleton_id = 1";
                await bump.ExecuteNonQueryAsync(ct);
                bump.CommandText = "SELECT revision FROM permission_state WHERE singleton_id = 1";
                var revision = Convert.ToInt64(await bump.ExecuteScalarAsync(ct));
                var eventId = Guid.NewGuid();
                await using var outbox = connection.CreateCommand();
                outbox.Transaction = tx;
                outbox.CommandText = "INSERT INTO permission_outbox(event_id, revision, created_at_utc) VALUES (@id,@revision,@created)";
                outbox.Parameters.AddWithValue("@id", eventId.ToString());
                outbox.Parameters.AddWithValue("@revision", revision);
                outbox.Parameters.AddWithValue("@created", clock.GetUtcNow().UtcDateTime);
                await outbox.ExecuteNonQueryAsync(ct);
                await using var key = connection.CreateCommand();
                key.Transaction = tx;
                key.CommandText = "INSERT INTO permission_mutation_keys(actor_account_id,idempotency_key,fingerprint,result,revision) VALUES (@actor,@key,@fingerprint,'sync_pending',@revision)";
                key.Parameters.AddWithValue("@actor", request.ActorAccountId.ToString()); key.Parameters.AddWithValue("@key", request.IdempotencyKey);
                key.Parameters.AddWithValue("@fingerprint", fingerprint); key.Parameters.AddWithValue("@revision", revision);
                await key.ExecuteNonQueryAsync(ct);
                await InsertMutationAuditAsync(connection, tx, request, revision,
                    "allowed", "authorized", "sync_pending", ct);
                await tx.CommitAsync(ct);
                return new("sync_pending", revision);
            }
        }
        catch (MySqlException e) when (e.Number == 1062)
        {
            await tx.RollbackAsync(ct);
            return new("conflict");
        }
        catch (PermissionMutationConflictException)
        {
            await tx.RollbackAsync(ct);
            return new("conflict");
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task CheckReadyAsync(CancellationToken ct)
    {
        _ = await LoadSnapshotAsync(ct);
        await using var connection = Connection();
        await connection.OpenAsync(ct);
        await using (var auditSchema = connection.CreateCommand())
        {
            auditSchema.CommandText = "SELECT decision, reason, result FROM permission_audit LIMIT 0";
            await using var reader = await auditSchema.ExecuteReaderAsync(ct);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision FROM permission_state WHERE singleton_id = 1";
        if (await command.ExecuteScalarAsync(ct) is null) throw new InvalidOperationException("Permission revision state is missing.");
    }

    internal static async Task<PermissionSnapshot> ReadSnapshotAsync(MySqlConnection connection,
        MySqlTransaction? tx, CancellationToken ct)
    {
        var groups = new Dictionary<string, GroupBuilder>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = "SELECT group_name, rank_value, ladder, group_prefix, group_suffix FROM permission_groups ORDER BY group_name";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) groups.Add(reader.GetString(0), new(reader.GetString(0), reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = "SELECT group_name,parent_name FROM permission_group_inheritance ORDER BY group_name,position";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) groups[reader.GetString(0)].Parents.Add(reader.GetString(1));
        }
        await ReadNodesAsync(connection, tx, "permission_group_nodes", (name, node) => groups[name].Nodes.Add(node), ct);
        var memberships = new Dictionary<Guid, List<PermissionGroupMembership>>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = "SELECT account_id,group_name,instance_id,system_id,expires_utc FROM permission_user_groups ORDER BY account_id,position";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = Guid.Parse(ReadDatabaseText(reader, 0));
                if (!memberships.TryGetValue(id, out var list)) memberships[id] = list = [];
                list.Add(new(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc))));
            }
        }
        var userNodes = new Dictionary<Guid, List<PermissionNode>>();
        await ReadNodesAsync(connection, tx, "permission_user_nodes", (account, node) =>
        {
            var id = Guid.Parse(account);
            if (!userNodes.TryGetValue(id, out var list)) userNodes[id] = list = [];
            list.Add(node);
        }, ct);
        var ids = memberships.Keys.Concat(userNodes.Keys).Distinct();
        var users = ids.Select(id => new PermissionUser(id, memberships.GetValueOrDefault(id, [])?.ToArray() ?? [],
            userNodes.GetValueOrDefault(id, [])?.ToArray() ?? [])).ToArray();
        long revision;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = "SELECT revision FROM permission_state WHERE singleton_id = 1";
            revision = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        }
        return new PermissionSnapshot(revision, users, groups.Values.Select(g => new PermissionGroup(g.Name,
            g.Parents.ToArray(), g.Rank, g.Ladder, g.Prefix, g.Suffix, g.Nodes.ToArray())));
    }

    private static async Task ReadNodesAsync(MySqlConnection connection, MySqlTransaction? tx, string table,
        Action<string, PermissionNode> add, CancellationToken ct)
    {
        var owner = table == "permission_group_nodes" ? "group_name" : "account_id";
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT {owner},permission_pattern,pattern_kind,allowed,system_id,instance_id,expires_utc FROM {table} ORDER BY {owner},position";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) add(ReadDatabaseText(reader, 0), new(reader.GetString(1), reader.GetBoolean(3),
            (PermissionPatternKind)reader.GetByte(2), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(6) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc))));
    }

    private static string ReadDatabaseText(MySqlDataReader reader, int ordinal) => reader.GetValue(ordinal) switch
    {
        string text => text,
        Guid guid => guid.ToString("D"),
        _ => throw new InvalidDataException($"Unexpected value type in Administration SQL column {ordinal}.")
    };

    private static async Task ApplyMutationAsync(MySqlConnection connection, MySqlTransaction tx,
        PermissionMutation mutation, CancellationToken ct)
    {
        async Task Execute(string sql, Action<MySqlCommand>? parameters = null)
        {
            await using var command = connection.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
            parameters?.Invoke(command); await command.ExecuteNonQueryAsync(ct);
        }
        switch (mutation.Kind)
        {
            case PermissionMutationKind.UpsertGroup:
                await Execute("INSERT INTO permission_groups(group_name,rank_value,ladder,group_prefix,group_suffix) VALUES (@name,@rank,@ladder,@prefix,@suffix) ON DUPLICATE KEY UPDATE rank_value=VALUES(rank_value),ladder=VALUES(ladder),group_prefix=VALUES(group_prefix),group_suffix=VALUES(group_suffix),updated_at_utc=UTC_TIMESTAMP(6)", c => GroupParameters(c, mutation));
                await Execute("DELETE FROM permission_group_inheritance WHERE group_name=@name", c => c.Parameters.AddWithValue("@name", mutation.GroupName!));
                await Execute("DELETE FROM permission_group_nodes WHERE group_name=@name", c => c.Parameters.AddWithValue("@name", mutation.GroupName!));
                for (var i = 0; i < (mutation.Parents ?? []).Length; i++)
                {
                    var parent = mutation.Parents![i];
                    await Execute("INSERT INTO permission_group_inheritance(group_name,parent_name,position) VALUES (@name,@parent,@position)", c =>
                    { c.Parameters.AddWithValue("@name", mutation.GroupName!); c.Parameters.AddWithValue("@parent", parent); c.Parameters.AddWithValue("@position", i); });
                }
                foreach (var (node, index) in (mutation.Nodes ?? []).Select((n, i) => (n, i)))
                    await InsertNode(connection, tx, "permission_group_nodes", "group_name", mutation.GroupName!, node, index, ct);
                break;
            case PermissionMutationKind.DeleteGroup:
                if (string.Equals(mutation.GroupName, "default", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The default group cannot be deleted.");
                await Execute("DELETE FROM permission_groups WHERE group_name=@name", c => c.Parameters.AddWithValue("@name", mutation.GroupName!));
                break;
            case PermissionMutationKind.AssignGroup:
                await Execute("DELETE FROM permission_user_groups WHERE account_id=@account AND group_name=@group AND instance_id <=> @instance AND system_id <=> @system", c => MembershipParameters(c, mutation));
                var nextPosition = await ScalarLong(connection, tx, "SELECT COALESCE(MAX(position)+1,0) FROM permission_user_groups WHERE account_id=@account", "@account", mutation.AccountId!.Value.ToString(), ct);
                await Execute("INSERT INTO permission_user_groups(account_id,group_name,instance_id,system_id,expires_utc,position) VALUES (@account,@group,@instance,@system,@expires,@position)", c =>
                { MembershipParameters(c, mutation); c.Parameters.AddWithValue("@expires", (object?)mutation.ExpiresUtc?.UtcDateTime ?? DBNull.Value); c.Parameters.AddWithValue("@position", nextPosition); });
                break;
            case PermissionMutationKind.UnassignGroup:
                await Execute("DELETE FROM permission_user_groups WHERE account_id=@account AND group_name=@group AND instance_id <=> @instance AND system_id <=> @system", c => MembershipParameters(c, mutation));
                break;
            case PermissionMutationKind.SetUserNode:
                await Execute("DELETE FROM permission_user_nodes WHERE account_id=@account AND permission_pattern=@pattern AND instance_id <=> @instance AND system_id <=> @system", c => NodeParameters(c, mutation));
                var nodePosition = await ScalarLong(connection, tx, "SELECT COALESCE(MAX(position)+1,0) FROM permission_user_nodes WHERE account_id=@account", "@account", mutation.AccountId!.Value.ToString(), ct);
                await InsertNode(connection, tx, "permission_user_nodes", "account_id", mutation.AccountId!.Value.ToString(),
                    new(mutation.Pattern!, mutation.Allowed, mutation.PatternKind, mutation.InstanceId, mutation.SystemId, mutation.ExpiresUtc), checked((int)nodePosition), ct);
                break;
            case PermissionMutationKind.RemoveUserNode:
                await Execute("DELETE FROM permission_user_nodes WHERE account_id=@account AND permission_pattern=@pattern AND instance_id <=> @instance AND system_id <=> @system", c => NodeParameters(c, mutation));
                break;
            case PermissionMutationKind.PromoteGroup:
            case PermissionMutationKind.DemoteGroup:
                await ChangeLadderGroupAsync(connection, tx, mutation, ct);
                break;
            default: throw new InvalidOperationException("Unknown permission mutation.");
        }
    }

    private static async Task ChangeLadderGroupAsync(MySqlConnection connection, MySqlTransaction tx,
        PermissionMutation mutation, CancellationToken ct)
    {
        await using var current = connection.CreateCommand();
        current.Transaction = tx;
        current.CommandText = "SELECT ug.membership_id,ug.group_name,g.rank_value FROM permission_user_groups ug JOIN permission_groups g ON g.group_name=ug.group_name WHERE ug.account_id=@account AND g.ladder=@ladder AND ug.instance_id <=> @instance AND ug.system_id <=> @system ORDER BY ug.position LIMIT 1 FOR UPDATE";
        current.Parameters.AddWithValue("@account", mutation.AccountId!.Value.ToString());
        current.Parameters.AddWithValue("@ladder", mutation.Ladder!);
        current.Parameters.AddWithValue("@instance", (object?)mutation.InstanceId ?? DBNull.Value);
        current.Parameters.AddWithValue("@system", (object?)mutation.SystemId ?? DBNull.Value);
        await using var reader = await current.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new PermissionMutationConflictException();
        var membershipId = reader.GetInt64(0); var oldGroup = reader.GetString(1); var rank = reader.GetInt32(2);
        await reader.DisposeAsync();
        await using var target = connection.CreateCommand();
        target.Transaction = tx;
        var higher = mutation.Kind == PermissionMutationKind.PromoteGroup;
        target.CommandText = "SELECT group_name FROM permission_groups WHERE ladder=@ladder AND rank_value " + (higher ? ">" : "<") + " @rank ORDER BY rank_value " + (higher ? "ASC" : "DESC") + ",group_name LIMIT 1";
        target.Parameters.AddWithValue("@ladder", mutation.Ladder!); target.Parameters.AddWithValue("@rank", rank);
        if (await target.ExecuteScalarAsync(ct) is not string nextGroup || string.Equals(nextGroup, oldGroup, StringComparison.OrdinalIgnoreCase))
            throw new PermissionMutationConflictException();
        await using var change = connection.CreateCommand();
        change.Transaction = tx;
        change.CommandText = "UPDATE permission_user_groups SET group_name=@next WHERE membership_id=@id";
        change.Parameters.AddWithValue("@next", nextGroup); change.Parameters.AddWithValue("@id", membershipId);
        try { await change.ExecuteNonQueryAsync(ct); }
        catch (MySqlException e) when (e.Number == 1062)
        {
            await using var remove = connection.CreateCommand(); remove.Transaction = tx;
            remove.CommandText = "DELETE FROM permission_user_groups WHERE membership_id=@id";
            remove.Parameters.AddWithValue("@id", membershipId); await remove.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task InsertNode(MySqlConnection connection, MySqlTransaction tx, string table,
        string ownerColumn, string owner, PermissionNode node, int position, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = $"INSERT INTO {table}({ownerColumn},position,permission_pattern,pattern_kind,allowed,system_id,instance_id,expires_utc) VALUES (@owner,@position,@pattern,@kind,@allowed,@system,@instance,@expires)";
        command.Parameters.AddWithValue("@owner", owner); command.Parameters.AddWithValue("@position", position);
        command.Parameters.AddWithValue("@pattern", node.Pattern); command.Parameters.AddWithValue("@kind", (byte)node.Kind);
        command.Parameters.AddWithValue("@allowed", node.Allowed); command.Parameters.AddWithValue("@system", (object?)node.SystemId ?? DBNull.Value);
        command.Parameters.AddWithValue("@instance", (object?)node.InstanceId ?? DBNull.Value);
        command.Parameters.AddWithValue("@expires", node.ExpiresUtc?.UtcDateTime ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ScalarLong(MySqlConnection connection, MySqlTransaction tx,
        string sql, string name, string value, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private static void GroupParameters(MySqlCommand c, PermissionMutation m)
    {
        c.Parameters.AddWithValue("@name", m.GroupName!); c.Parameters.AddWithValue("@rank", m.Rank);
        c.Parameters.AddWithValue("@ladder", (object?)m.Ladder ?? DBNull.Value); c.Parameters.AddWithValue("@prefix", (object?)m.Prefix ?? DBNull.Value);
        c.Parameters.AddWithValue("@suffix", (object?)m.Suffix ?? DBNull.Value);
    }
    private static void MembershipParameters(MySqlCommand c, PermissionMutation m)
    {
        c.Parameters.AddWithValue("@account", m.AccountId!.Value.ToString()); c.Parameters.AddWithValue("@group", m.GroupName!);
        c.Parameters.AddWithValue("@instance", (object?)m.InstanceId ?? DBNull.Value); c.Parameters.AddWithValue("@system", (object?)m.SystemId ?? DBNull.Value);
    }
    private static void NodeParameters(MySqlCommand c, PermissionMutation m)
    {
        c.Parameters.AddWithValue("@account", m.AccountId!.Value.ToString()); c.Parameters.AddWithValue("@pattern", m.Pattern!);
        c.Parameters.AddWithValue("@instance", (object?)m.InstanceId ?? DBNull.Value); c.Parameters.AddWithValue("@system", (object?)m.SystemId ?? DBNull.Value);
    }

    private async Task InsertMutationAuditAsync(MySqlConnection connection, MySqlTransaction tx,
        AuthorizedPermissionMutation request, long revision, string decision, string reason, string result,
        CancellationToken ct)
    {
        await using var audit = connection.CreateCommand();
        audit.Transaction = tx;
        audit.CommandText = "INSERT INTO permission_audit(created_at_utc, actor_account_id, correlation_id, source, idempotency_key, mutation_kind, target, revision, decision, reason, result) VALUES (@at,@actor,@correlation,@source,@key,@kind,@target,@revision,@decision,@reason,@result)";
        audit.Parameters.AddWithValue("@at", clock.GetUtcNow().UtcDateTime);
        audit.Parameters.AddWithValue("@actor", request.ActorAccountId.ToString());
        audit.Parameters.AddWithValue("@correlation", request.CorrelationId.ToString());
        audit.Parameters.AddWithValue("@source", request.Source);
        audit.Parameters.AddWithValue("@key", request.IdempotencyKey);
        audit.Parameters.AddWithValue("@kind", request.Mutation.Kind.ToString());
        audit.Parameters.AddWithValue("@target", request.Mutation.GroupName ?? request.Mutation.AccountId?.ToString());
        audit.Parameters.AddWithValue("@revision", revision);
        audit.Parameters.AddWithValue("@decision", decision);
        audit.Parameters.AddWithValue("@reason", reason);
        audit.Parameters.AddWithValue("@result", result);
        await audit.ExecuteNonQueryAsync(ct);
    }

    private static bool Valid(AuthorizedPermissionMutation r)
    {
        if (r is null || r.Mutation is null) return false;
        var m = r.Mutation;
        if (r.ActorAccountId == Guid.Empty || r.CorrelationId == Guid.Empty || r.IdempotencyKey is not { Length: > 0 and <= 96 } ||
            r.Source is not { Length: > 0 and <= 128 } || !Enum.IsDefined(m.Kind)) return false;
        var groupRequired = m.Kind is PermissionMutationKind.UpsertGroup or PermissionMutationKind.DeleteGroup or PermissionMutationKind.AssignGroup or PermissionMutationKind.UnassignGroup;
        if (groupRequired && (m.GroupName is not { Length: > 0 and <= 64 } || m.GroupName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')))) return false;
        if (m.Parents is { Length: > 64 } || m.Nodes is { Length: > 512 } || m.Rank is < -100000 or > 100000 ||
            m.Prefix?.Length > 128 || m.Suffix?.Length > 128 || m.Ladder?.Length > 64 ||
            (m.AccountId.HasValue && m.AccountId == Guid.Empty) ||
            (m.Kind is PermissionMutationKind.AssignGroup or PermissionMutationKind.UnassignGroup or PermissionMutationKind.SetUserNode or PermissionMutationKind.RemoveUserNode or PermissionMutationKind.PromoteGroup or PermissionMutationKind.DemoteGroup && !m.AccountId.HasValue)) return false;
        if (m.Kind is PermissionMutationKind.PromoteGroup or PermissionMutationKind.DemoteGroup && m.Ladder is not { Length: > 0 and <= 64 }) return false;
        if (m.Kind is PermissionMutationKind.SetUserNode or PermissionMutationKind.RemoveUserNode && m.Pattern is not { Length: > 0 and <= 256 }) return false;
        return true;
    }

    private sealed class GroupBuilder(string name, int rank, string? ladder, string? prefix, string? suffix)
    {
        public string Name { get; } = name; public int Rank { get; } = rank; public string? Ladder { get; } = ladder;
        public string? Prefix { get; } = prefix; public string? Suffix { get; } = suffix;
        public List<string> Parents { get; } = []; public List<PermissionNode> Nodes { get; } = [];
    }

    private sealed class PermissionMutationConflictException : Exception { }
}
