using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;
using OrderIntake.Api.Infrastructure;
using OrderIntake.Application;
using OrderIntake.Infrastructure;
using OrderIntake.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

const string CorsPolicyName = "spa";

// ---------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        // Statuses travel as "Confirmed", not 1. The wire format should be
        // readable in a browser's network tab and should not silently change
        // meaning if someone reorders the enum.
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

// Each layer registers its own dependencies. Program.cs composes; it does not
// enumerate. Adding a service to the application layer never means editing here.
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddHealthChecks();

builder.Services.AddCors(options =>
    options.AddPolicy(CorsPolicyName, policy =>
    {
        var allowedOrigins = builder.Configuration
                                 .GetSection("Cors:AllowedOrigins")
                                 .Get<string[]>()
                             ?? new[] { "http://localhost:4200" };

        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            // So the Angular client can read the idempotency signal. Custom
            // response headers are invisible to browser JS unless exposed.
            .WithExposedHeaders("X-Idempotent-Replay", "X-Status-Changed");
    }));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Order Intake API",
        Version = "v1",
        Description =
            "Records customer purchase orders and tracks their status. " +
            "Submission is idempotent on the client-supplied external reference."
    });

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);

    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }
});

var app = builder.Build();

// ---------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------

// First in the pipeline: nothing downstream should ever return an unshaped error.
app.UseExceptionHandler();

// Swagger is on in every environment here. This is an internal tool behind a
// take-home README, and a reviewer landing on a live API explorer is worth more
// than the habit of hiding it. A public deployment would gate this.
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Order Intake API v1");
    options.DocumentTitle = "Order Intake API";
});

app.UseCors(CorsPolicyName);

app.MapControllers();
app.MapHealthChecks("/health");

// Redirect the root to the API explorer so `dotnet run` plus a click lands
// somewhere useful instead of on a 404.
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

await DatabaseInitialiser.InitialiseAsync(app.Services);

await app.RunAsync();

/// <summary>
/// Exposed so an integration test project can drive the real pipeline with
/// WebApplicationFactory. Top-level statements generate an internal Program
/// class, which a test project cannot see without this.
/// </summary>
public partial class Program;
