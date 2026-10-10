using LancerNexus.Administration.Api;
using LancerNexus.Protocol;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using MySqlConnector;
using Xunit;

namespace LancerNexus.Administration.Tests;

public sealed class PermissionManagementMySqlTests
{
    [MySqlFact]
    public async Task MutationsAreAtomicAuthorizedAndIdempotent()
    {
        var source = Environment.GetEnvironmentVariable("LANCER_NEXUS_ADMINISTRATION_TEST_MYSQL");

        var sourceBuilder = new MySqlConnectionStringBuilder(source!) { Database = "mysql" };
        var database = $"admin_test_{Guid.NewGuid():N}";
        await using var server = new MySqlConnection(sourceBuilder.ConnectionString);
        await server.OpenAsync();
        await using (var create = server.CreateCommand())
        {
            create.CommandText = $"CREATE DATABASE `{database}` CHARACTER SET utf8mb4";
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var databaseBuilder = new MySqlConnectionStringBuilder(sourceBuilder.ConnectionString) { Database = database };
            await using (var connection = new MySqlConnection(databaseBuilder.ConnectionString))
            {
                await connection.OpenAsync();
                await ExecuteAsync(connection, "CREATE TABLE accounts (account_id CHAR(36) NOT NULL PRIMARY KEY) ENGINE=InnoDB");
                await ExecuteAsync(connection, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sql", "001_admin_queries.sql")));
                await ExecuteAsync(connection, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sql", "002_permissions.sql")));
                await ExecuteAsync(connection, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sql", "004_permission_audit_decisions.sql")));
                await ExecuteAsync(connection, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sql", "005_admin_action_outbox.sql")));
            }

            var actor = Guid.NewGuid();
            var deniedActor = Guid.NewGuid();
            await using (var connection = new MySqlConnection(databaseBuilder.ConnectionString))
            {
                await connection.OpenAsync();
                await using var seed = connection.CreateCommand();
                seed.CommandText = "INSERT INTO accounts(account_id) VALUES (@actor),(@deniedActor); INSERT INTO permission_user_nodes(account_id,position,permission_pattern,pattern_kind,allowed) VALUES (@actor,0,'permissions.manage',0,TRUE),(@actor,1,'admin.message',0,TRUE)";
                seed.Parameters.AddWithValue("@actor", actor.ToString());
                seed.Parameters.AddWithValue("@deniedActor", deniedActor.ToString());
                await seed.ExecuteNonQueryAsync();
            }

            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Administration"] = databaseBuilder.ConnectionString
            }).Build();
            var store = new PermissionManagementStore(config, TimeProvider.System);
            var actionStore = new AdminActionSubmissionStore(config, TimeProvider.System);
            var key = Guid.NewGuid().ToString("N");
            var request = Request(actor, key, new(PermissionMutationKind.UpsertGroup, "test-group", Rank: 5));

            var denied = await store.MutateAsync(Request(deniedActor, Guid.NewGuid().ToString("N"),
                new(PermissionMutationKind.UpsertGroup, "denied-group")), CancellationToken.None);
            Assert.Equal("denied", denied.Status);
            using (var httpDenied = await PostMutationAsync(databaseBuilder.ConnectionString,
                       Request(deniedActor, Guid.NewGuid().ToString("N"), new(PermissionMutationKind.UpsertGroup, "http-denied-group"))))
                Assert.Equal(HttpStatusCode.Forbidden, httpDenied.StatusCode);

            var created = await store.MutateAsync(request, CancellationToken.None);
            Assert.Equal("sync_pending", created.Status);
            Assert.Equal(2, created.Revision);
            Assert.Equal(created, await store.MutateAsync(request, CancellationToken.None));

            var keyReuse = await store.MutateAsync(Request(actor, key,
                new(PermissionMutationKind.UpsertGroup, "other-group", Rank: 5)), CancellationToken.None);
            Assert.Equal("conflict", keyReuse.Status);
            using (var httpConflict = await PostMutationAsync(databaseBuilder.ConnectionString,
                       Request(actor, key, new(PermissionMutationKind.UpsertGroup, "other-group", Rank: 5))))
                Assert.Equal(HttpStatusCode.Conflict, httpConflict.StatusCode);

            var concurrentResults = await Task.WhenAll(Enumerable.Range(0, 4).Select(index =>
                store.MutateAsync(Request(actor, Guid.NewGuid().ToString("N"),
                    new(PermissionMutationKind.UpsertGroup, $"parallel-{index}", Rank: index)), CancellationToken.None)));
            Assert.All(concurrentResults, result => Assert.Equal("sync_pending", result.Status));
            Assert.Equal(new long[] { 3, 4, 5, 6 }, concurrentResults.Select(result => result.Revision!.Value).Order().ToArray());

            var cycle = await store.MutateAsync(Request(actor, Guid.NewGuid().ToString("N"),
                new(PermissionMutationKind.UpsertGroup, "cycle-group", Parents: ["cycle-group"])), CancellationToken.None);
            Assert.Equal("conflict", cycle.Status);

            var action = new AdminActionRequestV1
            {
                CommandId = Guid.NewGuid(),
                CorrelationId = Guid.NewGuid(),
                ActorAccountId = actor,
                Source = "rest",
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                Kind = AdminActionKind.Message,
                TargetKind = AdminActionTargetKind.All,
                Message = "Maintenance starts soon"
            };
            var authorizedAction = new AuthorizedAdminActionSubmission(Guid.NewGuid(), action, null, false);
            var pending = await actionStore.SubmitAsync(authorizedAction, CancellationToken.None);
            Assert.Equal(AdminActionStatus.Pending, pending.Status);
            Assert.NotNull(pending.OperationId);
            Assert.NotNull(pending.Sequence);
            var replay = await actionStore.SubmitAsync(authorizedAction, CancellationToken.None);
            Assert.Equal(pending.OperationId, replay.OperationId);
            Assert.Equal(pending.Sequence, replay.Sequence);
            Assert.Equal(AdminActionStatus.Conflict, (await actionStore.SubmitAsync(authorizedAction with
            {
                Action = action with { Message = "Different message" }
            }, CancellationToken.None)).Status);
            var deniedAction = action with
            {
                CommandId = Guid.NewGuid(),
                CorrelationId = Guid.NewGuid(),
                ActorAccountId = deniedActor,
                IdempotencyKey = Guid.NewGuid().ToString("N")
            };
            Assert.Equal(AdminActionStatus.Denied, (await actionStore.SubmitAsync(
                new AuthorizedAdminActionSubmission(Guid.NewGuid(), deniedAction, null, false), CancellationToken.None)).Status);
            using (var httpAction = await PostActionAsync(databaseBuilder.ConnectionString, authorizedAction))
                Assert.Equal(HttpStatusCode.Accepted, httpAction.StatusCode);

            await using (var revoke = new MySqlConnection(databaseBuilder.ConnectionString))
            {
                await revoke.OpenAsync();
                await using var revokePermission = revoke.CreateCommand();
                revokePermission.CommandText = "DELETE FROM permission_user_nodes WHERE account_id=@actor";
                revokePermission.Parameters.AddWithValue("@actor", actor.ToString());
                await revokePermission.ExecuteNonQueryAsync();
            }
            Assert.Equal("denied", (await store.MutateAsync(request, CancellationToken.None)).Status);

            await using var verify = new MySqlConnection(databaseBuilder.ConnectionString);
            await verify.OpenAsync();
            Assert.Equal(6L, await ScalarLongAsync(verify, "SELECT revision FROM permission_state WHERE singleton_id=1"));
            Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM permission_groups WHERE group_name='test-group'"));
            Assert.Equal(0L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM permission_groups WHERE group_name='denied-group'"));
            Assert.Equal(4L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM permission_groups WHERE group_name LIKE 'parallel-%'"));
            Assert.Equal(0L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM permission_groups WHERE group_name='cycle-group'"));
            Assert.Equal(5L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM permission_outbox"));
            Assert.Equal(8L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM permission_audit"));
            Assert.Equal(3L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM permission_audit WHERE decision='denied' AND reason='permissions.manage_missing' AND result='denied'"));
            Assert.Equal(5L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM permission_audit WHERE decision='allowed' AND reason='authorized' AND result='sync_pending'"));
            Assert.Equal(5L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM permission_mutation_keys"));
            Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM admin_action_commands"));
            Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM admin_action_outbox"));
            Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM admin_action_outbox WHERE JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.Action.Message'))='Maintenance starts soon' AND JSON_EXTRACT(payload_json,'$.StepUpVerified') IS NULL"));
            Assert.Equal(5L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM admin_action_audit"));
            Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM admin_action_audit WHERE decision='denied' AND reason='permission_denied'"));
        }
        finally
        {
            await using var drop = server.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS `{database}`";
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static AuthorizedPermissionMutation Request(Guid actor, string key, PermissionMutation mutation) =>
        new(actor, "integration-test", Guid.NewGuid(), key, mutation);

    private static async Task<HttpResponseMessage> PostMutationAsync(string connectionString,
        AuthorizedPermissionMutation request)
    {
        var gatewayApiKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Administration"] = connectionString,
                    ["Administration:GatewayApiKey"] = gatewayApiKey,
                    ["Administration:RedisEndpoint"] = "127.0.0.1:1"
                })));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", gatewayApiKey);
        return await client.PostAsJsonAsync("/api/v1/admin/permissions/mutations", request);
    }

    private static async Task<HttpResponseMessage> PostActionAsync(string connectionString,
        AuthorizedAdminActionSubmission request)
    {
        var gatewayApiKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Administration"] = connectionString,
                    ["Administration:GatewayApiKey"] = gatewayApiKey,
                    ["Administration:RedisEndpoint"] = "127.0.0.1:1"
                })));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", gatewayApiKey);
        return await client.PostAsJsonAsync("/api/v1/admin/actions", request);
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(MySqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}

public sealed class MySqlFactAttribute : FactAttribute
{
    public MySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LANCER_NEXUS_ADMINISTRATION_TEST_MYSQL")))
            Skip = "Set LANCER_NEXUS_ADMINISTRATION_TEST_MYSQL to run the Administration MySQL integration test.";
    }
}
