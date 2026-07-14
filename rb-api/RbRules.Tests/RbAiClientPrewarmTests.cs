using Microsoft.Extensions.Logging.Abstractions;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Voorverwarmsignaal (#154): PrewarmAsync is best-effort en mag
/// het request-pad nooit raken — geen exceptions, geen lange wachttijden.</summary>
public class RbAiClientPrewarmTests
{
    [Fact]
    public async Task PrewarmAsync_PostNaarPrewarmEndpoint()
    {
        HttpRequestMessage? seen = null;
        var client = new RbAiClient(
            new HttpClient(new StubHandler(req =>
            {
                seen = req;
                return new HttpResponseMessage(System.Net.HttpStatusCode.Accepted);
            }))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);

        await client.PrewarmAsync();

        Assert.NotNull(seen);
        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Equal("/prewarm", seen.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task PrewarmAsync_UitvalIsVolledigStil()
    {
        var client = new RbAiClient(
            new HttpClient(new ThrowingHandler(() => new HttpRequestException("rb-ai plat")))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);

        // Geen exception — degradatie is het verwachte pad.
        await client.PrewarmAsync();
    }

    [Fact]
    public async Task PrewarmAsync_OokAnnuleringWordtGeslikt()
    {
        // Anders dan Ask*: zelfs een geannuleerde paginalaad mag geen
        // OperationCanceledException het request-pad in duwen.
        using var cts = new CancellationTokenSource();
        var client = new RbAiClient(
            new HttpClient(new ThrowingHandler(() =>
            {
                cts.Cancel();
                return new OperationCanceledException(cts.Token);
            }))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);

        await client.PrewarmAsync(cts.Token);
    }

    [Fact]
    public async Task PrewarmAsync_HangendeRbAiWordtOp2sGekapt()
    {
        // De interne CancelAfter(2s) is de kap; hier alleen verifiëren dat
        // een handler die op het token wacht netjes (stil) eindigt zodra de
        // aanroeper zelf annuleert — de kap deelt datzelfde mechanisme.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var client = new RbAiClient(
            new HttpClient(new HangingHandler())
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);

        await client.PrewarmAsync(cts.Token);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private sealed class ThrowingHandler(Func<Exception> exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(exception());
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.Accepted);
        }
    }
}
