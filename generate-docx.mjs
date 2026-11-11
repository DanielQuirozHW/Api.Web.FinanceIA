import {
  Document, Packer, Paragraph, TextRun, HeadingLevel,
  Table, TableRow, TableCell, WidthType,
  AlignmentType, BorderStyle, Header, Footer,
  PageNumber, convertInchesToTwip, ShadingType,
  LevelFormat
} from './node_modules/docx/dist/index.mjs';
import { writeFileSync } from 'fs';

const BLUE       = "1F4E79";
const LIGHT_BLUE = "2E75B6";
const CODE_BG    = "F2F2F2";
const TH_BG      = "2E75B6";
const TR_ALT     = "DEEAF1";

const h1 = (text) => new Paragraph({
  text, heading: HeadingLevel.HEADING_1, pageBreakBefore: true,
  spacing: { after: 240 }, style: "Heading1"
});
const h2 = (text) => new Paragraph({
  text, heading: HeadingLevel.HEADING_2,
  spacing: { before: 240, after: 160 }, style: "Heading2"
});
const body = (text) => new Paragraph({
  children: [new TextRun({ text, font: "Calibri", size: 22 })],
  spacing: { after: 120 },
});
const bold = (text) => new Paragraph({
  children: [new TextRun({ text, font: "Calibri", size: 22, bold: true })],
  spacing: { after: 120 },
});
const bullet = (text) => new Paragraph({
  children: [new TextRun({ text, font: "Calibri", size: 22 })],
  bullet: { level: 0 }, spacing: { after: 80 },
});
const numbered = (text) => new Paragraph({
  children: [new TextRun({ text, font: "Calibri", size: 22 })],
  numbering: { reference: "num1", level: 0 }, spacing: { after: 80 },
});
const sp = () => new Paragraph({ text: "", spacing: { after: 120 } });

const code = (lines) => lines.split('\n').map(line =>
  new Paragraph({
    children: [new TextRun({ text: line || " ", font: "Courier New", size: 18, color: "333333" })],
    spacing: { after: 0, line: 240 },
    shading: { type: ShadingType.CLEAR, fill: CODE_BG }
  })
);

const tbl = (headers, rows) => {
  const hrow = new TableRow({
    tableHeader: true,
    children: headers.map(h => new TableCell({
      children: [new Paragraph({
        children: [new TextRun({ text: h, bold: true, color: "FFFFFF", font: "Calibri", size: 20 })],
        alignment: AlignmentType.CENTER,
      })],
      shading: { type: ShadingType.CLEAR, fill: TH_BG },
      margins: { top: 80, bottom: 80, left: 100, right: 100 }
    }))
  });
  const drows = rows.map((row, i) => new TableRow({
    children: row.map(cell => new TableCell({
      children: [new Paragraph({ children: [new TextRun({ text: cell, font: "Calibri", size: 20 })] })],
      shading: { type: ShadingType.CLEAR, fill: i % 2 === 1 ? TR_ALT : "FFFFFF" },
      margins: { top: 60, bottom: 60, left: 100, right: 100 }
    }))
  }));
  return new Table({ width: { size: 100, type: WidthType.PERCENTAGE }, rows: [hrow, ...drows] });
};

// ── Cover ──────────────────────────────────────────────────────────────────────
const cover = [
  sp(), sp(), sp(), sp(),
  new Paragraph({
    children: [new TextRun({ text: "Api.Web.ServiciosFinancieros", bold: true, size: 64, color: BLUE, font: "Calibri" })],
    alignment: AlignmentType.CENTER, spacing: { after: 240 },
  }),
  new Paragraph({
    children: [new TextRun({ text: "Documentación Técnica — Template Multi-Tenant BFF", size: 36, color: LIGHT_BLUE, font: "Calibri" })],
    alignment: AlignmentType.CENTER, spacing: { after: 480 },
  }),
  tbl(["Campo","Valor"], [
    ["Versión","1.0"],
    ["Fecha","22 de abril de 2026"],
    ["Autor","[Equipo de Desarrollo]"],
    ["Estado","Draft"],
    ["Clasificación","Uso Interno"],
  ]),
  sp(), sp(),
  new Paragraph({
    children: [new TextRun({ text: "© 2026 — Todos los derechos reservados", size: 18, color: "888888", font: "Calibri" })],
    alignment: AlignmentType.CENTER,
  }),
];

// ── Ch 1 ───────────────────────────────────────────────────────────────────────
const ch1 = [
  h1("1. RESUMEN EJECUTIVO"),
  h2("1.1 Propósito del Proyecto"),
  body("Api.Web.ServiciosFinancieros es un template de API multi-tenant tipo BFF (Backend for Frontend) diseñado para aplicaciones de servicios financieros. Proporciona una base sólida y segura para construir APIs que manejan múltiples clientes (tenants) de forma aislada, se integran con Firebase Authentication y Microsoft Dynamics 365, y cumplen con estándares de seguridad enterprise."),
  body("El objetivo principal es reducir el tiempo de bootstrap de nuevos proyectos de APIs financieras, garantizando desde el inicio una arquitectura correcta, patrones de seguridad aplicados y las integraciones enterprise ya configuradas."),
  h2("1.2 Beneficios Clave"),
  bullet("Seguridad por diseño: Aislamiento de tenants, encriptación AES-256, JWT con claims obligatorios, reCAPTCHA V3."),
  bullet("Multi-tenancy real: Cada tenant tiene su propio proyecto Firebase, instancia Dynamics 365 y credenciales aisladas."),
  bullet("Listo para Azure: App Service, SQL Database, Application Insights — todo pre-configurado."),
  bullet("Database First: Sin migraciones EF Core. El DBA controla el esquema con DDL scripts."),
  bullet("Código limpio y mantenible: Estándares .NET 8, sealed classes, records, nullable reference types."),
  bullet("Extensible: Patrón BFF que facilita agregar endpoints, servicios y repositorios nuevos."),
  h2("1.3 Resumen del Tech Stack"),
  tbl(["Capa","Tecnología","Versión"], [
    ["Framework","ASP.NET Core","8.0"],
    ["Lenguaje","C#","12"],
    ["Base de Datos","Azure SQL Server + EF Core","8.0"],
    ["Autenticación","Firebase Admin SDK","3.x"],
    ["Sesión","JWT Bearer (HMAC-SHA256)","—"],
    ["MSAL","Microsoft.Identity.Client","4.x"],
    ["API Externa","Dynamics 365 OData","v9.2"],
    ["Bot Protection","Google reCAPTCHA V3","—"],
    ["Encriptación","AES-256-CBC","—"],
    ["Telemetría","Azure Application Insights","—"],
    ["CI/CD","GitHub Actions","—"],
  ]),
  sp(),
  h2("1.4 Arquitectura de Alto Nivel"),
  body("El sistema sigue el patrón BFF: un único punto de entrada HTTP que orquesta autenticación, resolución de contexto de tenant y comunicación con servicios backend (Firebase, Dynamics 365). Cada request es procesado de forma aislada por tenant antes de llegar al controlador."),
  body("La base de datos SQL Server centralizada almacena únicamente configuración de tenants y logs de errores. Los datos de negocio residen exclusivamente en Dynamics 365, accedidos por tenant mediante credenciales OAuth aisladas."),
];

