namespace RbRules.Api;

/// <summary>Guard voor /api/admin/*: X-Admin-Key-header moet gelijk zijn aan
/// ADMIN_PASSWORD. Niet gezet = admin volledig vergrendeld.</summary>
public class AdminAuthFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
        var provided = context.HttpContext.Request.Headers["X-Admin-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(expected) || provided != expected)
            return Results.Unauthorized();
        return await next(context);
    }
}
