// Project : Api.Web.FinanceIA
// Created : 2026-05-12

using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using Api.Web.FinanceIA.Models.Arca;
using Microsoft.Extensions.Caching.Memory;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// WSAA authentication service for ARCA web services.
/// Generates TRA (Ticket Request Access), signs with X.509 certificate,
/// and caches tokens (valid ~12 hours).
/// </summary>
public sealed class ArcaWsaaService : IArcaWsaaService
{
    private readonly HttpClient                  _httpClient;
    private readonly ILogger<ArcaWsaaService>    _logger;
    private readonly IConfiguration              _configuration;
    private readonly IMemoryCache                _cache;

    public ArcaWsaaService(
        HttpClient                httpClient,
        ILogger<ArcaWsaaService>  logger,
        IConfiguration            configuration,
        IMemoryCache              cache)
    {
        _httpClient    = httpClient;
        _logger        = logger;
        _configuration = configuration;
        _cache         = cache;
    }

    /// <inheritdoc />
    public async Task<WsaaTicket> GetTicketAsync(
        string serviceId,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"arca:wsaa:ticket:{serviceId}";

        if (_cache.TryGetValue(cacheKey, out WsaaTicket? cached) && cached is not null)
        {
            if (cached.ExpirationTime > DateTime.UtcNow.AddMinutes(10))
            {
                _logger.LogDebug("WSAA ticket cache hit for service {ServiceId}", serviceId);
                return cached;
            }
        }

        _logger.LogInformation("Requesting new WSAA ticket for service {ServiceId}", serviceId);

        var ticket = await RequestTicketAsync(serviceId, cancellationToken)
            .ConfigureAwait(false);

        var cacheExpiration = ticket.ExpirationTime.AddMinutes(-10);
        _cache.Set(cacheKey, ticket, cacheExpiration);

        return ticket;
    }

    private async Task<WsaaTicket> RequestTicketAsync(
        string serviceId,
        CancellationToken cancellationToken)
    {
        // 1. Generate TRA XML
        var tra = GenerateTra(serviceId);

        // 2. Sign TRA with certificate (PKCS#7 / CMS)
        var signedTra = SignTra(tra, serviceId);
        string? varibale = _configuration["Arca:Services:ws_sr_constancia_inscripcion:CertificatePassword"];
        // 3. Call WSAA LoginCms endpoint
        var responseXml = await CallWsaaAsync(signedTra, cancellationToken)
            .ConfigureAwait(false);

        // 4. Parse the LoginTicketResponse
        return ParseLoginTicketResponse(responseXml);
    }

