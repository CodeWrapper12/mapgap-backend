using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GapMap.Api.Infrastructure;

public class ErrorLoggingMiddleware(RequestDelegate next, ILogger<ErrorLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Intercept response stream to read the body
        var originalBodyStream = context.Response.Body;
        using var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception caught in middleware.");
            throw; // Let ASP.NET Core handle it
        }
        finally
        {
            if (context.Response.StatusCode >= 400)
            {
                memoryStream.Seek(0, SeekOrigin.Begin);
                var responseBody = await new StreamReader(memoryStream).ReadToEndAsync();
                logger.LogError("HTTP {StatusCode} returned. Response Body: {Body}", context.Response.StatusCode, responseBody);
            }

            memoryStream.Seek(0, SeekOrigin.Begin);
            await memoryStream.CopyToAsync(originalBodyStream);
        }
    }
}
