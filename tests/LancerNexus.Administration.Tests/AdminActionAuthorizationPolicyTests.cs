using LancerNexus.Administration.Api;
using LancerNexus.Cluster;
using LancerNexus.Protocol;
using Xunit;

namespace LancerNexus.Administration.Tests;

public sealed class AdminActionAuthorizationPolicyTests
{
    [Fact]
    public void MapsActionTypesToNarrowPermissionNodes()
    {
        var cases = new[]
        {
            (Request(AdminActionKind.Message, AdminActionTargetKind.All), "admin.message"),
            (Request(AdminActionKind.Kick, AdminActionTargetKind.Player), "player.kick"),
            (Request(AdminActionKind.Ban, AdminActionTargetKind.Account, expiry: DateTimeOffset.UtcNow.AddHours(1)), "player.ban"),
            (Request(AdminActionKind.Unban, AdminActionTargetKind.Account), "player.unban"),
            (Request(AdminActionKind.Drain, AdminActionTargetKind.Instance), "server.drain"),
            (Request(AdminActionKind.Move, AdminActionTargetKind.Player, targetInstanceId: "instance-02"), "transfer.move"),
            (Request(AdminActionKind.Teleport, AdminActionTargetKind.Player, targetSystemId: "li01"), "player.teleport"),
            (Request(AdminActionKind.Transfer, AdminActionTargetKind.Player, targetInstanceId: "instance-02"), "transfer.start")
        };

        foreach (var (request, expectedPermission) in cases)
        {
            Assert.True(AdminActionAuthorizationPolicy.TryCreateRequirement(request,
                TargetContext(request), out var requirement));
            Assert.Equal(expectedPermission, requirement!.Permission);
        }
    }

    [Fact]
    public void MessageScopesFollowTheTypedTarget()
    {
        var global = Create(Request(AdminActionKind.Message, AdminActionTargetKind.All));
        var system = Create(Request(AdminActionKind.Message, AdminActionTargetKind.System, targetId: "li01"));
        var instance = Create(Request(AdminActionKind.Message, AdminActionTargetKind.Instance, targetId: "instance-01"));
        var playerRequest = Request(AdminActionKind.Message, AdminActionTargetKind.Player, targetId: "player-01");
        var player = Create(playerRequest, TargetContext(playerRequest));

        Assert.Equal(new PermissionContext(), Assert.Single(global.Contexts));
        Assert.Equal(new PermissionContext(SystemId: "li01"), Assert.Single(system.Contexts));
        Assert.Equal(new PermissionContext(InstanceId: "instance-01"), Assert.Single(instance.Contexts));
        Assert.Equal(new PermissionContext("instance-01", "li01"), Assert.Single(player.Contexts));
    }

    [Fact]
    public void MovementRequiresCurrentAndResolvedDestinationContexts()
    {
        var move = Request(AdminActionKind.Move, AdminActionTargetKind.Player,
            targetId: "player-01", targetInstanceId: "instance-02");

        Assert.True(AdminActionAuthorizationPolicy.TryCreateRequirement(move, TargetContext(move), out var requirement));
        Assert.Equal(
        [
            new PermissionContext("instance-01", "li01"),
            new PermissionContext("instance-02", "br01")
        ], requirement!.Contexts);
        Assert.False(AdminActionAuthorizationPolicy.TryCreateRequirement(move,
            TargetContext(move) with { DestinationInstanceId = "instance-03" }, out _));
        Assert.False(AdminActionAuthorizationPolicy.TryCreateRequirement(move,
            TargetContext(move) with { DestinationSystemId = null }, out _));
    }

    [Fact]
    public void PlayerActionsFailClosedWithoutMatchingCurrentCoordinatorTarget()
    {
        var request = Request(AdminActionKind.Kick, AdminActionTargetKind.Player, targetId: "player-01");

        Assert.False(AdminActionAuthorizationPolicy.TryCreateRequirement(request, null, out _));
        Assert.False(AdminActionAuthorizationPolicy.TryCreateRequirement(request,
            TargetContext(request) with { TargetId = "player-02" }, out _));
        Assert.False(AdminActionAuthorizationPolicy.TryCreateRequirement(request,
            TargetContext(request) with { CurrentInstanceId = null }, out _));
    }

    [Fact]
    public void PermanentBanAndDrainRequireStepUpWhileTemporaryBanDoesNot()
    {
        var permanentBan = Create(Request(AdminActionKind.Ban, AdminActionTargetKind.Account,
            targetId: "account-01", confirmationId: Guid.NewGuid()));
        var temporaryBan = Create(Request(AdminActionKind.Ban, AdminActionTargetKind.Account,
            targetId: "account-01", expiry: DateTimeOffset.UtcNow.AddHours(1)));
        var drain = Create(Request(AdminActionKind.Drain, AdminActionTargetKind.Instance,
            targetId: "instance-01", confirmationId: Guid.NewGuid()));

        Assert.True(permanentBan.RequiresStepUp);
        Assert.False(temporaryBan.RequiresStepUp);
        Assert.True(drain.RequiresStepUp);
    }

