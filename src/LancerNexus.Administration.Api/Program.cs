using LancerNexus.Administration.Api;
using LancerNexus.Cluster;
using LancerNexus.Protocol;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<MySqlAdminQueryStore>();
builder.Services.AddSingleton<IAdminQueryStore>(s => s.GetRequiredService<MySqlAdminQueryStore>());
builder.Services.AddSingleton<PermissionManagementStore>();
builder.Services.AddHostedService<PermissionRevisionPublisher>();
builder.Services.AddHttpClient<IClusterStatusSource, CoordinatorStatusSource>(http => http.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddTransient<AdminQueryDispatcher>();
var app = builder.Build();
app.Logger.LogInformation("Administration service starting; SQL permissions, audit and Coordinator readiness are checked dynamically.");
app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "administration" }));
app.MapGet("/health/ready", async (MySqlAdminQueryStore store, IClusterStatusSource source, CancellationToken ct) =>
{
    try
    {
        if (app.Configuration["Administration:GatewayApiKey"] is not { Length: >= 32 } ||
            string.IsNullOrWhiteSpace(app.Configuration["Administration:RedisEndpoint"]))
            return Results.StatusCode(503);
        await store.CheckReadyAsync(ct);
        await app.Services.GetRequiredService<PermissionManagementStore>().CheckReadyAsync(ct);
        await source.ReadAsync(ct);
        return Results.Ok(new { status = "ready" });
    }
    catch (Exception e) when (e is MySqlConnector.MySqlException or HttpRequestException or InvalidOperationException or
        InvalidDataException or System.Text.Json.JsonException or OperationCanceledException)
    { return Results.StatusCode(503); }
});

app.MapGet("/api/v1/admin/permissions/snapshot", async (HttpContext context,
    PermissionManagementStore permissions, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!ServiceCredential.HasGatewayServiceCredential(context.Request, app.Configuration["Administration:GatewayApiKey"]))
        return Results.Unauthorized();
    try { return Results.Ok(PermissionSnapshotDocument.FromSnapshot(await permissions.LoadSnapshotAsync(ct))); }
    catch (Exception e) when (e is MySqlConnector.MySqlException or InvalidOperationException or ArgumentException)
    { return Results.StatusCode(503); }
});

app.MapPost("/api/v1/admin/permissions/ack", async (PermissionRevisionAcknowledged acknowledgement,
    HttpContext context, PermissionManagementStore permissions, CancellationToken ct) =>
{
    if (!ServiceCredential.HasGatewayServiceCredential(context.Request, app.Configuration["Administration:GatewayApiKey"]))
        return Results.Unauthorized();
    try
    {
        return await permissions.AcknowledgeAsync(acknowledgement.InstanceId, acknowledgement.Revision, ct)
            ? Results.Ok() : Results.Conflict();
    }
    catch (Exception e) when (e is MySqlConnector.MySqlException or InvalidOperationException or OperationCanceledException)
    { return Results.StatusCode(503); }
});

app.MapPost("/api/v1/admin/permissions/check", async (PermissionAuthorizationCheck check,
    HttpContext context, PermissionManagementStore permissions, CancellationToken ct) =>
{
    if (!ServiceCredential.HasGatewayServiceCredential(context.Request, app.Configuration["Administration:GatewayApiKey"]))
        return Results.Unauthorized();
    if (check.AccountId == Guid.Empty || check.Permission is not { Length: > 0 and <= 256 }) return Results.BadRequest();
    try
    {
        var allowed = await permissions.HasPermissionAsync(check.AccountId, check.Permission,
            check.InstanceId, check.SystemId, ct);
        return Results.Ok(new { allowed });
    }
    catch (Exception e) when (e is MySqlConnector.MySqlException or InvalidOperationException or ArgumentException)
    { return Results.StatusCode(503); }
});

app.MapPost("/api/v1/admin/permissions/mutations", async (AuthorizedPermissionMutation request,
    HttpContext context, PermissionManagementStore permissions, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!ServiceCredential.HasGatewayServiceCredential(context.Request, app.Configuration["Administration:GatewayApiKey"]))
        return Results.Unauthorized();
    try
    {
        var mutation = request.Mutation;
        if (request.ActorAccountId == Guid.Empty || mutation is null) return Results.BadRequest();
        var authorized = await permissions.HasPermissionAsync(request.ActorAccountId, "permissions.manage",
            mutation.InstanceId, mutation.SystemId, ct);
        if (!authorized) return Results.Forbid();
        var result = await permissions.MutateAsync(request, ct);
        if (result.Status != "sync_pending" || result.Revision is not long revision)
            return Results.Json(result, statusCode: result.Status switch
            { "invalid" => 400, "conflict" => 409, "denied" => 403, _ => 202 });
        try
        {
            var now = DateTimeOffset.UtcNow;
            var active = (await app.Services.GetRequiredService<IClusterStatusSource>().ReadAsync(ct))
                .Where(i => i.IsAlive && i.AgentIsAlive && i.LastHeartbeatUtc <= now.AddSeconds(5) &&
                    now - i.LastHeartbeatUtc <= TimeSpan.FromSeconds(15))
                .Select(i => i.InstanceId).Distinct(StringComparer.Ordinal).ToArray();
            var timer = Stopwatch.StartNew();
            var acknowledged = false;
            while (timer.Elapsed < TimeSpan.FromSeconds(5) && !acknowledged)
            {
                acknowledged = await permissions.AreAcknowledgedAsync(revision, active, ct);
                if (!acknowledged) await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }
            var completed = result with { Status = acknowledged ? "ok" : "sync_pending" };
            return Results.Json(completed, statusCode: acknowledged ? 200 : 202);
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or MySqlConnector.MySqlException or OperationCanceledException)
        { return Results.Json(result, statusCode: 202); }
    }
    catch (Exception e) when (e is MySqlConnector.MySqlException or InvalidOperationException or ArgumentException or OperationCanceledException)
    { return Results.StatusCode(503); }
});

app.MapPost("/api/v1/admin/commands", async (AuthorizedAdminQueryRequest request,
    HttpContext context, AdminQueryDispatcher dispatcher, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var expected = app.Configuration["Administration:GatewayApiKey"];
    var authorization = context.Request.Headers.Authorization.ToString();
    if (expected is not { Length: >= 32 } || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
        authorization.Length > 1024 || !CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..]))))
        return Results.Unauthorized();
    var result = await dispatcher.DispatchAsync(request, ct);
    return Results.Json(result, statusCode: result.Status switch
    { "denied" => 403, "invalid" => 400, "conflict" => 409, "unavailable" => 503, "not_found" => 404, _ => 200 });
});
app.Run();
public partial class Program;

internal static class ServiceCredential
{
    public static bool HasGatewayServiceCredential(HttpRequest request, string? expected)
    {
        var authorization = request.Headers.Authorization.ToString();
        return expected is { Length: >= 32 } && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
            authorization.Length <= 1024 && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
                SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..])));
    }
}
