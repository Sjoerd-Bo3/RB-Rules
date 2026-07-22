using Microsoft.AspNetCore.Http;
using RbRules.Api.Endpoints;
using RbRules.Infrastructure;

namespace RbRules.Tests;

public class AiConfigAdminEndpointsTests
{
    [Theory]
    [InlineData(AiControlFailure.InvalidRequest, StatusCodes.Status400BadRequest)]
    [InlineData(AiControlFailure.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(AiControlFailure.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(AiControlFailure.Unavailable, StatusCodes.Status503ServiceUnavailable)]
    public void FailureMapping_UsesStableStatusWithoutUpstreamBody(
        AiControlFailure failure, int expectedStatus)
    {
        var result = AiConfigAdminEndpoints.ToResult(
            AiControlResult<AiControlSnapshot>.Failed(failure));

        Assert.Equal(expectedStatus,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        Assert.DoesNotContain("upstream", value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SuccessfulCreate_Returns201WithOnlyTypedValue()
    {
        var pool = new AiPoolView(
            "pool-1", "codex-sdk", "Codex", true, 10, 1,
            "managed", true, 1, 1, "ready");

        var result = AiConfigAdminEndpoints.ToResult(
            AiControlResult<AiPoolView>.Success(pool), created: true);

        Assert.Equal(StatusCodes.Status201Created,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Same(pool, Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
    }

    [Fact]
    public void DeleteSuccess_Returns204()
    {
        var result = AiConfigAdminEndpoints.ToNoContent(
            AiControlResult<bool>.Success(true));

        Assert.Equal(StatusCodes.Status204NoContent,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }
}
