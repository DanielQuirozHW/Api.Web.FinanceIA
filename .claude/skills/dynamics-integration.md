# Dynamics 365 Integration — Api.Web.ServiciosFinancieros

> **STATUS: template legacy. Not auto-imported by CLAUDE.md — load only when you are
> actually working on ConsultaFetchController or SearchController.**
>
> The mechanics below (OData, MSAL, TenantContext credentials) are still accurate for those
> two controllers. The premise is not: **business data does NOT live in Dynamics 365.**
> Azure SQL is the system of record — see CLAUDE.md > Architecture and
> financia-architecture.md. Dynamics is DI-registered and health-checked but no FinancIA
> business feature calls it.

## Overview
Dynamics 365 is reached via the OData v9.x REST API
using OAuth 2.0 client credentials flow (machine-to-machine).
Credentials are sourced exclusively from TenantContext.

## Architecture

### Two-Layer Design
Layer 1 — Token Acquisition (IDynamicsTokenService)
  Responsible for acquiring and caching OAuth tokens via MSAL.
  One instance per request, credentials from TenantContext.

Layer 2 — HTTP Client (IDynamicsClient)  
  Responsible for all HTTP communication with Dynamics 365.
  Uses IHttpClientFactory — never new HttpClient().
  Receives access token from IDynamicsTokenService.
  Throws typed exceptions — never returns null silently.

### File Locations
Servicios/
├── IDynamicsTokenService.cs   ← MSAL token acquisition
├── DynamicsTokenService.cs    ← implementation
├── IDynamicsClient.cs         ← Dynamics HTTP operations interface
└── DynamicsClient.cs          ← implementation

## IDynamicsTokenService

### Responsibilities
- Acquire OAuth access token using MSAL client credentials flow
- Use MSAL built-in token cache — never acquire new token on every call
- Source all credentials from TenantContext exclusively
- Never store tokens in DB or distributed cache
- Log acquisition success with tenantId and token expiry
- Never log the token value itself

### MSAL Configuration
Authority : https://login.microsoftonline.com/{TenantContext.AzureAdTenantId}
ClientId  : TenantContext.DynamicsClientId
Secret    : TenantContext.DynamicsClientSecret (decrypted at runtime)
Scope     : {TenantContext.DynamicsUrl}/.default
Builder   : ConfidentialClientApplicationBuilder per request

### Error Handling
- MsalException → throw DynamicsAuthException
- MsalServiceException (wrong credentials) → throw DynamicsAuthException
- Any other exception → log + throw DynamicsAuthException

### Interface Definition
public interface IDynamicsTokenService
{
    Task<string> AcquireTokenAsync(
        TenantContext tenantContext,
        CancellationToken cancellationToken);
}

## IDynamicsClient

### Responsibilities
- Execute all HTTP operations against Dynamics 365 OData API
- Use IHttpClientFactory — never instantiate HttpClient directly
- Add required OData headers on every request
- Handle Dynamics error responses and map to typed exceptions
- Support FetchXML queries with automatic pagination
- Support documentbody (attachment) retrieval

### Standard HTTP Headers (every request)
OData-MaxVersion        : 4.0
OData-Version           : 4.0
Accept                  : application/json
Prefer                  : odata.include-annotations=*

### API Version Strategy
Use v9.2 for all endpoints:
  api/data/v9.2/{entityName}
  api/data/v9.2/{entityName}({id})
  api/data/v9.2/{entityName}?fetchXml={encodedFetchXml}

### Timeout Configuration
Default timeout    : 30 seconds (configurable in appsettings)
FetchXML queries   : 2 minutes (configurable in appsettings)
Create/Update ops  : 10 minutes (configurable in appsettings)
Never hardcode TimeSpan values — always read from IOptions<DynamicsOptions>

### Interface Definition
public interface IDynamicsClient
{
    Task<string> CreateRecordAsync(
        string entityName,
        JsonObject entity,
        TenantContext tenantContext,
        CancellationToken cancellationToken);

    Task UpdateRecordAsync(
        string entityName,
        string entityId,
        JsonObject entity,
        TenantContext tenantContext,
        CancellationToken cancellationToken);

    Task<JsonArray> RetrieveMultipleAsync(
        string entityName,
        string fetchXml,
        TenantContext tenantContext,
        CancellationToken cancellationToken);

