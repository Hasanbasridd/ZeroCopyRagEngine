using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using ZeroCopyRagEngine.Application.Security;

namespace ZeroCopyRagEngine.Presentation.Middleware;

/// <summary>
/// Validates X-Api-Key or Bearer token authentication for /api/* endpoints when Security:RequireAuthentication is enabled
/// or when Security:ApiKey is configured.
/// </summary>
public sealed class ApiKeyAuthenticationMiddleware
{
    public const string ApiKeyHeaderName = "X-Api-Key";
    private readonly RequestDelegate _next;
    private readonly RagSecurityOptions _options;

    public ApiKeyAuthenticationMiddleware(RequestDelegate next, RagSecurityOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        bool isProtectedApi = context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
        bool enforceAuth = _options.RequireAuthentication || !string.IsNullOrWhiteSpace(_options.ApiKey);

        if (isProtectedApi && enforceAuth)
        {
            string? expectedKey = _options.ApiKey;
            if (string.IsNullOrWhiteSpace(expectedKey))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("API authentication is required but no key was provided.");
                return;
            }

            string providedKey = context.Request.Headers[ApiKeyHeaderName].ToString();
            if (string.IsNullOrWhiteSpace(providedKey))
            {
                string authHeader = context.Request.Headers.Authorization.ToString();
                if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    providedKey = authHeader.Substring("Bearer ".Length).Trim();
                }
            }

            if (!ConstantTimeEquals(providedKey, expectedKey))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Invalid or missing API key.");
                return;
            }
        }

        await _next(context);
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;

        byte[] bytesA = Encoding.UTF8.GetBytes(a);
        byte[] bytesB = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }
}

