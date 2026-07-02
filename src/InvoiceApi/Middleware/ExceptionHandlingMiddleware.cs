using InvoiceApi.Exceptions;

namespace InvoiceApi.Middleware;

/// <summary>
/// Maps domain exceptions to HTTP status codes with the { error } body shape
/// the frontend expects. Controllers throw; nobody try/catches per action.
/// </summary>
public class ExceptionHandlingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (Exception ex) when (StatusCodeFor(ex) is int status)
        {
            if (ctx.Response.HasStarted)
                throw;

            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
    }

    private static int? StatusCodeFor(Exception ex) => ex switch
    {
        ValidationException => StatusCodes.Status400BadRequest,
        UnauthorizedException => StatusCodes.Status401Unauthorized,
        NotFoundException => StatusCodes.Status404NotFound,
        ConflictException => StatusCodes.Status409Conflict,
        _ => null
    };
}
