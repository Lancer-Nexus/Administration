using LancerNexus.Administration.Api;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LancerNexus.Administration.Tests;

public sealed class ServiceCredentialTests
{
    private const string ConfiguredKey = "test-gateway-service-key-with-at-least-32-bytes";

    [Theory]
    [InlineData(null, "Bearer test-gateway-service-key-with-at-least-32-bytes", false)]
    [InlineData("too-short", "Bearer too-short", false)]
    [InlineData(ConfiguredKey, null, false)]
    [InlineData(ConfiguredKey, "Basic test-gateway-service-key-with-at-least-32-bytes", false)]
    [InlineData(ConfiguredKey, "Bearer wrong-gateway-service-key-with-at-least-32-bytes", false)]
    [InlineData(ConfiguredKey, "Bearer test-gateway-service-key-with-at-least-32-bytes ", false)]
    [InlineData(ConfiguredKey, "bearer test-gateway-service-key-with-at-least-32-bytes", true)]
    [InlineData(ConfiguredKey, "Bearer test-gateway-service-key-with-at-least-32-bytes", true)]
    public void ValidatesGatewayServiceBearerCredential(string? expected, string? authorization, bool accepted)
    {
        var context = new DefaultHttpContext();
        if (authorization is not null)
            context.Request.Headers.Authorization = authorization;

        Assert.Equal(accepted, ServiceCredential.HasGatewayServiceCredential(context.Request, expected));
    }

    [Fact]
    public void RejectsOversizedAuthorizationHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer " + new string('x', 1024);

        Assert.False(ServiceCredential.HasGatewayServiceCredential(context.Request, ConfiguredKey));
    }
}
