
using System.Globalization;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.OnRejected = async (context, cancellationToken) =>
    {
        var response = context.HttpContext.Response;

        var retryAfter = 10.0;

        if (context.Lease.TryGetMetadata(
                MetadataName.RetryAfter, out var retryTime))
        {
            retryAfter = Math.Max(1, Math.Ceiling(retryTime.TotalSeconds));
        }

        response.Headers["Retry-After"] =
            retryAfter.ToString(CultureInfo.InvariantCulture);

        await response.WriteAsJsonAsync(new
        {
            error = "Too Many Requests",
            message = "Rate limit exceeded. Try again later.",
            retryAfter
        }, cancellationToken);
    };

    options.AddPolicy("EntryPolicy", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString()
                 ?? "unknown";

        var isVip = string.Equals(
            httpContext.Request.Headers["X-VIP"].ToString(),
            "true",
            StringComparison.OrdinalIgnoreCase);

        var limit = isVip ? 20 : 5;

        var partitionKey = $"{ip}:{(isVip ? "vip" : "normal")}";

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromSeconds(10),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    options.AddPolicy("BarPolicy", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString()
                 ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(
            ip,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromSeconds(10),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});

var app = builder.Build();

app.UseRouting();
app.UseRateLimiter();

app.MapGet("/api/entry", () => Results.Ok(new
{
    message = "Entry request successful"
}))
.RequireRateLimiting("EntryPolicy");

app.MapGet("/api/bar", () => Results.Ok(new
{
    message = "Bar request successful"
}))
.RequireRateLimiting("BarPolicy");

app.Run();
