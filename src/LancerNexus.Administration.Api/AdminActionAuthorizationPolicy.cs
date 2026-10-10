using LancerNexus.Cluster;
using LancerNexus.Protocol;

namespace LancerNexus.Administration.Api;

/// <summary>Authoritative target context resolved from the current Coordinator registry.</summary>
public sealed record AdminActionTargetContext(
    string TargetId,
    string? CurrentInstanceId,
    string? CurrentSystemId,
    string? DestinationInstanceId = null,
    string? DestinationSystemId = null);

internal sealed record AdminActionPermissionRequirement(
    string Permission,
    IReadOnlyList<PermissionContext> Contexts,
    bool RequiresStepUp);

/// <summary>
/// Maps typed admin actions to narrow permission nodes and all contexts they affect.
/// Player context must be resolved by Coordinator; request fields alone are not trusted.
/// </summary>
internal static class AdminActionAuthorizationPolicy
{
    public static bool IsAuthorized(AdminActionRequestV1 request, AdminActionTargetContext? targetContext,
        IPermissionService permissions, bool stepUpVerified, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(permissions);
        if (!permissions.IsSynchronized ||
            !TryCreateRequirement(request, targetContext, out var requirement) || requirement is null ||
            requirement.Contexts.Count == 0 || requirement.RequiresStepUp && !stepUpVerified)
            return false;

        return requirement.Contexts.All(context => permissions.HasPermission(request.ActorAccountId,
            requirement.Permission, context, now));
    }

    public static bool TryCreateRequirement(AdminActionRequestV1 request,
        AdminActionTargetContext? targetContext, out AdminActionPermissionRequirement? requirement)
    {
        requirement = null;
        if (request is null || !request.IsValid()) return false;

        var contexts = new List<PermissionContext>(2);
        var requiresStepUp = false;
        string permission;

        switch (request.Kind)
        {
            case AdminActionKind.Message:
                permission = "admin.message";
                if (request.TargetKind == AdminActionTargetKind.All)
                    contexts.Add(new PermissionContext());
                else if (request.TargetKind == AdminActionTargetKind.System)
                    contexts.Add(new PermissionContext(SystemId: request.TargetId));
                else if (request.TargetKind == AdminActionTargetKind.Instance)
                    contexts.Add(new PermissionContext(InstanceId: request.TargetId));
                else if (!TryAddCurrentTargetContext(request, targetContext, contexts))
                    return false;
                break;

            case AdminActionKind.Kick:
                permission = "player.kick";
                if (!TryAddCurrentTargetContext(request, targetContext, contexts)) return false;
                break;

            case AdminActionKind.Ban:
                permission = "player.ban";
                if (request.TargetKind == AdminActionTargetKind.Player)
                {
                    if (!TryAddCurrentTargetContext(request, targetContext, contexts)) return false;
                }
                else
                {
                    contexts.Add(new PermissionContext());
                }
                requiresStepUp = request.ExpiresUtc is null;
                break;

            case AdminActionKind.Unban:
                permission = "player.unban";
                contexts.Add(new PermissionContext());
                break;

            case AdminActionKind.Drain:
                permission = "server.drain";
                contexts.Add(new PermissionContext(InstanceId: request.TargetId));
                requiresStepUp = true;
                break;

            case AdminActionKind.Move:
            case AdminActionKind.Transfer:
                permission = request.Kind == AdminActionKind.Move ? "transfer.move" : "transfer.start";
                if (!TryAddCurrentTargetContext(request, targetContext, contexts) ||
                    targetContext is null ||
                    !string.Equals(targetContext.DestinationInstanceId, request.TargetInstanceId, StringComparison.Ordinal) ||
                    !AdminQuery.ValidIdentifier(targetContext.DestinationSystemId))
                    return false;
                contexts.Add(new PermissionContext(targetContext.DestinationInstanceId,
                    targetContext.DestinationSystemId));
                break;

            case AdminActionKind.Teleport:
                permission = "player.teleport";
                if (!TryAddCurrentTargetContext(request, targetContext, contexts) || targetContext is null ||
                    !string.Equals(targetContext.DestinationSystemId, request.TargetSystemId, StringComparison.Ordinal))
                    return false;
                contexts.Add(new PermissionContext(SystemId: targetContext.DestinationSystemId));
                break;

            default:
                return false;
        }

        requirement = new AdminActionPermissionRequirement(permission, contexts, requiresStepUp);
        return true;
    }

    private static bool TryAddCurrentTargetContext(AdminActionRequestV1 request,
        AdminActionTargetContext? targetContext, ICollection<PermissionContext> contexts)
    {
        if (request.TargetId is not { } targetId || targetContext is null ||
            !string.Equals(targetContext.TargetId, targetId, StringComparison.Ordinal) ||
            !AdminQuery.ValidIdentifier(targetContext.CurrentInstanceId) ||
            !AdminQuery.ValidIdentifier(targetContext.CurrentSystemId))
            return false;

        contexts.Add(new PermissionContext(targetContext.CurrentInstanceId, targetContext.CurrentSystemId));
        return true;
    }
}
