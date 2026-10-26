using NotebookApi.Clients;
using NotebookApi.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AiServiceExceptionHandler>();

builder.Services.AddOptions<AiServiceOptions>()
    .Bind(builder.Configuration.GetSection(AiServiceOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<CorrelationIdHandler>();
builder.Services.AddHttpClient<IAiServiceClient, AiServiceClient>((services, client) =>
    {
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiServiceOptions>>().Value;
        // A trailing slash makes relative paths such as "ask" resolve under the base address instead of replacing its last segment.
        client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    })
    .AddHttpMessageHandler<CorrelationIdHandler>();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthorization();
app.MapControllers();

app.Run();

// Lets the test project start the real application in memory (WebApplicationFactory<Program>).
public partial class Program;
