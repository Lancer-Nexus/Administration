var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.Logger.LogWarning(
    "Administration API is starting in skeleton mode: authorization, audit storage and command dispatch are not configured; readiness remains unavailable.");

app.MapGet("/health/live", () => Results.Ok(new
{
    status = "live",
    service = "administration"
}));

// Keep readiness closed until authorization, audit storage and command dispatch exist.
app.MapGet("/health/ready", () => Results.Problem(
    title: "Administration is not configured",
    detail: "Required authorization and audit services are not registered.",
    statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapGet("/api/v1/admin", () => Results.Problem(
    title: "Administration API is not configured",
    statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapPost("/api/v1/admin/commands", () => Results.Problem(
    title: "Administration command dispatch is not configured",
    detail: "Commands remain unavailable until Gateway identity, current authorization and audit storage are registered.",
    statusCode: StatusCodes.Status503ServiceUnavailable));

app.Run();

public partial class Program;