// ── Ch 2 ───────────────────────────────────────────────────────────────────────
const ch2 = [
  h1("2. ARQUITECTURA DEL SISTEMA"),
  h2("2.1 Patrón BFF (Backend for Frontend)"),
  body("El patrón Backend for Frontend (BFF) consiste en crear una capa de API dedicada para cada tipo de cliente frontend. A diferencia de una API genérica, el BFF está optimizado para las necesidades del frontend: agrega llamadas a múltiples servicios backend, transforma datos al formato exacto que necesita el cliente y gestiona autenticación y sesión."),
  body("En este template, el BFF: centraliza la validación del token Firebase y la emisión del JWT interno; protege endpoints anónimos con reCAPTCHA V3; orquesta llamadas a Dynamics 365 OData, agregando resultados cuando es necesario; y aísla completamente al frontend de la complejidad de MSAL, OData y el modelo de Dynamics."),
  h2("2.2 Multi-Tenancy"),
  body("La arquitectura multi-tenant se basa en el header HTTP X-Tenant-ID, presente en cada request. Este identificador determina qué configuración, credenciales Firebase y credenciales Dynamics 365 se utilizan para ese request."),
  bold("Flujo de resolución de tenant:"),
  ...code(`1. Request llega con header: X-Tenant-ID: "tenant-abc"
2. TenantResolutionMiddleware valida el formato del header
3. Consulta IMemoryCache con clave "tenant:tenant-abc"
4. Cache MISS -> Carga Credenciales + TenantFirebaseConfig desde SQL Server
5. Desencripta campos sensibles en memoria (AES-256-CBC)
6. Construye TenantContext inmutable y guarda en caché (sliding 30 min)
7. Almacena TenantContext en HttpContext.Items["TenantContext"]
8. El request continúa al middleware de autenticación`),
  sp(),
  tbl(["Componente","Aislamiento","Implementación"], [
    ["Firebase","Proyecto Firebase propio","Named FirebaseApp instance por tenantId"],
    ["Dynamics 365","Instancia y credenciales propias","TenantContext.DynamicsUrl + ClientId/Secret"],
    ["Azure AD","Tenant Azure AD propio","TenantContext.AzureAdTenantId"],
    ["reCAPTCHA","Secret key propia por tenant","TenantContext.RecaptchaSecretKey"],
    ["JWT","Claim tenantId en cada token","TokenService valida tenantId vs TenantContext"],
  ]),
  sp(),
  h2("2.3 Componentes Principales"),
  bold("TenantResolutionMiddleware"),
  body("Middleware que se ejecuta antes de la autenticación en cada request. Valida el header X-Tenant-ID, carga la configuración del tenant desde SQL Server o caché, desencripta credenciales sensibles y almacena el TenantContext en HttpContext.Items. Lanza TenantResolutionException (400) o TenantNotFoundException (404) si el tenant es inválido."),
  bold("FirebaseAuthenticationHandler"),
  body("Handler de autenticación ASP.NET Core que valida tokens Firebase ID. Utiliza el TenantContext para obtener la instancia FirebaseApp específica del tenant. Nunca usa FirebaseApp.DefaultInstance. Registra el UID de Firebase en los claims del ClaimsPrincipal."),
  bold("TokenService"),
  body("Servicio que emite JWTs internos (HMAC-SHA256) después de autenticación Firebase exitosa. Incluye claims obligatorios: tenantId, sub, azp, iat, scope, roles, end_user_uid y end_user_email. Configurable en lifetime por ambiente."),
  bold("DynamicsClient"),
  body("Cliente HTTP para Dynamics 365 OData v9.2. Usa IHttpClientFactory. Soporta CRUD, consultas FetchXML con paginación automática y recuperación de documentbody. Mapea errores HTTP de Dynamics a excepciones tipadas."),
  bold("GlobalExceptionHandler"),
  body("Implementa IExceptionHandler de ASP.NET Core 8. Captura todas las excepciones no manejadas del pipeline. Siempre responde con RFC 7807 ProblemDetails. Integra IErrorLogService para persistir errores en SQL Server."),
  bold("EncryptionService"),
  body("Servicio singleton que implementa AES-256-CBC. La clave AES proviene del environment variable ENCRYPTION_MASTER_KEY. Nunca cachea valores desencriptados. Lanza EncryptionException en caso de fallo."),
  h2("2.4 Stack Tecnológico"),
  tbl(["Categoría","Tecnología","Propósito"], [
    ["Framework","ASP.NET Core 8 (.NET 8.0)","API HTTP, DI, Middleware pipeline"],
    ["Lenguaje","C# 12","Sealed classes, primary ctors, records"],
    ["ORM","Entity Framework Core 8","Database First — solo lectura/escritura"],
    ["Base de Datos","Azure SQL Server","Credenciales y config de tenants"],
    ["Auth externa","Firebase Admin SDK 3.x","Validación tokens Firebase por tenant"],
    ["Auth interna","JWT Bearer (HMAC-SHA256)","Sesión post-login (15 min)"],
    ["MSAL","Microsoft.Identity.Client 4.x","OAuth client credentials para Dynamics"],
    ["Dynamics 365","OData v9.2 REST API","Todos los datos de negocio"],
    ["Bot protection","Google reCAPTCHA V3","Endpoints anónimos"],
    ["Encriptación","AES-256-CBC","Credenciales en reposo"],
    ["Caché","IMemoryCache","Config de tenants (in-process)"],
    ["Telemetría","Azure Application Insights","Logs, traces, métricas"],
    ["Docs API","Swashbuckle (Swagger)","Solo en Development"],
    ["CI/CD","GitHub Actions","Build, test, deploy a Azure"],
  ]),
  sp(),
  h2("2.5 Decisiones de Diseño Clave"),
  bold("Database First — Sin migraciones EF Core"),
  body("El esquema es controlado por el equipo de DBA mediante DDL scripts en /docs/database/. EF Core mapea las tablas existentes pero nunca las crea ni modifica. Garantiza control total del schema y sin riesgo de migraciones automáticas en producción."),
  bold("Named FirebaseApp instances (nunca DefaultInstance)"),
  body("Cada tenant tiene su propia instancia FirebaseApp nombrada con el tenantId. Usar FirebaseApp.DefaultInstance crearía una condición de carrera y permitiría que tokens de un tenant sean validados contra el proyecto Firebase de otro."),
  bold("MSAL en lugar de ADAL"),
  body("Microsoft.Identity.Client (MSAL) es la librería moderna y soportada de Microsoft. ADAL está en desuso desde 2022. MSAL provee caché de tokens incorporado y soporte para Azure AD v2 endpoints."),
  bold("RFC 7807 ProblemDetails"),
  body("Todos los errores de API usan el estándar RFC 7807 (application/problem+json). Facilita el consumo de errores por frontends, es interoperable con herramientas de monitoreo y nunca expone detalles internos al cliente."),
];