    [Fact]
    public void MoveRequiresPermissionInBothCurrentAndDestinationContexts()
    {
        var request = Request(AdminActionKind.Move, AdminActionTargetKind.Player,
            targetId: "player-01", targetInstanceId: "instance-02");
        var actor = request.ActorAccountId;
        var currentOnly = Permissions(actor,
            new PermissionNode("transfer.move", true, PermissionPatternKind.Exact,
                InstanceId: "instance-01", SystemId: "li01"));
        var both = Permissions(actor,
            new PermissionNode("transfer.move", true, PermissionPatternKind.Exact,
                InstanceId: "instance-01", SystemId: "li01"),
            new PermissionNode("transfer.move", true, PermissionPatternKind.Exact,
                InstanceId: "instance-02", SystemId: "br01"));

        Assert.False(AdminActionAuthorizationPolicy.IsAuthorized(request, TargetContext(request), currentOnly,
            stepUpVerified: true, DateTimeOffset.UtcNow));
        Assert.True(AdminActionAuthorizationPolicy.IsAuthorized(request, TargetContext(request), both,
            stepUpVerified: true, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void StepUpIsRequiredInAdditionToThePermissionGrant()
    {
        var request = Request(AdminActionKind.Drain, AdminActionTargetKind.Instance,
            targetId: "instance-01", confirmationId: Guid.NewGuid());
        var permissions = Permissions(request.ActorAccountId,
            new PermissionNode("server.drain", true, PermissionPatternKind.Exact, InstanceId: "instance-01"));

        Assert.False(AdminActionAuthorizationPolicy.IsAuthorized(request, null, permissions,
            stepUpVerified: false, DateTimeOffset.UtcNow));
        Assert.True(AdminActionAuthorizationPolicy.IsAuthorized(request, null, permissions,
            stepUpVerified: true, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void StalePermissionSnapshotFailsClosed()
    {
        var request = Request(AdminActionKind.Message, AdminActionTargetKind.All);
        var permissions = Permissions(request.ActorAccountId,
            new PermissionNode("admin.message", true, PermissionPatternKind.Exact));
        permissions.RequireRevision(2);

        Assert.False(AdminActionAuthorizationPolicy.IsAuthorized(request, null, permissions,
            stepUpVerified: true, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void InvalidContractsDoNotProduceAuthorizationRequirements()
    {
        Assert.False(AdminActionAuthorizationPolicy.TryCreateRequirement(
            Request(AdminActionKind.Drain, AdminActionTargetKind.Instance) with { CommandId = Guid.Empty }, null, out _));
    }

    private static AdminActionPermissionRequirement Create(AdminActionRequestV1 request,
        AdminActionTargetContext? context = null)
    {
        Assert.True(AdminActionAuthorizationPolicy.TryCreateRequirement(request, context, out var requirement));
        return requirement!;
    }

    private static AdminActionTargetContext TargetContext(AdminActionRequestV1 request) =>
        new(request.TargetId!, "instance-01", "li01",
            request.TargetInstanceId,
            request.TargetInstanceId is not null ? "br01" : request.TargetSystemId);

    private static AdminActionRequestV1 Request(AdminActionKind kind, AdminActionTargetKind targetKind,
        string? targetId = null, string? targetInstanceId = null, string? targetSystemId = null,
        DateTimeOffset? expiry = null, Guid? confirmationId = null) => new()
        {
            CommandId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            ActorAccountId = Guid.NewGuid(),
            Source = "rest",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            Kind = kind,
            TargetKind = targetKind,
            TargetId = targetId ?? (targetKind == AdminActionTargetKind.All ? null : "player-01"),
            TargetInstanceId = targetInstanceId,
            TargetSystemId = targetSystemId,
            Reason = kind is AdminActionKind.Ban or AdminActionKind.Drain ? "test reason" : null,
            Message = kind == AdminActionKind.Message ? "maintenance notice" : null,
            ExpiresUtc = expiry,
            ConfirmationId = confirmationId ?? (kind == AdminActionKind.Drain ? Guid.NewGuid() : null)
        };

    private static PermissionService Permissions(Guid accountId, params PermissionNode[] nodes) =>
        new(new PermissionSnapshot(1,
            [new PermissionUser(accountId, [], nodes)],
            []));
}
