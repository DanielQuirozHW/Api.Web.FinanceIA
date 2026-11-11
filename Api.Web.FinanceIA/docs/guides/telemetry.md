# Telemetría — Application Insights

## Qué problema resuelve

Hasta este cambio, producción **no tenía telemetría**. El proyecto referenciaba
`Microsoft.ApplicationInsights.AspNetCore` 3.1.0, pero nada lo registraba. Y como la app trae el SDK,
el agente "codeless" de App Service (el que se activa al vincular el recurso de Application Insights)
**se retira**. Resultado: `traces`, `requests` y `exceptions` vacíos, y cada `ILogger` de producción
perdido, salvo lo poco que llegaba al Log stream de App Service.

## Cómo está cableado

Todo vive en `Clases/Telemetry/TelemetryRegistration.cs`, llamado una vez desde `Program.cs`
(`builder.AddFinanciaTelemetry()`). No cambia el orden de los middlewares.

- **SDK 3.x = OpenTelemetry por debajo.** `AddApplicationInsightsTelemetry()` registra:
  - el trazado de ASP.NET Core (→ `requests`);
  - HttpClient y SqlClient (→ `dependencies`: Anthropic, BCRA, LUFE, NOSIS, CASFOG, ARCA y la base SQL);
  - el proveedor de logging de OpenTelemetry (→ `traces`, y `exceptions` cuando el log trae excepción);
  - el exportador de Azure Monitor.
- **Sólo se enciende con connection string.** El 3.x **tira una excepción** si no la tiene, así que
  sin ella no se registra nada y la app corre exactamente como antes (local, tests, CI). Al arrancar
  escribe una sola línea Information:
  - `Application Insights telemetry is off: no APPLICATIONINSIGHTS_CONNECTION_STRING configured`
  - o `Application Insights telemetry is on (…)`

  La connection string nunca se loguea.
- **De dónde sale.** Sólo de configuración, en el orden en que la lee el propio SDK:
  1. el App Setting `APPLICATIONINSIGHTS_CONNECTION_STRING`, que es el que pone App Service al vincular el recurso;
  2. `ApplicationInsights:ConnectionString`.

  **Nunca** en `appsettings.json`.
- **Sin muestreo.** Los defaults del 3.x tienen dos trampas:
  - un muestreo por tasa de 5 trazas/s;
  - un "trace-based logs sampler" que descarta **todos** los logs de un request muestreado afuera,
    Warnings y Errors incluidos.

  Acá van `SamplingRatio = 1` y `EnableTraceBasedLogsSampler = false`: no se descarta nada. Se
  pueden cambiar con App Settings (ver abajo), y el valor del operador gana.
- **Niveles de log.** Se toman de `Logging:OpenTelemetry:LogLevel`, en `appsettings.json`:
  - `Default` = Warning
  - `Api.Web.FinanceIA` = Information
  - `Microsoft` = Warning
  - `System` = Warning

  Cada uno se puede sobrescribir con un App Setting.
- **TenantId.** Cada request con tenant resuelto lleva `customDimensions.TenantId`. Sirve tanto si
  vino del header `X-Tenant-ID` como del JWT, porque se lee de `HttpContext.Items["TenantContext"]`
  al cerrar el request.

### Qué NO sale nunca

| Dato | Cómo se evita |
|---|---|
| Query strings (NOSIS `?Documento=<CUIT>`, Identity Platform `?key=`) | La redacción de OpenTelemetry queda forzada encendida y, además, se descarta el query entero de requests y dependencias. |
| CUIT / DNI en el path (BCRA `Deudas/{cuit}`, LUFE `entidades/{cuit}`, CASFOG `central_deudores/{cuit}`, `api/entities/cuit/{cuit}`, `api/bcra/deudores/{cuit}`) | Todo segmento con 7 o más dígitos seguidos pasa a `{id}` (`UrlRedaction`). |
| Valores de parámetros SQL | Forzado apagado. El texto de la consulta sí va, pero EF parametriza todo y no hay SQL crudo en el repo. |
| Headers, cuerpos de request y respuesta | Ninguna instrumentación los captura por defecto, y nada acá los enciende. |
| Connection strings, tokens | No se loguean (regla del proyecto); la connection string de Application Insights tampoco. |

`--verify-schema` corre **antes** de construir el host web, así que nunca registra telemetría y sale
igual que antes, aunque la variable esté definida (hay un test que lo verifica).

## App Settings por cliente

La lista de clientes es `.github/clients.json`. En **cada** App Service:

| Cliente | App Service | App Setting obligatorio |
|---|---|---|
| sgr-portales | `api-sgr-portales-analisisderiesgo` | `APPLICATIONINSIGHTS_CONNECTION_STRING` |
| movilsgr | `api-portales-analisisderiesgo-movilsgr` | `APPLICATIONINSIGHTS_CONNECTION_STRING` |
| neuquenpymessgr | `api-portales-analisisderiesgo-neuquenpymessgr` | `APPLICATIONINSIGHTS_CONNECTION_STRING` |

