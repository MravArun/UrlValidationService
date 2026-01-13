using System.Net;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Extensions.Http;
using Polly.Timeout;
using UrlValidationService.Models;

namespace UrlValidationService.Infrastructure;

/// <summary>
/// HTTP client configuration with Polly resilience policies.
/// Design Decision: Named HTTP clients with different policies for sync vs async scenarios.
/// 
/// Interview Talking Points:
/// 1. Circuit Breaker: Prevents cascade failures when target servers are down
/// 2. Timeout: Bounded wait times prevent resource exhaustion
/// 3. Retry (async only): Improves success rate without blocking user requests
/// </summary>
public static class HttpClientConfiguration
{
    public const string ValidationClientName = "ValidationClient";
    public const string AsyncValidationClientName = "AsyncValidationClient";

    /// <summary>
    /// Configures HTTP clients with resilience policies.
    /// </summary>
    public static IServiceCollection AddValidationHttpClients(
        this IServiceCollection services)
    {
        // Sync client: No retries (user waiting), but has circuit breaker and timeout
        services.AddHttpClient(ValidationClientName, ConfigureBaseClient)
            .ConfigurePrimaryHttpMessageHandler(ConfigureHandler)
            .AddPolicyHandler((sp, _) => GetTimeoutPolicy(sp))
            .AddPolicyHandler((sp, _) => GetCircuitBreakerPolicy(sp));

        // Async client: Has retries (background processing), circuit breaker, and timeout
        services.AddHttpClient(AsyncValidationClientName, ConfigureBaseClient)
            .ConfigurePrimaryHttpMessageHandler(ConfigureHandler)
            .AddPolicyHandler((sp, _) => GetRetryPolicy(sp))
            .AddPolicyHandler((sp, _) => GetTimeoutPolicy(sp))
            .AddPolicyHandler((sp, _) => GetCircuitBreakerPolicy(sp));

        return services;
    }

    private static void ConfigureBaseClient(IServiceProvider sp, HttpClient client)
    {
        var settings = sp.GetRequiredService<IOptions<ValidationSettings>>().Value;
        
        // Don't follow redirects automatically - we want to count them
        client.DefaultRequestHeaders.Add("User-Agent", "UrlValidationService/1.0");
        
        // Overall timeout as safety net (Polly timeout is per-attempt)
        client.Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds * 3);
    }

    private static HttpClientHandler ConfigureHandler(IServiceProvider sp)
    {
        var settings = sp.GetRequiredService<IOptions<ValidationSettings>>().Value;
        
        return new HttpClientHandler
        {
            // Manual redirect handling to detect loops
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = settings.MaxRedirects,
            
            // Accept any SSL certificate for validation purposes
            // Interview Note: In production, might want stricter validation
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            
            // Use system proxy settings
            UseProxy = true,
            
            // Enable compression
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
    }

    /// <summary>
    /// Timeout policy - prevents hanging on slow servers.
    /// </summary>
    private static IAsyncPolicy<HttpResponseMessage> GetTimeoutPolicy(IServiceProvider sp)
    {
        var settings = sp.GetRequiredService<IOptions<ValidationSettings>>().Value;
        
        return Policy.TimeoutAsync<HttpResponseMessage>(
            TimeSpan.FromSeconds(settings.RequestTimeoutSeconds),
            TimeoutStrategy.Optimistic);
    }

    /// <summary>
    /// Circuit breaker - fail fast when a host is consistently failing.
    /// Design Decision: Per-host circuit breakers would be ideal but add complexity.
    /// Global circuit breaker is simpler and still provides protection.
    /// </summary>
    private static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy(IServiceProvider sp)
    {
        var settings = sp.GetRequiredService<IOptions<ResilienceSettings>>().Value;
        var logger = sp.GetRequiredService<ILogger<HttpClient>>();

        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .Or<TimeoutRejectedException>()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: settings.CircuitBreakerThreshold,
                durationOfBreak: TimeSpan.FromSeconds(settings.CircuitBreakerDurationSeconds),
                onBreak: (outcome, duration) =>
                {
                    logger.LogWarning(
                        "Circuit breaker opened for {Duration}s due to {Exception}",
                        duration.TotalSeconds,
                        outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString());
                },
                onReset: () =>
                {
                    logger.LogInformation("Circuit breaker reset");
                });
    }

    /// <summary>
    /// Retry policy - only for async processing (user not waiting).
    /// Uses exponential backoff to avoid hammering failing servers.
    /// </summary>
    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy(IServiceProvider sp)
    {
        var settings = sp.GetRequiredService<IOptions<ResilienceSettings>>().Value;
        var logger = sp.GetRequiredService<ILogger<HttpClient>>();

        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .Or<TimeoutRejectedException>()
            .WaitAndRetryAsync(
                retryCount: settings.RetryAttempts,
                sleepDurationProvider: attempt => 
                    TimeSpan.FromMilliseconds(settings.RetryBaseDelayMs * Math.Pow(2, attempt)),
                onRetry: (outcome, delay, attempt, _) =>
                {
                    logger.LogDebug(
                        "Retry {Attempt} after {Delay}ms due to {Reason}",
                        attempt,
                        delay.TotalMilliseconds,
                        outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString());
                });
    }
}