// ── Ch 3 ───────────────────────────────────────────────────────────────────────
const ch3 = [
  h1("3. FLUJOS DE AUTENTICACIÓN"),
  h2("3.1 Flujo Firebase → Internal JWT"),
  body("Este flujo se usa cuando un usuario ya autenticado en el portal necesita intercambiar su Firebase ID Token por un JWT interno de la API."),
  numbered("El usuario inicia sesión en el portal con Firebase Authentication (email/password, Google, etc.)."),
  numbered("El portal obtiene el Firebase ID Token del SDK de Firebase."),
  numbered("El portal envía POST /api/bff/auth/token con el Firebase ID Token en Authorization: Bearer y el header X-Tenant-ID."),
  numbered("TenantResolutionMiddleware resuelve el TenantContext para el tenantId especificado."),
  numbered("FirebaseAuthenticationHandler valida el token contra la instancia FirebaseApp del tenant (no la Default)."),
  numbered("Si el token es válido, el handler crea el ClaimsPrincipal con el UID de Firebase."),
  numbered("BffController invoca TokenService.IssueInternalJwtAsync(tenantContext, firebaseUid, email, scopes, roles)."),
  numbered("TokenService genera el JWT interno firmado con HMAC-SHA256, lifetime de 15 minutos."),
  numbered("El response incluye el JWT en el body y en una httpOnly cookie (Cache-Control: no-store)."),
  sp(),
  h2("3.2 Flujo Onboarding Anónimo"),
  body("Este flujo permite a usuarios sin cuenta obtener un JWT limitado para completar el proceso de registro, protegido por reCAPTCHA V3."),
  numbered("El portal envía POST /api/bff/auth/onboarding-token con el header X-Tenant-ID y el token reCAPTCHA V3 en el body."),
  numbered("TenantResolutionMiddleware resuelve el TenantContext y provee la RecaptchaSecretKey del tenant."),
  numbered("RecaptchaVerifier valida el token reCAPTCHA contra la API de Google usando la secret key del tenant."),
  numbered("Si el score es menor a 0.5, se retorna 403 Forbidden con ProblemDetails."),
  numbered("Si el score es aceptable, TokenService.IssueOnboardingJwtAsync() emite el JWT limitado."),
  numbered("El JWT de onboarding tiene scope onboarding_anon, lifetime de 10 minutos y claims mínimos."),
  sp(),
  h2("3.3 Estructura de los JWTs"),
  bold("Internal JWT — Claims"),
  tbl(["Claim","Tipo","Descripción","Ejemplo"], [
    ["tenantId","string","ID del tenant resuelto","banco-abc"],
    ["sub","string","Firebase UID del usuario","firebase|uid123"],
    ["azp","string","Authorized party (cliente OAuth)","portal-web"],
    ["iat","int","Issued at (Unix timestamp)","1714000000"],
    ["exp","int","Expiry (iat + 900 seg)","1714000900"],
    ["scope","string","Scopes separados por espacio","api:read api:write"],
    ["roles","array","Roles asignados","[\"user\", \"admin\"]"],
    ["end_user_uid","string","Firebase UID (on-behalf-of flow)","firebase|uid123"],
    ["end_user_email","string","Email del usuario","user@example.com"],
  ]),
  sp(),
  bold("Onboarding JWT — Claims"),
  tbl(["Claim","Tipo","Descripción","Ejemplo"], [
    ["tenantId","string","ID del tenant","banco-abc"],
    ["sub","string","Identificador anónimo temporal","anon:uuid4"],
    ["azp","string","Authorized party","portal-web"],
    ["iat","int","Issued at","1714000000"],
    ["exp","int","Expiry (iat + 600 seg)","1714000600"],
    ["scope","string","Scope limitado de onboarding","onboarding_anon"],
  ]),
  sp(),
  tbl(["Token","Lifetime Default","Config Key"], [
    ["Internal JWT","15 minutos","Jwt:ExpirationMinutes"],
    ["Onboarding JWT","10 minutos","Jwt:OnboardingExpirationMinutes"],
  ]),
];

