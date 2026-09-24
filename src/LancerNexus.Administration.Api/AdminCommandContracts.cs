namespace LancerNexus.Administration.Api;

/// <summary>Transport envelope shared by REST and future in-game adapters.</summary>
public sealed record AdminCommandEnvelope(
    Guid CorrelationId,
    string IdempotencyKey,
    AdminCommand Command);

/// <summary>Closed command set; arbitrary SQL and shell text are intentionally not representable.</summary>
public abstract record AdminCommand;

public sealed record SendPlayerMessageCommand(string CharacterId, string Message) : AdminCommand;

public sealed record KickPlayerCommand(string CharacterId, string Reason) : AdminCommand;

public sealed record AdminCommandResult(
    Guid CorrelationId,
    string Status,
    string? OperationId = null);

public interface IAdminCommandDispatcher
{
    ValueTask<AdminCommandResult> DispatchAsync(
        string actorId,
        string source,
        AdminCommandEnvelope envelope,
        CancellationToken cancellationToken);
}
