namespace RbRules.Api;

/// <summary>Guard voor /api/admin/*: X-Admin-Key-header moet gelijk zijn aan
/// ADMIN_PASSWORD. Niet gezet = admin volledig vergrendeld.</summary>
public class AdminAuthFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!IsAdmin(context.HttpContext)) return Results.Unauthorized();
        return await next(context);
    }

    /// <summary>Zelfde check als de filter, herbruikbaar buiten /api/admin/*
    /// voor endpoints waar autoriteit de route bepaalt in plaats van hem
    /// volledig af te sluiten (#166: in-chat-rulings — beheerder vs
    /// ingelogde gebruiker vs anoniem). Server-authoritatief: leest alleen de
    /// echte ADMIN_PASSWORD-omgevingsvariabele, nooit request-invoer.</summary>
    public static bool IsAdmin(HttpContext http)
    {
        var expected = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
        var provided = http.Request.Headers["X-Admin-Key"].FirstOrDefault();
        return !string.IsNullOrEmpty(expected) && provided == expected;
    }
}