// ── Ch 4 ───────────────────────────────────────────────────────────────────────
const ch4 = [
  h1("4. SEGURIDAD"),
  h2("4.1 Encriptación de Credenciales"),
  body("Todos los campos sensibles almacenados en SQL Server están encriptados con AES-256-CBC. La desencriptación ocurre en memoria en cada lectura; nunca se cachean valores desencriptados."),
  tbl(["Tabla","Campo","Descripción"], [
    ["Credenciales","clientsecret","Client Secret de la App Registration en Azure AD"],
    ["TenantFirebaseConfig","CredentialJson","JSON de la Service Account de Firebase"],
    ["TenantFirebaseConfig","RecaptchaSecretKey","Secret key de reCAPTCHA V3"],
  ]),
  sp(),
  bold("Gestión de la clave AES:"),
  bullet("Variable de entorno: ENCRYPTION_MASTER_KEY"),
  bullet("Local: dotnet user-secrets"),
  bullet("Azure: Application Settings / Key Vault"),
  bullet("Nunca en appsettings.json ni en el repositorio"),
  bullet("Rotación: Re-encriptar todos los campos con la nueva clave antes de actualizar la variable"),
  sp(),
  h2("4.2 Aislamiento de Tenants"),
  tbl(["Capa","Mecanismo de Aislamiento"], [
    ["Header","X-Tenant-ID validado en cada request antes de cualquier otra operación"],
    ["Firebase","Named FirebaseApp instance por tenantId — nunca DefaultInstance"],
    ["JWT interno","Claim tenantId verificado contra TenantContext.TenantId en cada request"],
    ["Dynamics 365","Credenciales OAuth separadas por tenant (ClientId/Secret/TenantId)"],
    ["reCAPTCHA","Secret key separada por tenant"],
    ["Caché","Cache key incluye tenantId: 'tenant:{tenantId}'"],
  ]),
  sp(),
  h2("4.3 JWT Signing"),
  tbl(["Parámetro","Valor"], [
    ["Algoritmo","HMAC-SHA256"],
    ["Longitud mínima clave","256 bits (32 bytes)"],
    ["Almacenamiento clave","user-secrets / Azure App Settings"],
    ["Lifetime default","15 minutos"],
    ["Validación","Issuer, Audience, Expiry, tenantId claim"],
  ]),
  sp(),
  h2("4.4 reCAPTCHA V3"),
  tbl(["Parámetro","Valor"], [
    ["Score mínimo aceptable","0.5 (configurable por tenant)"],
    ["Acción al fallar","403 Forbidden + ProblemDetails"],
    ["Secret key","Por tenant — TenantContext.RecaptchaSecretKey"],
    ["Endpoint de validación","https://www.google.com/recaptcha/api/siteverify"],
  ]),
  sp(),
  h2("4.5 CORS"),
  body("CORS está configurado con soporte para credenciales (httpOnly cookies). Los orígenes permitidos se configuran explícitamente por ambiente en appsettings.json. Nunca se usa AllowAnyOrigin() en producción."),
  ...code(`// appsettings.json
"Cors": {
  "AllowedOrigins": [
    "https://portal.banco-abc.com",
    "https://admin.banco-abc.com"
  ]
}`),
  sp(),
  h2("4.6 Gestión de Secrets"),
  tbl(["Ambiente","Mecanismo","Descripción"], [
    ["Local Development","dotnet user-secrets","Almacenado en perfil de usuario, nunca en repo"],
    ["Staging","Azure App Service App Settings","Variables de entorno del App Service"],
    ["Production","Azure App Service App Settings","Variables de entorno del App Service"],
    ["Opcional","Azure Key Vault","Para rotación automática de secretos"],
  ]),
];

// ── Ch 5 ───────────────────────────────────────────────────────────────────────
const ch5 = [
  h1("5. INTEGRACIÓN CON DYNAMICS 365"),
  h2("5.1 Autenticación MSAL"),
  body("La autenticación con Dynamics 365 usa el flujo OAuth 2.0 Client Credentials (machine-to-machine) mediante MSAL (Microsoft.Identity.Client). El API se autentica directamente usando su App Registration en Azure AD."),
  ...code(`var app = ConfidentialClientApplicationBuilder
    .Create(tenantContext.DynamicsClientId)
    .WithClientSecret(tenantContext.DynamicsClientSecret)
    .WithAuthority($"https://login.microsoftonline.com/{tenantContext.AzureAdTenantId}")
    .Build();

var result = await app.AcquireTokenForClient(
    new[] { $"{tenantContext.DynamicsUrl}/.default" })
    .ExecuteAsync(cancellationToken);`),
  sp(),
  tbl(["Parámetro MSAL","Fuente"], [
    ["ClientId","TenantContext.DynamicsClientId"],
    ["ClientSecret","TenantContext.DynamicsClientSecret (desencriptado en runtime)"],
    ["Authority","https://login.microsoftonline.com/{TenantContext.AzureAdTenantId}"],
    ["Scope","{TenantContext.DynamicsUrl}/.default"],
  ]),
  sp(),
  h2("5.2 Operaciones OData v9.2"),
  bold("Headers estándar (cada request):"),
  ...code(`OData-MaxVersion: 4.0
OData-Version: 4.0
Accept: application/json
Prefer: odata.include-annotations=*
Authorization: Bearer {accessToken}`),
  sp(),
  tbl(["Método","Descripción"], [
    ["CreateRecordAsync","POST a /api/data/v9.2/{entityName}"],
    ["UpdateRecordAsync","PATCH a /api/data/v9.2/{entityName}({id})"],
    ["RetrieveSingleAsync","GET con $select de campos específicos"],
    ["RetrieveMultipleAsync","FetchXML con paginación automática (250 registros/página)"],
    ["RetrieveWithFetchAsync","FetchXML para registro único con documentbody opcional"],
  ]),
  sp(),
  h2("5.3 Timeouts Configurables"),
  tbl(["Operación","Timeout Default","Config Key"], [
    ["GET / Consultas simples","30 segundos","Dynamics:DefaultTimeoutSeconds"],
    ["FetchXML / Paginación","2 minutos","Dynamics:FetchXmlTimeoutSeconds"],
    ["Create / Update","10 minutos","Dynamics:WriteTimeoutSeconds"],
  ]),
  sp(),
  h2("5.4 Manejo de Errores Dynamics"),
  tbl(["HTTP Status","Excepción","Descripción"], [
    ["400","DynamicsBusinessException","Violación de regla de negocio"],
    ["401 / 403","DynamicsAuthException","Token expirado o credenciales inválidas"],
    ["404","DynamicsNotFoundException","Entidad o registro no existe"],
    ["429","DynamicsUnavailableException","Rate limit excedido"],
    ["5xx","DynamicsUnavailableException","Error en servicio Dynamics"],
    ["Timeout","DynamicsTimeoutException","Timeout de la operación"],
    ["Network error","DynamicsUnavailableException","Error de red HTTP"],
  ]),
];

