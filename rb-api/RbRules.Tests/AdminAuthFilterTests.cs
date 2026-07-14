using Microsoft.AspNetCore.Http;
using RbRules.Api;

namespace RbRules.Tests;

/// <summary>AdminAuthFilter.IsAdmin (#166): server-authoritatieve
/// beheerder-check, hergebruikt buiten /api/admin/* om autoriteit te bepalen
/// bij in-chat-rulings (verified vs pending). Leest alleen de echte
/// ADMIN_PASSWORD-omgevingsvariabele — nooit request-invoer.</summary>
public class AdminAuthFilterTests
{
    [Fact]
    public void JuisteXAdminKey_MetGezetWachtwoord_IsAdmin()
    {
        WithAdminPassword("geheim123", () =>
        {
            var http = HttpContextWith("geheim123");
            Assert.True(AdminAuthFilter.IsAdmin(http));
        });
    }

    [Fact]
    public void OnjuisteXAdminKey_IsGeenAdmin()
    {
        WithAdminPassword("geheim123", () =>
        {
            var http = HttpContextWith("verkeerd");
            Assert.False(AdminAuthFilter.IsAdmin(http));
        });
    }

    [Fact]
    public void OntbrekendeHeader_IsGeenAdmin()
    {
        WithAdminPassword("geheim123", () =>
        {
            var http = HttpContextWith(null);
            Assert.False(AdminAuthFilter.IsAdmin(http));
        });
    }

    [Fact]
    public void GeenAdminPasswordGezet_NooitAdmin_OokNietMetLegeHeader()
    {
        // ADMIN_PASSWORD ontbreekt = admin volledig vergrendeld (bestaand
        // gedrag van AdminAuthFilter) — óók als iemand toevallig een lege
        // header meestuurt.
        WithAdminPassword(null, () =>
        {
            var http = HttpContextWith("");
            Assert.False(AdminAuthFilter.IsAdmin(http));
        });
    }

    private static HttpContext HttpContextWith(string? adminKey)
    {
        var http = new DefaultHttpContext();
        if (adminKey is not null) http.Request.Headers["X-Admin-Key"] = adminKey;
        return http;
    }

    /// <summary>ADMIN_PASSWORD is proces-breed — try/finally zet 'm altijd
    /// terug (patroon AskServiceApproachTests.WithModeAsync), zodat een
    /// falende assertie de omgeving van andere tests niet vervuilt.</summary>
    private static void WithAdminPassword(string? value, Action body)
    {
        var previous = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
        Environment.SetEnvironmentVariable("ADMIN_PASSWORD", value);
        try
        {
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ADMIN_PASSWORD", previous);
        }
    }
}
