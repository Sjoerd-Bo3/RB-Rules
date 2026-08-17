using System.Net;
using System.Net.Sockets;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Fetch-laag van de SSRF-guard (#45): een SocketsHttpHandler voor
/// HttpClients die uitsluitend met de buitenwereld praten (IngestService).
/// Niet gebruiken voor interne clients (rb-ai, Ollama) — die moeten juist
/// wél bij compose-interne adressen kunnen.
///
/// Wat deze handler afdekt, zonder de fetch-laag te herbouwen:
/// - ConnectCallback resolvet DNS en weigert geblokkeerde IP's op het moment
///   van verbinden — dus voor élk verbindingsdoel, óók redirect-hops, en
///   zonder check-dan-fetch-gat (DNS-rebinding tussen validatie en fetch).
///   Eén geblokkeerd adres in de DNS-respons weigert de hele host (een
///   mix van publieke en interne A-records is precies de rebinding-truc).
/// - MaxAutomaticRedirections begrenst redirect-kettingen; https→http-
///   downgrade weigert SocketsHttpHandler al uit zichzelf, dus redirects
///   blijven https.
///
/// Wat bewust NIET kan op deze laag: de hostnaam-regels van
/// <see cref="UrlGuard.Check"/> (geen single-label-namen, geen .local e.d.)
/// gelden alleen voor de start-URL — een redirect naar zo'n naam passeert
/// hier alleen de IP-check. Dat is afdoende: die namen resolven intern naar
/// privé-adressen en stranden dus alsnog. Per-hop URL-validatie zou een
/// eigen redirect-loop vergen (AllowAutoRedirect uit) en dat is de
/// complexiteit hier niet waard.</summary>
public static class SafeExternalHttp
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = UrlGuard.MaxRedirects,
        ConnectCallback = async (ctx, ct) =>
        {
            var host = ctx.DnsEndPoint.Host;
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            if (addresses.Length == 0 || addresses.Any(UrlGuard.IsBlockedIp))
                throw new HttpRequestException(
                    $"SSRF-guard: host '{host}' wijst naar een intern of privé IP-adres");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, ctx.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