// ── Ch 6 ───────────────────────────────────────────────────────────────────────
const ch6 = [
  h1("6. BASE DE DATOS"),
  h2("6.1 Database First — Sin Migraciones"),
  body("El proyecto usa un enfoque Database First estricto. EF Core se usa exclusivamente para acceso a datos — nunca para crear ni modificar el esquema. Garantiza que el equipo de DBA tiene control total y los cambios pasan por proceso de revisión formal."),
  bullet("NUNCA ejecutar dotnet ef migrations add"),
  bullet("NUNCA ejecutar dotnet ef database update"),
  bullet("Los cambios de esquema se realizan mediante DDL scripts ejecutados por el DBA"),
  sp(),
  h2("6.2 Esquema de Tablas"),
  bold("Tabla: Credenciales"),
  tbl(["Columna","Tipo","Descripción"], [
    ["id","int (PK, identity)","Identificador interno"],
    ["cliente","varchar(100) UNIQUE","ID del tenant — valor del header X-Tenant-ID"],
    ["url","varchar(500)","URL base de Dynamics 365 del tenant"],
    ["clientid","varchar(200)","Client ID de la App Registration Azure AD"],
    ["clientsecret","varchar(MAX)","Client Secret encriptado AES-256"],
    ["tenantid","varchar(100)","Azure AD Tenant GUID"],
  ]),
  sp(),
  bold("Tabla: TenantFirebaseConfig"),
  tbl(["Columna","Tipo","Descripción"], [
    ["Id","int (PK, identity)","Identificador interno"],
    ["TenantId","varchar(100) UNIQUE","Referencia al Cliente en Credenciales"],
    ["ProjectId","varchar(200)","ID del proyecto Firebase"],
    ["CredentialJson","varchar(MAX)","JSON Service Account Firebase — encriptado"],
    ["RecaptchaSecretKey","varchar(500)","Secret key reCAPTCHA V3 — encriptada"],
  ]),
  sp(),
  bold("Tabla: ErrorLogs"),
  tbl(["Columna","Tipo","Descripción"], [
    ["ErrorId","int (PK, identity)","Identificador del log"],
    ["Timestamp","datetime","Fecha/hora UTC del error"],
    ["Level","varchar(50)","Warning / Error / Critical"],
    ["Message","nvarchar(MAX)","Descripción segura del error"],
    ["ExceptionDetails","nvarchar(MAX)","Tipo de excepción y errorCode"],
    ["Source","varchar(255)","TenantId"],
    ["Url","varchar(2048)","Request path"],
    ["UserId","varchar(255)","end_user_uid del JWT"],
    ["IPAddress","varchar(45)","IP del cliente"],
    ["StackTrace","nvarchar(MAX)","Stack trace completo"],
    ["TraceId","varchar(100)","Correlation ID"],
  ]),
  sp(),
  h2("6.3 Índices"),
  tbl(["Tabla","Índice","Tipo","Propósito"], [
    ["Credenciales","IX_Credenciales_Cliente","UNIQUE","Lookup por tenantId"],
    ["TenantFirebaseConfig","IX_TenantFirebaseConfig_TenantId","UNIQUE","Lookup por tenantId"],
    ["TenantFirebaseConfig","IX_TenantFirebaseConfig_ProjectId","UNIQUE","Evitar proyectos Firebase duplicados"],
    ["ErrorLogs","IX_ErrorLogs_Timestamp","Non-unique","Queries de logs por fecha"],
    ["ErrorLogs","IX_ErrorLogs_TraceId","Non-unique","Lookup por correlation ID"],
  ]),
];

// ── Ch 7 ───────────────────────────────────────────────────────────────────────
const ch7 = [
  h1("7. MANEJO DE ERRORES"),
  h2("7.1 Jerarquía de Excepciones"),
  body("Todas las excepciones de dominio heredan de AppException. Las de servicios externos heredan de ExternalServiceException, que agrega el nombre del servicio."),
  tbl(["Excepción","HTTP","errorCode","Log Level","Capa"], [
    ["TenantResolutionException","400","tenant_resolution_error","Warning","Middleware"],
    ["TenantNotFoundException","404","tenant_not_found","Warning","Middleware"],
    ["TenantConfigException","500","tenant_config_error","Error","Middleware"],
    ["FirebaseAuthException","401","firebase_auth_error","Warning","Auth Handler"],
    ["TokenGenerationException","500","token_generation_error","Error","TokenService"],
    ["DynamicsAuthException","502","dynamics_auth_error","Error","DynamicsTokenService"],
    ["DynamicsNotFoundException","404","dynamics_not_found","Warning","DynamicsClient"],
    ["DynamicsBusinessException","422","dynamics_business_error","Warning","DynamicsClient"],
    ["DynamicsUnavailableException","503","dynamics_unavailable","Error","DynamicsClient"],
    ["DynamicsTimeoutException","504","dynamics_timeout","Error","DynamicsClient"],
    ["FirebaseServiceException","502","firebase_service_error","Error","Servicios"],
    ["BusinessException","422","business_error","Warning","Clases/"],
    ["ValidationException","400","validation_error","Warning","Clases/"],
    ["DatabaseException","500","database_error","Error","Repositories"],
    ["EncryptionException","500","encryption_error","Error","EncryptionService"],
    ["RecaptchaException","502","recaptcha_error","Error","RecaptchaVerifier"],
    ["Exception (unhandled)","500","internal_error","Critical","GlobalExceptionHandler"],
  ]),
  sp(),
  h2("7.2 GlobalExceptionHandler"),
  body("El GlobalExceptionHandler implementa IExceptionHandler de ASP.NET Core 8 y es el último punto de captura. Garantiza que el cliente nunca reciba un error sin formato o con información sensible."),
  bold("Flujo de procesamiento:"),
  numbered("Extrae tenantId de HttpContext.Items y traceId de Activity.Current."),
  numbered("Extrae userId e ipAddress del ClaimsPrincipal y HttpContext.Connection."),
  numbered("Verifica si la excepción es subtype de AppException."),
  numbered("Usa exception.HttpStatusCode y exception.ErrorCode para el response."),
  numbered("Log al nivel definido por el tipo de excepción con ILogger<T>."),
  numbered("Persiste en ErrorLogs vía IErrorLogService."),
  numbered("Retorna ProblemDetails sanitizado — nunca detalles internos."),
  sp(),
  bold("Formato de respuesta ProblemDetails (RFC 7807):"),
  ...code(`{
  "type": "https://serviciosfinancieros.api/errors/{errorCode}",
  "title": "Descripción del error",
  "status": 400,
  "detail": "Mensaje seguro para el cliente",
  "instance": "/api/bff/auth/token",
  "errorCode": "tenant_not_found",
  "traceId": "00-abc123-def456-00"
}`),
  sp(),
  h2("7.3 Estrategia de Logging"),
  bullet("SIEMPRE incluir TenantId en cada log entry."),
  bullet("SIEMPRE incluir TraceId (correlation ID)."),
  bullet("NUNCA loguear: tokens, passwords, secrets, connection strings, PII."),
  bullet("NUNCA loguear el contenido de FetchXML ni valores de campos de entidades."),
  bullet("NUNCA usar Console.Write o Debug.Write — siempre ILogger<T>."),
];