    private static string GenerateTra(string serviceId)
    {
        var now = DateTime.UtcNow;

        // ARCA expects Argentina time offset (-03:00)
        var generationTime = now.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:sszzz");
        var expirationTime = now.AddHours(12).ToString("yyyy-MM-ddTHH:mm:sszzz");
        var uniqueId       = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <loginTicketRequest version="1.0">
              <header>
                <uniqueId>{uniqueId}</uniqueId>
                <generationTime>{generationTime}</generationTime>
                <expirationTime>{expirationTime}</expirationTime>
              </header>
              <service>{serviceId}</service>
            </loginTicketRequest>
            """;
    }

    private string SignTra(string tra, string serviceId)
    {
        using var cert = LoadCertificate(serviceId);

        var traBytes = Encoding.UTF8.GetBytes(tra);

        var contentInfo = new ContentInfo(traBytes);
        var signedCms   = new SignedCms(contentInfo, false); // false = attached/encapsulated
        var signer      = new CmsSigner(cert)
        {
            IncludeOption   = X509IncludeOption.EndCertOnly,
            DigestAlgorithm = new System.Security.Cryptography.Oid("2.16.840.1.101.3.4.2.1") // SHA-256
        };

        signedCms.ComputeSignature(signer);
        var signedBytes = signedCms.Encode();

        return Convert.ToBase64String(signedBytes);
    }

    private X509Certificate2 LoadCertificate(string serviceId)
    {
        // Try service-specific config first: Arca:Services:{serviceId}:*
        var servicePrefix = $"Arca:Services:{serviceId}";
        var certBase64   = _configuration[$"{servicePrefix}:CertificateBase64"];
        var certPath     = _configuration[$"{servicePrefix}:CertificatePath"];
        var certPassword = _configuration[$"{servicePrefix}:CertificatePassword"];

        // Fallback to legacy keys: Arca:CertificateBase64 / Arca:CertificatePath
        if (string.IsNullOrEmpty(certBase64) && string.IsNullOrEmpty(certPath))
        {
            _logger.LogDebug(
                "No service-specific certificate for {ServiceId}, falling back to global Arca config",
                serviceId);

            certBase64   = _configuration["Arca:CertificateBase64"];
            certPath     = _configuration["Arca:CertificatePath"];
            certPassword = _configuration["Arca:CertificatePassword"];
        }

        if (!string.IsNullOrEmpty(certBase64))
        {
            _logger.LogDebug("Loading certificate from Base64 for service {ServiceId}", serviceId);
            var certBytes = Convert.FromBase64String(certBase64);

            if (string.IsNullOrEmpty(certPassword))
                throw new InvalidOperationException(
                    $"Certificate password not configured for service '{serviceId}'");

            return new X509Certificate2(certBytes, certPassword,
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
        }

        if (!string.IsNullOrEmpty(certPath))
        {
            _logger.LogDebug("Loading certificate from file {CertPath} for service {ServiceId}",
                certPath, serviceId);

            if (string.IsNullOrEmpty(certPassword))
                throw new InvalidOperationException(
                    $"Certificate password not configured for service '{serviceId}'");

            return new X509Certificate2(certPath, certPassword,
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
        }

        throw new InvalidOperationException(
            $"No certificate configured for ARCA service '{serviceId}'. " +
            $"Set Arca:Services:{serviceId}:CertificatePath or Arca:Services:{serviceId}:CertificateBase64");
    }

    private async Task<string> CallWsaaAsync(
        string signedTra,
        CancellationToken cancellationToken)
    {
        var soapEnvelope = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"
                              xmlns:wsaa="http://wsaa.view.sua.dvadac.desein.afip.gov">
              <soapenv:Body>
                <wsaa:loginCms>
                  <wsaa:in0>{signedTra}</wsaa:in0>
                </wsaa:loginCms>
              </soapenv:Body>
            </soapenv:Envelope>
            """;

        var req = new HttpRequestMessage(HttpMethod.Post, "");
        req.Content = new StringContent(soapEnvelope, Encoding.UTF8, "text/xml");
        req.Headers.TryAddWithoutValidation("SOAPAction", "\"\"");

        var response = await _httpClient
            .SendAsync(req, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            _logger.LogError(
                "WSAA login failed. Status={StatusCode} Body={FaultBody}",
                response.StatusCode, body);

            throw new HttpRequestException(
                $"WSAA login failed with status {response.StatusCode}");
        }

        return await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static WsaaTicket ParseLoginTicketResponse(string soapXml)
    {
        var doc = XDocument.Parse(soapXml);

        // The WSAA response contains the LoginTicketResponse as a string
        // inside the SOAP body: <loginCmsReturn>...</loginCmsReturn>
        var returnElement = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "loginCmsReturn")
            ?? throw new InvalidOperationException("WSAA response missing loginCmsReturn element");

        var ticketXml = XDocument.Parse(returnElement.Value);

        var credentials = ticketXml.Descendants("credentials").FirstOrDefault()
            ?? throw new InvalidOperationException("WSAA response missing credentials element");

        var token = credentials.Element("token")?.Value
            ?? throw new InvalidOperationException("WSAA response missing token");
        var sign = credentials.Element("sign")?.Value
            ?? throw new InvalidOperationException("WSAA response missing sign");

        var header = ticketXml.Descendants("header").FirstOrDefault();
        var expirationStr = header?.Element("expirationTime")?.Value;

        var expiration = DateTime.TryParse(expirationStr, out var exp)
            ? exp.ToUniversalTime()
            : DateTime.UtcNow.AddHours(11); // safe fallback

        return new WsaaTicket(token, sign, expiration);
    }
}
