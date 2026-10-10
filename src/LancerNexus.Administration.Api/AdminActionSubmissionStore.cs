using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LancerNexus.Cluster;
using LancerNexus.Protocol;
using MySqlConnector;

namespace LancerNexus.Administration.Api;

/// <summary>
/// Request forwarded by Gateway after it revalidates the actor's session and resolves target
/// state through Coordinator. The shared Gateway service credential authenticates this attestation.
/// </summary>
public sealed record AuthorizedAdminActionSubmission(
    Guid GatewaySessionId,
    AdminActionRequestV1 Action,
    AdminActionTargetContext? TargetContext,
    bool StepUpVerified);

public sealed class AdminActionSubmissionStore(IConfiguration configuration, TimeProvider clock)
{
    private string ConnectionString => configuration.GetConnectionString("Administration")
        ?? throw new InvalidOperationException("Administration SQL is not configured.");

    public async Task<AdminActionResultV1> SubmitAsync(AuthorizedAdminActionSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(submission.Action);
        if (submission.GatewaySessionId == Guid.Empty || !submission.Action.IsValid())
            return Result(submission.Action, AdminActionStatus.Invalid, "invalid_action");

        var action = submission.Action;
        // Step-up is an authorization input, not a downstream execution credential.
        var payloadJson = JsonSerializer.Serialize(new { submission.Action, submission.TargetContext });
        var fingerprintPayload = JsonSerializer.Serialize(new { submission.Action, submission.TargetContext });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintPayload))).ToLowerInvariant();
        var resolved = ResolveTargetColumns(submission);

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await LockPermissionRevisionAsync(connection, transaction, cancellationToken);
            var snapshot = await PermissionManagementStore.ReadSnapshotAsync(connection, transaction, cancellationToken);
            var permissionService = new PermissionService(snapshot);
            if (!AdminActionAuthorizationPolicy.TryCreateRequirement(action, submission.TargetContext, out var requirement) ||
                requirement is null)
            {
                await WriteAuditAsync(connection, transaction, submission, null, resolved, "unavailable",
                    "target_context_unavailable", AdminActionStatus.Unavailable, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Result(action, AdminActionStatus.Unavailable, "target_context_unavailable", ResultTargetInstance(resolved));
            }

            if (!permissionService.IsSynchronized)
            {
                await WriteAuditAsync(connection, transaction, submission, requirement.Permission, resolved, "unavailable",
                    "permission_snapshot_unavailable", AdminActionStatus.Unavailable, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Result(action, AdminActionStatus.Unavailable, "permission_snapshot_unavailable", ResultTargetInstance(resolved));
            }

            if (requirement.RequiresStepUp && !submission.StepUpVerified)
            {
                await WriteAuditAsync(connection, transaction, submission, requirement.Permission, resolved, "denied",
                    "step_up_required", AdminActionStatus.Denied, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Result(action, AdminActionStatus.Denied, "step_up_required", ResultTargetInstance(resolved));
            }

            if (!AdminActionAuthorizationPolicy.IsAuthorized(action, submission.TargetContext,
                    permissionService, submission.StepUpVerified, clock.GetUtcNow()))
            {
                await WriteAuditAsync(connection, transaction, submission, requirement.Permission, resolved, "denied",
                    "permission_denied", AdminActionStatus.Denied, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Result(action, AdminActionStatus.Denied, "permission_denied", ResultTargetInstance(resolved));
            }

            var existing = await FindExistingAsync(connection, transaction, action, cancellationToken);
            if (existing is not null)
            {
                var matches = existing.CommandId == action.CommandId &&
                              existing.ActorAccountId == action.ActorAccountId &&
                              string.Equals(existing.IdempotencyKey, action.IdempotencyKey, StringComparison.Ordinal) &&
                              string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal);
                if (!matches)
                {
                    await WriteAuditAsync(connection, transaction, submission, requirement.Permission, resolved, "conflict",
                        "idempotency_conflict", AdminActionStatus.Conflict, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return Result(action, AdminActionStatus.Conflict, "idempotency_conflict", ResultTargetInstance(resolved));
                }

                await WriteAuditAsync(connection, transaction, submission, requirement.Permission, resolved, "allowed",
                    "idempotent_replay", AdminActionStatus.Pending, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Result(action, AdminActionStatus.Pending, null, ResultTargetInstance(resolved),
                    existing.OperationId, existing.Sequence);
            }

            var operationId = Guid.NewGuid();
            var sequence = await InsertCommandAsync(connection, transaction, submission, requirement.Permission,
                resolved, fingerprint, operationId, cancellationToken);
            await InsertOutboxAsync(connection, transaction, action.CommandId, payloadJson, cancellationToken);
            await WriteAuditAsync(connection, transaction, submission, requirement.Permission, resolved, "allowed",
                "authorized", AdminActionStatus.Pending, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result(action, AdminActionStatus.Pending, null, ResultTargetInstance(resolved), operationId, sequence);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task CheckReadyAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var table in new[] { "admin_action_commands", "admin_action_outbox", "admin_action_audit" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT 1 FROM `{table}` LIMIT 0";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        }
    }

    private static async Task LockPermissionRevisionAsync(MySqlConnection connection, MySqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM permission_state WHERE singleton_id=1 FOR UPDATE";
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
            throw new InvalidOperationException("Permission revision state is missing.");
    }

    private static async Task<ActionCommandRow?> FindExistingAsync(MySqlConnection connection,
        MySqlTransaction transaction, AdminActionRequestV1 action,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT command_id, actor_account_id, idempotency_key, payload_sha256, operation_id, queue_sequence
            FROM admin_action_commands
            WHERE (actor_account_id=@actor AND idempotency_key=@key) OR command_id=@command
            FOR UPDATE
            """;
        command.Parameters.AddWithValue("@actor", action.ActorAccountId.ToString("D"));
        command.Parameters.AddWithValue("@key", action.IdempotencyKey);
        command.Parameters.AddWithValue("@command", action.CommandId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var row = new ActionCommandRow(reader.GetGuid(0), reader.GetGuid(1),
            reader.GetString(2), reader.GetString(3), reader.GetGuid(4), reader.GetInt64(5));
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Admin action command identity indexes refer to multiple records.");
        return row;
    }

    private static async Task<long> InsertCommandAsync(MySqlConnection connection, MySqlTransaction transaction,
        AuthorizedAdminActionSubmission submission, string permission, ResolvedActionTarget target,
        string fingerprint, Guid operationId, CancellationToken cancellationToken)
    {
        var action = submission.Action;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO admin_action_commands
                (command_id,operation_id,actor_account_id,gateway_session_id,correlation_id,source,idempotency_key,
                 payload_sha256,action_kind,target_kind,permission_name,target_id,target_instance_id,target_system_id,
                 destination_instance_id,destination_system_id,status,created_at_utc)
            VALUES
                (@command,@operation,@actor,@session,@correlation,@source,@key,@fingerprint,@kind,@target_kind,@permission,
                 @target,@instance,@system,@destination_instance,@destination_system,'pending',@created)
            """;
        AddActionParameters(command, submission, permission, target);
        command.Parameters.AddWithValue("@command", action.CommandId.ToString("D"));
        command.Parameters.AddWithValue("@operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("@actor", action.ActorAccountId.ToString("D"));
        command.Parameters.AddWithValue("@session", submission.GatewaySessionId.ToString("D"));
        command.Parameters.AddWithValue("@correlation", action.CorrelationId.ToString("D"));
        command.Parameters.AddWithValue("@source", action.Source);
        command.Parameters.AddWithValue("@key", action.IdempotencyKey);
        command.Parameters.AddWithValue("@fingerprint", fingerprint);
        command.Parameters.AddWithValue("@kind", (byte)action.Kind);
        command.Parameters.AddWithValue("@target_kind", (byte)action.TargetKind);
        command.Parameters.AddWithValue("@created", DateTime.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return command.LastInsertedId;
    }

    private static async Task InsertOutboxAsync(MySqlConnection connection, MySqlTransaction transaction,
        Guid commandId, string payload, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO admin_action_outbox(command_id,payload_json,created_at_utc) VALUES (@id,@payload,UTC_TIMESTAMP(6))";
        command.Parameters.AddWithValue("@id", commandId.ToString("D"));
        command.Parameters.AddWithValue("@payload", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task WriteAuditAsync(MySqlConnection connection, MySqlTransaction transaction,
        AuthorizedAdminActionSubmission submission, string? permission, ResolvedActionTarget target,
        string decision, string reason, AdminActionStatus result, CancellationToken cancellationToken)
    {
        var action = submission.Action;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO admin_action_audit
                (created_at_utc,command_id,actor_account_id,gateway_session_id,correlation_id,source,idempotency_key,
                 action_kind,target_kind,permission_name,target_id,target_instance_id,target_system_id,
                 destination_instance_id,destination_system_id,decision,reason,result_status)
            VALUES
                (@created,@command,@actor,@session,@correlation,@source,@key,@kind,@target_kind,@permission,@target,
                 @instance,@system,@destination_instance,@destination_system,@decision,@reason,@status)
            """;
        AddActionParameters(command, submission, permission, target);
        command.Parameters.AddWithValue("@created", DateTime.UtcNow);
        command.Parameters.AddWithValue("@command", action.CommandId.ToString("D"));
        command.Parameters.AddWithValue("@actor", action.ActorAccountId.ToString("D"));
        command.Parameters.AddWithValue("@session", submission.GatewaySessionId.ToString("D"));
        command.Parameters.AddWithValue("@correlation", action.CorrelationId.ToString("D"));
        command.Parameters.AddWithValue("@source", action.Source);
        command.Parameters.AddWithValue("@key", action.IdempotencyKey);
        command.Parameters.AddWithValue("@kind", (byte)action.Kind);
        command.Parameters.AddWithValue("@target_kind", (byte)action.TargetKind);
        command.Parameters.AddWithValue("@decision", decision);
        command.Parameters.AddWithValue("@reason", reason);
        command.Parameters.AddWithValue("@status", (byte)result);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddActionParameters(MySqlCommand command, AuthorizedAdminActionSubmission submission,
        string? permission, ResolvedActionTarget target)
    {
        command.Parameters.AddWithValue("@permission", permission is null ? DBNull.Value : permission);
        command.Parameters.AddWithValue("@target", submission.Action.TargetId is null ? DBNull.Value : submission.Action.TargetId);
        command.Parameters.AddWithValue("@instance", target.InstanceId is null ? DBNull.Value : target.InstanceId);
        command.Parameters.AddWithValue("@system", target.SystemId is null ? DBNull.Value : target.SystemId);
        command.Parameters.AddWithValue("@destination_instance", target.DestinationInstanceId is null ? DBNull.Value : target.DestinationInstanceId);
        command.Parameters.AddWithValue("@destination_system", target.DestinationSystemId is null ? DBNull.Value : target.DestinationSystemId);
    }

    private static ResolvedActionTarget ResolveTargetColumns(AuthorizedAdminActionSubmission submission)
    {
        var action = submission.Action;
        var context = submission.TargetContext;
        return new(
            action.TargetKind == AdminActionTargetKind.Instance ? action.TargetId : context?.CurrentInstanceId,
            action.TargetKind == AdminActionTargetKind.System ? action.TargetId : context?.CurrentSystemId,
            action.TargetInstanceId ?? context?.DestinationInstanceId,
            action.TargetSystemId ?? context?.DestinationSystemId);
    }

    private static string? ResultTargetInstance(ResolvedActionTarget target) =>
        target.DestinationInstanceId ?? target.InstanceId;

    private static AdminActionResultV1 Result(AdminActionRequestV1 action, AdminActionStatus status,
        string? errorCode, string? targetInstanceId = null, Guid? operationId = null, long? sequence = null) => new()
        {
            CommandId = action.CommandId,
            CorrelationId = action.CorrelationId,
            Status = status,
            ErrorCode = errorCode,
            TargetInstanceId = targetInstanceId,
            OperationId = operationId,
            Sequence = sequence
        };

    private sealed record ActionCommandRow(Guid CommandId, Guid ActorAccountId, string IdempotencyKey,
        string Fingerprint, Guid OperationId, long Sequence);

    private sealed record ResolvedActionTarget(string? InstanceId, string? SystemId,
        string? DestinationInstanceId, string? DestinationSystemId);
}
