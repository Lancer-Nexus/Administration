using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LancerNexus.Protocol;

namespace LancerNexus.Administration.Api;

/// <summary>Publishes durable SQL outbox revisions to Redis; subscribers must load their snapshot from SQL.</summary>
public sealed class PermissionRevisionPublisher(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<PermissionRevisionPublisher> logger) : BackgroundService
{
    private const string Channel = "lancer-nexus:permissions:revision";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var endpoint = configuration["Administration:RedisEndpoint"];
                if (!TryEndpoint(endpoint, out var host, out var port))
                    throw new InvalidOperationException("Administration:RedisEndpoint must be configured for permission sync.");
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<PermissionManagementStore>();
                var events = await store.ReadPendingEventsAsync(stoppingToken);
                foreach (var item in events)
                {
                    var message = new PermissionRevisionChanged
                    { EventId = item.EventId, Revision = item.Revision, CreatedUtc = item.CreatedUtc };
                    await PublishAsync(host, port, message, stoppingToken);
                    await store.MarkPublishedAsync(item.EventId, stoppingToken);
                }
                await Task.Delay(events.Length == 0 ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromMilliseconds(25), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "Permission revision outbox publish failed; SQL events remain queued for retry.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private static async Task PublishAsync(string host, int port, PermissionRevisionChanged message, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(message);
        var args = new[] { "PUBLISH", Channel, payload };
        var command = new StringBuilder($"*{args.Length}\r\n");
        foreach (var arg in args) command.Append('$').Append(Encoding.UTF8.GetByteCount(arg)).Append("\r\n").Append(arg).Append("\r\n");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, timeout.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(command.ToString()), timeout.Token);
        var response = new byte[128];
        var count = 0;
        while (count < response.Length)
        {
            var received = await stream.ReadAsync(response.AsMemory(count, response.Length - count), timeout.Token);
            if (received == 0) throw new IOException("Redis closed the permission publish connection.");
            count += received;
            if (count >= 2 && response[count - 2] == '\r' && response[count - 1] == '\n') break;
        }
        if (count < 4 || response[0] != ':' || response[count - 2] != '\r')
            throw new IOException("Redis returned an invalid PUBLISH response.");
    }

    private static bool TryEndpoint(string? value, out string host, out int port)
    {
        host = ""; port = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(value[(separator + 1)..], out port) || port is < 1 or > 65535) return false;
        host = value[..separator].Trim();
        return host.Length > 0;
    }
}