    Task<JsonObject?> RetrieveSingleAsync(
        string entityName,
        string entityId,
        IReadOnlyCollection<string> selectedAttributes,
        TenantContext tenantContext,
        CancellationToken cancellationToken);

    Task<JsonObject?> RetrieveWithFetchAsync(
        string entityName,
        string fetchXml,
        TenantContext tenantContext,
        CancellationToken cancellationToken);
}

## HTTP Client Factory Registration
Named client registered in Program.cs:

builder.Services.AddHttpClient("DynamicsClient", client =>
{
    client.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
    client.DefaultRequestHeaders.Add("OData-Version", "4.0");
    client.DefaultRequestHeaders.Accept
        .Add(new MediaTypeWithQualityHeaderValue("application/json"));
});

DynamicsClient resolves named client per request:
var httpClient = _httpClientFactory.CreateClient("DynamicsClient");
httpClient.BaseAddress = new Uri(tenantContext.DynamicsUrl);
httpClient.Timeout = TimeSpan.FromSeconds(_options.DefaultTimeoutSeconds);

## Dynamics Error Response Handling

### Error Response Format from Dynamics
{
  "error": {
    "code": "0x80040265",
    "message": "The field value is not valid."
  }
}

### Parsing Error Responses
Always parse error response body before throwing:

private async Task<string> ExtractDynamicsErrorMessageAsync(
    HttpResponseMessage response,
    CancellationToken cancellationToken)
{
    try
    {
        var content = await response.Content
            .ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(content);

        if (doc.RootElement.TryGetProperty("error", out var error) &&
            error.TryGetProperty("message", out var message))
        {
            return message.GetString() ?? "Unknown Dynamics error";
        }

        return "Unknown Dynamics error";
    }
    catch
    {
        return "Unable to parse Dynamics error response";
    }
}

### HTTP Status to Exception Mapping
Dynamics 400 → DynamicsBusinessException
  Parse error message from response body
  Sanitize before returning to client

Dynamics 401/403 → DynamicsAuthException
  Token may have expired mid-session
  Log with tenantId + endpoint

Dynamics 404 → DynamicsNotFoundException
  Entity or record does not exist
  Include entityName and entityId in exception

Dynamics 429 → DynamicsUnavailableException
  Rate limited by Dynamics
  Log with tenantId + retry guidance

Dynamics 5xx → DynamicsUnavailableException
  Dynamics service error
  Log with tenantId + status code

HttpRequestException (network) → DynamicsUnavailableException
TaskCanceledException (timeout) → DynamicsTimeoutException

### Never throw generic Exception
Always map to typed exception from error-handling skill.
Never use: throw new Exception(message)
Always use: throw new DynamicsBusinessException(message, tenantId)

## FetchXML Pagination

### Pagination Strategy
- Always use automatic pagination in RetrieveMultipleAsync
- Page size: 250 records (configurable via DynamicsOptions)
- Continue fetching while Morerecords == true
- Pass paging cookie between requests
- Merge all pages into single JsonArray result

### Paging Cookie Processing
Always apply this exact sequence when processing paging cookie:
1. HttpUtility.HtmlDecode(rawCookie)
2. Parse as XDocument
3. Extract pagingcookie attribute from root
4. HttpUtility.UrlDecode twice (double-encoded by Dynamics)
5. HttpUtility.HtmlEncode before embedding in next FetchXML

### FetchXML Template Constants
private const string FetchTemplate =
    "<fetch page='{0}' paging-cookie='' mapping='logical' " +
    "output-format='xml-platform' version='1.0' distinct='false'>" +
    "{1}</fetch>";

private const string FetchTemplatePaginated =
    "<fetch page='{0}' mapping='logical' output-format='xml-platform' " +
    "version='1.0' distinct='false' paging-cookie='{1}'>" +
    "{2}</fetch>";

### Pagination Loop Pattern
var allRecords = new JsonArray();
var currentPage = 1;
var hasMoreRecords = true;
string? pagingCookie = null;

while (hasMoreRecords)
{
    var (records, moreRecords, nextCookie) =
        await FetchPageAsync(
            entityName,
            fetchXml,
            currentPage,
            pagingCookie,
            httpClient,
            cancellationToken);

    foreach (var record in records)
        allRecords.Add(record?.DeepClone());

    hasMoreRecords = moreRecords;
    pagingCookie = nextCookie;
    currentPage++;
}

