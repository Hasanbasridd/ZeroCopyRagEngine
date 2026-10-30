using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroCopyRagEngine.Infrastructure.Resilience;

public sealed class ResiliencePipelineOptions
{
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public int MaxRetries { get; set; } = 3;
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);
    public int CircuitBreakerFailureThreshold { get; set; } = 5;
    public TimeSpan CircuitBreakerBreakDuration { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class CircuitBrokenException : Exception
{
    public CircuitBrokenException(string message) : base(message) { }
}

/// <summary>
/// HTTP resilience delegating handler providing per-attempt timeout, exponential backoff retry, and circuit breaker protection
/// for outbound LLM and Embedding HTTP requests.
/// </summary>
public sealed class HttpResilienceHandler : DelegatingHandler
{
    private readonly ResiliencePipelineOptions _options;
    private readonly object _circuitLock = new();
    private int _consecutiveFailures;
    private DateTimeOffset _circuitOpenedUntil = DateTimeOffset.MinValue;

    public HttpResilienceHandler(ResiliencePipelineOptions? options = null, HttpMessageHandler? innerHandler = null)
    {
        _options = options ?? new ResiliencePipelineOptions();
        if (innerHandler is not null)
        {
            InnerHandler = innerHandler;
        }
    }

    public bool IsCircuitOpen
    {
        get
        {
            lock (_circuitLock)
            {
                return DateTimeOffset.UtcNow < _circuitOpenedUntil;
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ThrowIfCircuitOpen();

        Exception? lastException = null;
        int maxAttempts = Math.Max(1, _options.MaxRetries + 1);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.RequestTimeout);

            try
            {
                HttpResponseMessage response = await base.SendAsync(request, timeoutCts.Token);
                if ((int)response.StatusCode >= 500 || (int)response.StatusCode == 429)
                {
                    RecordFailure();
                    if (attempt < maxAttempts)
                    {
                        response.Dispose();
                        await DelayBeforeRetryAsync(attempt, cancellationToken);
                        continue;
                    }
                    return response;
                }

                RecordSuccess();
                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                RecordFailure();
                lastException = new TimeoutException($"HTTP request to '{request.RequestUri}' timed out after {_options.RequestTimeout.TotalMilliseconds.ToString()} ms (attempt {attempt.ToString()}/{maxAttempts.ToString()}).");
                if (attempt < maxAttempts)
                {
                    await DelayBeforeRetryAsync(attempt, cancellationToken);
                    continue;
                }
            }
            catch (HttpRequestException ex)
            {
                RecordFailure();
                lastException = ex;
                if (attempt < maxAttempts)
                {
                    await DelayBeforeRetryAsync(attempt, cancellationToken);
                    continue;
                }
            }
        }

        throw lastException ?? new HttpRequestException("HTTP request failed after all resilience retry attempts.");
    }

    private void ThrowIfCircuitOpen()
    {
        lock (_circuitLock)
        {
            if (DateTimeOffset.UtcNow < _circuitOpenedUntil)
            {
                throw new CircuitBrokenException(
                    $"Circuit breaker is OPEN until {_circuitOpenedUntil.ToString("O")} after {_consecutiveFailures.ToString()} consecutive failures.");
            }
        }
    }

    private void RecordSuccess()
    {
        lock (_circuitLock)
        {
            _consecutiveFailures = 0;
            _circuitOpenedUntil = DateTimeOffset.MinValue;
        }
    }

    private void RecordFailure()
    {
        lock (_circuitLock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _options.CircuitBreakerFailureThreshold)
            {
                _circuitOpenedUntil = DateTimeOffset.UtcNow.Add(_options.CircuitBreakerBreakDuration);
            }
        }
    }

    private Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        double backoffMultiplier = Math.Pow(2, attempt - 1);
        var delay = TimeSpan.FromMilliseconds(_options.BaseRetryDelay.TotalMilliseconds * backoffMultiplier);
        return Task.Delay(delay, cancellationToken);
    }
}