// ── Ch 8 ───────────────────────────────────────────────────────────────────────
const ch8 = [
  h1("8. GUÍA DE SETUP"),
  h2("8.1 Pre-requisitos"),
  tbl(["Herramienta","Versión mínima","Notas"], [
    [".NET SDK","8.0","https://dotnet.microsoft.com/download"],
    ["Visual Studio","2022 17.8+","O VS Code con extensión C#"],
    ["SQL Server","2019+","Local o Azure SQL Database"],
    ["Firebase Project","—","console.firebase.google.com"],
    ["reCAPTCHA V3 Site","—","www.google.com/recaptcha"],
    ["Azure AD App Reg","—","Para Dynamics 365 (si aplica)"],
  ]),
  sp(),
  h2("8.2 Instalación Paso a Paso"),
  numbered("Clonar el repositorio:"),
  ...code(`git clone [repository-url]
cd Api.Web.ServiciosFinancieros`),
  sp(),
  numbered("Inicializar user-secrets:"),
  ...code(`cd Api.Web.ServiciosFinancieros
dotnet user-secrets init`),
  sp(),
  numbered("Configurar secrets:"),
  ...code(`dotnet user-secrets set "ConnectionStrings:Default" "Server=...;Database=ServiciosFinancieros;..."
dotnet user-secrets set "Jwt:SigningKey" "your-256-bit-minimum-secret-key-here"
dotnet user-secrets set "ENCRYPTION_MASTER_KEY" "your-aes-256-master-key"`),
  sp(),
  numbered("Ejecutar scripts DDL en SQL Server (ver /docs/database/)."),
  sp(),
  numbered("Encriptar credenciales del primer tenant:"),
  ...code(`cd EncryptionHelper
dotnet run -- encrypt "MiClientSecret"
# Output: ENCRYPTED:base64encodedvalue==`),
  sp(),
  numbered("Insertar el tenant en base de datos:"),
  ...code(`INSERT INTO Credenciales (cliente, url, clientid, clientsecret, tenantid)
VALUES ('banco-abc',
        'https://banco-abc.crm.dynamics.com',
        'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx',
        'ENCRYPTED:...',
        'yyyyyyyy-yyyy-yyyy-yyyy-yyyyyyyyyyyy');`),
  sp(),
  numbered("Build y run:"),
  ...code(`dotnet restore && dotnet build
dotnet run --launch-profile https`),
  sp(),
  numbered("Verificar en https://localhost:7xxx/swagger"),
  sp(),
  h2("8.3 Configuración Completa de Secrets"),
  tbl(["Secret Key","Descripción","Ejemplo"], [
    ["ConnectionStrings:Default","Connection string SQL Server","Server=...;Database=...;Encrypt=True;"],
    ["Jwt:SigningKey","HMAC-SHA256 signing key","minimo-32-caracteres-aleatorios"],
    ["Jwt:ExpirationMinutes","Lifetime JWT interno","15"],
    ["Jwt:Issuer","Issuer del JWT","https://api.serviciosfinancieros.com"],
    ["Jwt:Audience","Audience del JWT","serviciosfinancieros-clients"],
    ["ENCRYPTION_MASTER_KEY","AES-256 master key","base64-encoded-32-byte-key"],
    ["ApplicationInsights:ConnectionString","App Insights conn string","InstrumentationKey=xxx;..."],
  ]),
  sp(),
  h2("8.4 Tenant Seeding con EncryptionHelper"),
  body("El proyecto EncryptionHelper/ es una CLI .NET para encriptar secretos antes de insertarlos en base de datos. El valor resultante (con prefijo ENCRYPTED:) se inserta directamente en las columnas clientsecret, CredentialJson y RecaptchaSecretKey."),
];

// ── Ch 9 ───────────────────────────────────────────────────────────────────────
const ch9 = [
  h1("9. GUÍA DE DEPLOYMENT (AZURE)"),
  h2("9.1 Recursos Azure Requeridos"),
  tbl(["Recurso","SKU Recomendado","Propósito"], [
    ["Azure App Service","B2 / P1v3","Hosting de la API (.NET 8, Windows)"],
    ["Azure SQL Database","S2 / GP_Gen5_2","Base de datos de configuración"],
    ["Application Insights","Pay-as-you-go","Telemetría y logs"],
    ["Key Vault (opcional)","Standard","Rotación automática de secretos"],
    ["Azure AD App Registration","—","Una por tenant para Dynamics 365"],
  ]),
  sp(),
  h2("9.2 Configuración del App Service"),
  bold("Application Settings (variables de entorno):"),
  ...code(`ASPNETCORE_ENVIRONMENT=Production
ENCRYPTION_MASTER_KEY=[valor]
ConnectionStrings__Default=[azure sql connection string]
Jwt__SigningKey=[jwt signing key]
ApplicationInsights__ConnectionString=[connection string]
Cors__AllowedOrigins__0=https://portal.tudominio.com`),
  sp(),
  body("En Azure App Service, los separadores : de la configuración .NET se reemplazan por __ (doble guión bajo) cuando se definen como Application Settings."),
  h2("9.3 CI/CD con GitHub Actions"),
  tbl(["Branch","Trigger","Acción"], [
    ["main","push","dotnet test -> deploy to Production"],
    ["develop","push","dotnet test -> deploy to Staging"],
    ["any","pull_request","dotnet build + dotnet test (sin deploy)"],
  ]),
  sp(),
  bold("Secrets requeridos en GitHub:"),
  ...code(`AZURE_WEBAPP_PUBLISH_PROFILE    # Perfil de publicación del App Service
ENCRYPTION_MASTER_KEY           # AES key para runtime
SQL_CONNECTION_STRING           # Connection string de producción`),
  sp(),
  bold("Workflow básico:"),
  ...code(`name: Deploy to Azure
on:
  push:
    branches: [main]
jobs:
  build-and-deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - run: dotnet restore
      - run: dotnet build --configuration Release
      - run: dotnet test --no-build
      - run: dotnet publish -c Release -o ./publish
      - uses: azure/webapps-deploy@v3
        with:
          app-name: api-servicios-financieros
          publish-profile: \${{ secrets.AZURE_WEBAPP_PUBLISH_PROFILE }}
          package: ./publish`),
];