return allRecords;

## DocumentBody (Attachment) Handling

### Special Case
When FetchXML contains "documentbody" attribute:
1. Extract annotationid from FetchXML conditions
2. If annotationid found: retrieve single annotation record directly
3. Retrieve documentbody separately via single record endpoint
4. Store base64 value temporarily
5. Execute FetchXML query normally for metadata
6. Inject documentbody into first result record

### Detection Pattern
private static bool ContainsDocumentBody(string fetchXml)
    => fetchXml.Contains(
        "documentbody",
        StringComparison.OrdinalIgnoreCase);

private static string? ExtractAnnotationId(string fetchXml)
{
    var xmlDoc = new XmlDocument();
    xmlDoc.LoadXml(fetchXml);
    var node = xmlDoc.SelectSingleNode(
        "//condition[@attribute='annotationid']");
    return node?.Attributes?["value"]?.Value;
}

## DynamicsOptions Configuration

### Options Class
public sealed class DynamicsOptions
{
    public int DefaultTimeoutSeconds { get; init; } = 30;
    public int FetchXmlTimeoutSeconds { get; init; } = 120;
    public int WriteTimeoutSeconds { get; init; } = 600;
    public int PageSize { get; init; } = 250;
    public string ApiVersion { get; init; } = "v9.2";
}

### appsettings.json
{
  "Dynamics": {
    "DefaultTimeoutSeconds": 30,
    "FetchXmlTimeoutSeconds": 120,
    "WriteTimeoutSeconds": 600,
    "PageSize": 250,
    "ApiVersion": "v9.2"
  }
}

### Registration in Program.cs
builder.Services.Configure<DynamicsOptions>(
    builder.Configuration.GetSection("Dynamics"));

## JSON Library
Always use System.Text.Json exclusively.
Never use Newtonsoft.Json.
Serializer options for Dynamics responses:
private static readonly JsonSerializerOptions DynamicsJsonOptions = new()
{
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true
};

## Logging Standards for Dynamics Operations
Always log these events:

Operation started (Debug):
_logger.LogDebug(
    "Dynamics {Operation} started. " +
    "Entity: {EntityName} | TenantId: {TenantId}",
    operation, entityName, tenantContext.TenantId);

Operation succeeded (Information):
_logger.LogInformation(
    "Dynamics {Operation} succeeded. " +
    "Entity: {EntityName} | TenantId: {TenantId} | " +
    "RecordId: {RecordId}",
    operation, entityName, tenantContext.TenantId, recordId);

Operation failed (Warning/Error — see error-handling skill):
_logger.LogError(ex,
    "Dynamics {Operation} failed. " +
    "Entity: {EntityName} | TenantId: {TenantId} | " +
    "StatusCode: {StatusCode}",
    operation, entityName, tenantContext.TenantId, statusCode);

Pagination progress (Debug):
_logger.LogDebug(
    "Dynamics pagination. Entity: {EntityName} | " +
    "Page: {Page} | Records: {Count} | " +
    "TenantId: {TenantId}",
    entityName, page, records.Count, tenantContext.TenantId);

Never log:
- FetchXML content (may contain sensitive filter values)
- Entity field values
- Access tokens
- Request/response bodies

## Review Checklist for Dynamics Integration
When reviewing any Dynamics-related file verify:
- [ ] No new HttpClient() — always IHttpClientFactory
- [ ] No ADAL — always MSAL via IDynamicsTokenService
- [ ] No .Result or .GetAwaiter().GetResult() — always await
- [ ] No Newtonsoft.Json — always System.Text.Json
- [ ] No generic Exception thrown — always typed DynamicsException
- [ ] No Credenciales passed as parameter — always TenantContext
- [ ] No hardcoded timeouts — always DynamicsOptions
- [ ] No hardcoded API version — always DynamicsOptions.ApiVersion
- [ ] No null returned silently on error — always throw typed exception
- [ ] CancellationToken passed to every async call
- [ ] TenantId in every log statement
- [ ] Paging cookie double-decoded before use
- [ ] DocumentBody handled separately when present in FetchXML