using UrlValidationService.Abstractions;
using UrlValidationService.Background;
using UrlValidationService.Infrastructure;
using UrlValidationService.Models;
using UrlValidationService.Repositories;
using UrlValidationService.Services;

var builder = WebApplication.CreateBuilder(args);

// =============================================================================
// CONFIGURATION
// Design Decision: Strongly-typed configuration enables IntelliSense and
// validation. Environment variables can override appsettings via standard
// .NET configuration binding (e.g., Validation__SyncThreshold=20).
// =============================================================================

builder.Services.Configure<ValidationSettings>(
    builder.Configuration.GetSection(ValidationSettings.SectionName));
builder.Services.Configure<CacheSettings>(
    builder.Configuration.GetSection(CacheSettings.SectionName));
builder.Services.Configure<ResilienceSettings>(
    builder.Configuration.GetSection(ResilienceSettings.SectionName));
builder.Services.Configure<MongoSettings>(
    builder.Configuration.GetSection(MongoSettings.SectionName));

// =============================================================================
// INFRASTRUCTURE
// =============================================================================

// HTTP clients with Polly resilience policies
builder.Services.AddValidationHttpClients();

// In-memory cache (swap for Redis in distributed deployment)
builder.Services.AddSingleton<IValidationCache, InMemoryValidationCache>();

// Rate limiter
builder.Services.AddSingleton<IRateLimiter, PerHostRateLimiter>();

// =============================================================================
// REPOSITORIES
// Design Decision: Scoped lifetime ensures each request gets fresh repository
// instances, preventing connection pooling issues with MongoDB.
// =============================================================================

builder.Services.AddScoped<ILinkRepository, LinkRepository>();
builder.Services.AddScoped<IJobRepository, JobRepository>();

// =============================================================================
// SERVICES
// =============================================================================

builder.Services.AddScoped<ILinkValidationService, LinkValidationService>();

// =============================================================================
// BACKGROUND WORKER
// Interview Note: Hosted services run alongside the web server.
// For production with high load, consider a separate worker process.
// =============================================================================

builder.Services.AddHostedService<ValidationWorker>();

// =============================================================================
// ASP.NET CORE
// =============================================================================

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "URL Validation Service",
        Version = "v1",
        Description = """
            A scalable URL validation API with hybrid sync/async processing.
            
            **Workflow:**
            1. POST /api/links - Store URLs to be validated
            2. POST /api/links/validate - Trigger validation
               - Small batches: Immediate results
               - Large batches: Returns jobId for polling
            3. GET /api/links/jobs/{jobId} - Poll async job status
            4. GET /api/links/broken - Retrieve all broken links
            
            **Key Features:**
            - Hybrid sync/async based on batch size threshold
            - Background worker for large-scale validation
            - Caching to avoid repeated validations
            - Resilience patterns (circuit breaker, timeout, rate limiting)
            """
    });
});

// Add health checks
builder.Services.AddHealthChecks();

var app = builder.Build();

// =============================================================================
// MIDDLEWARE PIPELINE
// =============================================================================

// Swagger for API documentation
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "URL Validation Service v1");
    options.RoutePrefix = string.Empty; // Serve Swagger UI at root
});

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/healthz");

// =============================================================================
// STARTUP INITIALIZATION
// =============================================================================

// Ensure MongoDB indexes are created
using (var scope = app.Services.CreateScope())
{
    var linkRepository = scope.ServiceProvider.GetRequiredService<ILinkRepository>();
    var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
    
    await linkRepository.EnsureIndexesAsync();
    await jobRepository.EnsureIndexesAsync();
}

// Log startup information
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var validationSettings = builder.Configuration.GetSection(ValidationSettings.SectionName).Get<ValidationSettings>();
logger.LogInformation(
    """
    URL Validation Service starting...
    - Sync threshold: {SyncThreshold} links
    - Request timeout: {Timeout}s
    - Max concurrency: {Concurrency}
    - Worker batch size: {BatchSize}
    - Worker polling: {Polling}s
    """,
    validationSettings?.SyncThreshold ?? 10,
    validationSettings?.RequestTimeoutSeconds ?? 10,
    validationSettings?.MaxConcurrency ?? 20,
    validationSettings?.BatchSize ?? 50,
    validationSettings?.WorkerPollingIntervalSeconds ?? 5);

app.Run();