// ── Ch 10 ──────────────────────────────────────────────────────────────────────
const ch10 = [
  h1("10. EXTENSIÓN DEL TEMPLATE"),
  h2("10.1 Agregar Nuevos Endpoints"),
  numbered("Crear el controller en Controllers/:"),
  ...code(`[ApiController]
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = "InternalJwt")]
public sealed class ClientesController : ControllerBase
{
    private readonly IDynamicsClient _dynamics;
    private readonly ITenantContextAccessor _tenantContextAccessor;

    public ClientesController(
        IDynamicsClient dynamics,
        ITenantContextAccessor tenantContextAccessor)
    {
        _dynamics = dynamics;
        _tenantContextAccessor = tenantContextAccessor;
    }

    [HttpGet]
    public async Task<IActionResult> GetClientesAsync(CancellationToken ct)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        var fetchXml = "<fetch><entity name='account'>...</entity></fetch>";
        var results = await _dynamics.RetrieveMultipleAsync(
            "accounts", fetchXml, tenantContext, ct);
        return Ok(results);
    }
}`),
  sp(),
  h2("10.2 Agregar Servicios de Negocio"),
  numbered("Definir la interfaz en Servicios/:"),
  ...code(`public interface IClienteService
{
    Task<IReadOnlyCollection<ClienteDto>> GetClientesActivosAsync(
        TenantContext tenantContext,
        CancellationToken cancellationToken);
}`),
  sp(),
  numbered("Registrar en Program.cs:"),
  ...code(`builder.Services.AddScoped<IClienteService, ClienteService>();`),
  sp(),
  h2("10.3 Testing"),
  body("El template está diseñado para ser testeable: inyección de dependencias mediante interfaces, TenantContext como parámetro (no desde HttpContext en servicios) y sin statics ni singletons de difícil mockeo."),
  ...code(`public class ClienteServiceTests
{
    [Fact]
    public async Task GetClientesActivosAsync_ReturnsMappedDtos()
    {
        var mockDynamics = new Mock<IDynamicsClient>();
        var tenantContext = new TenantContext { TenantId = "test-tenant" };

        mockDynamics
            .Setup(d => d.RetrieveMultipleAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                tenantContext, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonArray());

        var sut = new ClienteService(mockDynamics.Object, Mock.Of<ILogger<ClienteService>>());
        var result = await sut.GetClientesActivosAsync(tenantContext, CancellationToken.None);

        result.Should().NotBeNull();
    }
}`),
];

// ── Ch 11 ──────────────────────────────────────────────────────────────────────
const ch11 = [
  h1("11. TROUBLESHOOTING"),
  h2("Errores Comunes y Soluciones"),
  tbl(["Error","Causa","Solución"], [
    ["400 — tenant_resolution_error",
     "Header X-Tenant-ID ausente, vacío o con caracteres inválidos",
     "Verificar que el cliente envía el header. Solo alfanumérico y guiones, max 100 chars."],
    ["404 — tenant_not_found",
     "El valor de X-Tenant-ID no existe en la tabla Credenciales",
     "Verificar que el tenant fue insertado correctamente en la base de datos."],
    ["401 — firebase_auth_error",
     "Token Firebase expirado, inválido o de otro proyecto Firebase",
     "Verificar que el portal usa el proyecto Firebase correcto para este tenant."],
    ["500 — encryption_error",
     "ENCRYPTION_MASTER_KEY incorrecta o campos corruptos en BD",
     "Verificar que ENCRYPTION_MASTER_KEY es la misma que se usó para encriptar."],
    ["502 — dynamics_auth_error",
     "Client Secret expirado, ClientId incorrecto o permisos Azure AD insuficientes",
     "Verificar App Registration en Azure AD. Verificar que el secret no expiró."],
    ["504 — dynamics_timeout",
     "Query FetchXML muy compleja o Dynamics sobrecargado",
     "Revisar la query FetchXML. Agregar filtros. Aumentar FetchXmlTimeoutSeconds."],
    ["403 — reCAPTCHA score bajo",
     "Score menor a 0.5 — posible bot o entorno de pruebas",
     "En pruebas locales, bajar threshold a 0.0 en config. Verificar site key en producción."],
    ["Swagger no carga",
     "Swagger solo está habilitado en Development",
     "Verificar que ASPNETCORE_ENVIRONMENT=Development."],
    ["Config stale en caché",
     "La caché tiene sliding expiration de 30 minutos",
     "Reiniciar la aplicación o implementar cache invalidation por tenantId."],
  ]),
];

