using System.Text.Json;
using FluentAssertions;
using InvoiceApi.Middleware;
using Microsoft.AspNetCore.Http;

namespace InvoiceApi.Tests;

public class ExceptionHandlingMiddlewareTests
{
    [Fact]
    public async Task OversizedBody_Returns413_WithErrorBody()
    {
        // Kestrel throws this when a body exceeds MaxRequestBodySize — it must not
        // surface as an unhandled application error (logged at Error on every hit)
        var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        var middleware = new ExceptionHandlingMiddleware(_ =>
            throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));

        await middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        ctx.Response.Body.Position = 0;
        var body = await JsonDocument.ParseAsync(ctx.Response.Body);
        body.RootElement.GetProperty("error").GetString().Should().Be("Request body too large.");
    }
}
