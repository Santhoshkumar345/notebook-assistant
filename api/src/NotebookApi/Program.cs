using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotebookApi.Clients;
using NotebookApi.Data;
using NotebookApi.Infrastructure;
using NotebookApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AiServiceExceptionHandler>();

builder.Services.AddOptions<AiServiceOptions>()
    .Bind(builder.Configuration.GetSection(AiServiceOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DocumentWriteGate>();
builder.Services.AddDbContext<NotebookDb>((services, db) =>
{
    var path = services.GetRequiredService<IOptions<DatabaseOptions>>().Value.Path;
    if (!Path.IsPathRooted(path))
        path = Path.Combine(services.GetRequiredService<IHostEnvironment>().ContentRootPath, path);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    db.UseSqlite(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
});
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<ConversationService>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<CorrelationIdHandler>();
builder.Services.AddHttpClient<IAiServiceClient, AiServiceClient>((services, client) =>
    {
        var options = services.GetRequiredService<IOptions<AiServiceOptions>>().Value;
        // A trailing slash makes relative paths such as "ask" resolve under the base address instead of replacing its last segment.
        client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    })
    .AddHttpMessageHandler<CorrelationIdHandler>();

var app = builder.Build();

// Creates the database on first run and applies any newer migrations. Fine for one instance; several instances starting at once would need this as a separate deploy step.
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<NotebookDb>().Database.Migrate();

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