Si el App Service ya está vinculado a un recurso de Application Insights (Portal → App Service →
*Application Insights*), el setting **ya existe**. Lo único que hay que hacer es verificarlo en
*Configuration → Application settings*. Si no existe, se copia la connection string del recurso
(*Overview → Connection String*).

Opcionales, sólo si hace falta cambiar el default:

| App Setting | Default | Para qué |
|---|---|---|
| `ApplicationInsights__SamplingRatio` | `1` | Fracción de trazas enviadas (0–1). |
| `ApplicationInsights__EnableTraceBasedLogsSampler` | `false` | `true` descarta los logs de un request muestreado afuera. No recomendado. |
| `Logging__OpenTelemetry__LogLevel__Api.Web.FinanceIA` | `Information` | Nivel de nuestros logs (p. ej. `Debug` para diagnosticar). |
| `Logging__OpenTelemetry__LogLevel__Default` | `Warning` | Nivel del resto de las categorías. |

Cambiar un App Setting reinicia la app; no hace falta un deploy.

## Qué ver después del deploy

En unos minutos, en el recurso de Application Insights de cada cliente:

- **`traces`**: la línea `Application Insights telemetry is on`, y después los logs Information+ de
  `Api.Web.FinanceIA.*`.
- **`requests`**:
  - un registro por llamada a la API, con nombre de ruta (`GET api/analyses/{id}`);
  - `customDimensions.TenantId` cuando hay tenant;
  - `{id}` en lugar de CUITs en la URL;
  - sin query string.
- **`dependencies`**: llamadas a `api.anthropic.com`, `api.bcra.gob.ar`,
  `legajounicoapi.produccion.gob.ar`, NOSIS, CASFOG, los servicios de ARCA, y la base SQL.
- **`exceptions`**: lo que loguea `GlobalExceptionHandler`.

### KQL: logs por día y severidad

```kusto
traces
| where timestamp > ago(7d)
| summarize count() by bin(timestamp, 1d), severityLevel
| order by timestamp asc, severityLevel asc
```

`severityLevel` va de 0 (Verbose) a 4 (Critical): 1 Information, 2 Warning, 3 Error. Antes de este
cambio, la consulta no devolvía nada.

### KQL: respuestas de error de Claude

```kusto
traces
| where timestamp > ago(7d) and severityLevel >= 2
| where message startswith "Claude" or message startswith "Scoring narrative" or message startswith "Scan orientation"
     or message has "returned HTTP"
| extend StatusCode = toint(customDimensions.StatusCode),
         ErrorType  = tostring(customDimensions.ErrorType),
         RequestId  = tostring(customDimensions.RequestId),
         TenantId   = tostring(customDimensions.TenantId)
| where isnotnull(StatusCode)
| project timestamp, cloud_RoleName, StatusCode, ErrorType, RequestId, TenantId, message
| order by timestamp desc
```

Y las llamadas fallidas vistas como dependencia:

```kusto
dependencies
| where timestamp > ago(7d) and target has "api.anthropic.com" and success == false
| summarize count() by resultCode, bin(timestamp, 1d)
```

## El motivo de un 4xx de Claude, también en la base

Antes, `ApiCallLogs.ErrorMessage` guardaba sólo `Claude API error: HTTP 400.`. Ahora todos los
caminos de Claude que escriben `ApiCallLogs` (extracción y su escalado, sugerencia de corrección,
orientación de scans, chat y legajo) guardan lo que dice el sobre de error de Anthropic, leído por
`Servicios/AnthropicErrorDiagnostics.cs`:

```
HTTP 400 invalid_request_error: <mensaje, hasta 300 caracteres> [req_…]
```

- **Sólo esas partes.** El tipo de error (`error.type`), el mensaje (sin caracteres de control,
  recortado a 300) y el request id (header `request-id`, o `request_id` del cuerpo). Nunca el cuerpo
  crudo, nunca nada del request (el documento).
- **Los caminos que guardan un código corto lo mantienen como prefijo**: orientación, legajo y
  sugerencia, p. ej. `claude_api_error | HTTP 400 …` o `http_400 | Claude API error: HTTP 400 …`.
- **El chat guarda ahora el motivo interno** (`DiagnosticMessage`). Antes guardaba el texto genérico
  que ve el cliente.
- **El mensaje al cliente no cambia en ningún caso.**
- **La narrativa del scoring** no escribe fila cuando falla (nunca la escribió). Su motivo va en el
  Warning `Scoring narrative request failed: …`, que ahora llega a Application Insights.
- **En los logs** van el status, el tipo y el request id como campos estructurados. El mensaje va
  también en extracción, chat y narrativa, pero no en legajo, sugerencia ni orientación: ahí el
  mensaje podría repetir parte del request, así que queda sólo en la fila de `ApiCallLogs`.

Para ver los últimos motivos en una base Financia:

```sql
SELECT TOP 50 CalledAt, TenantId, Service, Operation, Model, ResponseStatus, ErrorMessage
FROM dbo.ApiCallLogs
WHERE IsSuccess = 0 AND Service LIKE 'Claude%'
ORDER BY CalledAt DESC;
```

Con el request id, soporte de Anthropic puede encontrar la llamada exacta.