// ── Ch 12 / Appendices ─────────────────────────────────────────────────────────
const ch12 = [
  h1("12. APÉNDICES"),
  h2("Apéndice A: appsettings.json Completo"),
  ...code(`{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "ConnectionStrings": {
    "Default": "<<SET IN USER SECRETS OR ENV>>"
  },
  "Jwt": {
    "Issuer": "https://api.serviciosfinancieros.com",
    "Audience": "serviciosfinancieros-clients",
    "ExpirationMinutes": 15,
    "OnboardingExpirationMinutes": 10,
    "SigningKey": "<<SET IN USER SECRETS OR ENV>>"
  },
  "Cors": {
    "AllowedOrigins": [ "https://portal.tudominio.com" ]
  },
  "Dynamics": {
    "DefaultTimeoutSeconds": 30,
    "FetchXmlTimeoutSeconds": 120,
    "WriteTimeoutSeconds": 600,
    "PageSize": 250,
    "ApiVersion": "v9.2"
  },
  "Recaptcha": {
    "MinScore": 0.5,
    "VerifyUrl": "https://www.google.com/recaptcha/api/siteverify"
  },
  "TenantCache": {
    "SlidingExpirationMinutes": 30,
    "AbsoluteExpirationHours": 4
  },
  "ApplicationInsights": {
    "ConnectionString": "<<SET IN ENV>>"
  }
}`),
  sp(),
  h2("Apéndice B: Variables de Entorno para Producción"),
  tbl(["Variable","Descripción","Requerida"], [
    ["ASPNETCORE_ENVIRONMENT","Production","Sí"],
    ["ENCRYPTION_MASTER_KEY","AES-256 master key","Sí"],
    ["ConnectionStrings__Default","Azure SQL connection string","Sí"],
    ["Jwt__SigningKey","HMAC-SHA256 signing key","Sí"],
    ["Jwt__Issuer","JWT issuer URL","Sí"],
    ["Jwt__Audience","JWT audience","Sí"],
    ["ApplicationInsights__ConnectionString","App Insights connection string","Recomendada"],
    ["Cors__AllowedOrigins__0","Primer origen CORS permitido","Sí"],
  ]),
  sp(),
  h2("Apéndice C: Comandos Útiles"),
  bold("dotnet:"),
  ...code(`dotnet user-secrets list                     # Ver todos los secrets configurados
dotnet user-secrets set "Key" "Value"        # Establecer un secret
dotnet build --configuration Release         # Build de release
dotnet run --launch-profile https            # Ejecutar con HTTPS
dotnet test                                  # Ejecutar tests
dotnet publish -c Release -o ./publish       # Publicar para deploy`),
  sp(),
  bold("SQL Server:"),
  ...code(`-- Ver tenants configurados
SELECT cliente, url, clientid, tenantid FROM Credenciales;

-- Ver errores recientes
SELECT TOP 50 * FROM ErrorLogs ORDER BY Timestamp DESC;`),
  sp(),
  h2("Apéndice D: Referencias"),
  tbl(["Recurso","URL"], [
    ["Firebase Admin SDK .NET","https://firebase.google.com/docs/admin/setup"],
    ["MSAL .NET","https://learn.microsoft.com/en-us/azure/active-directory/develop/msal-net-overview"],
    ["Dynamics 365 OData v9.2","https://learn.microsoft.com/en-us/power-apps/developer/data-platform/webapi/overview"],
    ["reCAPTCHA V3","https://developers.google.com/recaptcha/docs/v3"],
    ["RFC 7807 ProblemDetails","https://datatracker.ietf.org/doc/html/rfc7807"],
    ["ASP.NET Core 8 Auth","https://learn.microsoft.com/en-us/aspnet/core/security/authentication"],
    ["EF Core Database First","https://learn.microsoft.com/en-us/ef/core/managing-schemas/scaffolding"],
    ["Azure App Service Deploy","https://learn.microsoft.com/en-us/azure/app-service/deploy-github-actions"],
  ]),
];

// ── Assemble ───────────────────────────────────────────────────────────────────
const doc = new Document({
  numbering: {
    config: [{
      reference: "num1",
      levels: [{
        level: 0,
        format: LevelFormat.DECIMAL,
        text: "%1.",
        alignment: AlignmentType.LEFT,
        style: {
          paragraph: { indent: { left: convertInchesToTwip(0.5), hanging: convertInchesToTwip(0.25) } }
        }
      }]
    }]
  },
  styles: {
    default: {
      document: { run: { font: "Calibri", size: 22 } }
    },
    paragraphStyles: [
      {
        id: "Heading1", name: "Heading1", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { size: 32, bold: true, color: BLUE, font: "Calibri" },
        paragraph: { spacing: { before: 480, after: 240 } }
      },
      {
        id: "Heading2", name: "Heading2", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { size: 28, bold: true, color: LIGHT_BLUE, font: "Calibri" },
        paragraph: { spacing: { before: 360, after: 160 } }
      },
    ]
  },
  sections: [{
    headers: {
      default: new Header({
        children: [new Paragraph({
          children: [new TextRun({ text: "Api.Web.ServiciosFinancieros — Documentación Técnica    v1.0    |    Uso Interno", font: "Calibri", size: 18, color: "888888" })],
          border: { bottom: { color: LIGHT_BLUE, space: 1, style: BorderStyle.SINGLE, size: 6 } }
        })]
      })
    },
    footers: {
      default: new Footer({
        children: [new Paragraph({
          children: [
            new TextRun({ text: "Página ", font: "Calibri", size: 18, color: "888888" }),
            new TextRun({ children: [PageNumber.CURRENT], font: "Calibri", size: 18, color: "888888" }),
            new TextRun({ text: " de ", font: "Calibri", size: 18, color: "888888" }),
            new TextRun({ children: [PageNumber.TOTAL_PAGES], font: "Calibri", size: 18, color: "888888" }),
            new TextRun({ text: "    |    © 2026 Api.Web.ServiciosFinancieros", font: "Calibri", size: 18, color: "AAAAAA" }),
          ],
          alignment: AlignmentType.RIGHT,
          border: { top: { color: LIGHT_BLUE, space: 1, style: BorderStyle.SINGLE, size: 6 } }
        })]
      })
    },
    children: [
      ...cover,
      ...ch1, ...ch2, ...ch3, ...ch4, ...ch5, ...ch6,
      ...ch7, ...ch8, ...ch9, ...ch10, ...ch11, ...ch12,
    ]
  }]
});

const out = "D:/Poyectos/source/repos/Api.Web.ServiciosFinancieros/Api.Web.ServiciosFinancieros/docs/Api.Web.ServiciosFinancieros-Documentacion-Tecnica.docx";
const buf = await Packer.toBuffer(doc);
writeFileSync(out, buf);
console.log("Done:", out, `(${(buf.length/1024).toFixed(1)} KB)`);
