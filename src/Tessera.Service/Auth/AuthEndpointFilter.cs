namespace Tessera.Service.Auth;

/// <summary>
/// Applied to every route under /clients. Validates the Authorization
/// header as an HS256 JWT (see Hs256JwtValidator) and, on success, stashes
/// the token's subject in HttpContext.Items["operator"] for the endpoint
/// handler to use as the operator identity. Endpoint handlers never read
/// an operator from the request body, only from here, so a caller can't
/// claim to be a different operator than the token they presented says
/// they are.
/// </summary>
public sealed class AuthEndpointFilter(Hs256JwtValidator validator) : IEndpointFilter
{
    private const string BearerPrefix = "Bearer ";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var header = context.HttpContext.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.Ordinal))
            return Results.Json(new { error = "missing bearer token" }, statusCode: StatusCodes.Status401Unauthorized);

        var token = header[BearerPrefix.Length..];
        var result = validator.Validate(token);
        if (!result.IsValid)
            return Results.Json(new { error = result.FailureReason }, statusCode: StatusCodes.Status401Unauthorized);

        context.HttpContext.Items["operator"] = result.Subject;
        return await next(context).ConfigureAwait(false);
    }
}
