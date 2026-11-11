using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Api.Web.ServiciosFinancieros.Clases;
using Api.Web.ServiciosFinancieros.Clases.Middleware;
using Api.Web.ServiciosFinancieros.Servicios.RateLimiting;
using Api.Web.ServiciosFinancieros.Data;
using Api.Web.ServiciosFinancieros.Data.Repositories;
using Api.Web.ServiciosFinancieros.Models;
using Api.Web.ServiciosFinancieros.Servicios;
using Api.Web.ServiciosFinancieros.Servicios.HealthChecks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ── MVC / Swagger ─────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── Singleton services ────────────────────────────────────────────────────────
builder.Services.AddSingleton<IEncryptionService, EncryptionService>();
builder.Services.AddSingleton<ITenantFirebaseRegistry, TenantFirebaseRegistry>();

// ── EF Core ───────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(cfg.GetConnectionString("Default"),
        sqlOptions => sqlOptions.EnableRetryOnFailure(
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromSeconds(5),
            errorNumbersToAdd: null))
    .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

// ── Scoped services ───────────────────────────────────────────────────────────
builder.Services.AddScoped<ICredencialRepository, CredencialRepository>();
builder.Services.AddScoped<ITenantFirebaseConfigRepository, TenantFirebaseConfigRepository>();
builder.Services.AddScoped<IErrorLogRepository, ErrorLogRepository>();
builder.Services.AddScoped<IErrorLogService, ErrorLogService>();
builder.Services.AddScoped<ITenantContextAccessor, TenantContextAccessor>();
builder.Services.AddScoped<ITenantResolver, TenantResolver>();
builder.Services.AddScoped<IFirebaseTokenValidator, FirebaseTokenValidator>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IOnboardingTokenIssuer, OnboardingTokenIssuer>();
builder.Services.AddScoped<IRecaptchaVerifier, RecaptchaVerifier>();
builder.Services.AddScoped<IDynamicsTokenService, DynamicsTokenService>();
builder.Services.AddScoped<IDynamicsClient, DynamicsClient>();
builder.Services.AddScoped<IUserContextResolver, FirestoreUserContextResolver>();

// ── Options ───────────────────────────────────────────────────────────────────
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));

// ── Named HTTP clients ────────────────────────────────────────────────────────
builder.Services.AddHttpClient("RecaptchaClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient("DynamicsClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

// ── Rate limiting ─────────────────────────────────────────────────────────────
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IRateLimitService, RateLimitService>();

// ── Health checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database")
    .AddCheck<DynamicsHealthCheck>("dynamics")
    .AddCheck<FirebaseHealthCheck>("firebase")
    .AddCheck<FirestoreHealthCheck>("firestore");

// ── Exception handling ────────────────────────────────────────────────────────
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// ── Authentication ────────────────────────────────────────────────────────────
var jwtConfig = builder.Configuration.GetSection("Jwt");
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = "SmartJwt";
        options.DefaultChallengeScheme    = "SmartJwt";
    })
    .AddScheme<AuthenticationSchemeOptions, FirebaseAuthenticationHandler>(
        FirebaseAuthenticationHandler.SchemeName, _ => { })
    .AddJwtBearer("Internal", o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer            = true,
            ValidIssuer               = jwtConfig["Issuer"],
            ValidateAudience          = true,
            ValidAudience             = jwtConfig["Audience"],
            ValidateLifetime          = true,
            ClockSkew                 = TimeSpan.FromMinutes(2),
            ValidateIssuerSigningKey  = true,
            IssuerSigningKey          = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtConfig["SigningKey"]!)),
            ValidAlgorithms           = new[] { SecurityAlgorithms.HmacSha256 }
        };
    })
    .AddPolicyScheme("SmartJwt", "Smart JWT Selector", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            var authHeader = context.Request.Headers.Authorization.ToString();
            if (string.IsNullOrWhiteSpace(authHeader) ||
                !authHeader.StartsWith("Bearer "))
            {
                return FirebaseAuthenticationHandler.SchemeName;
            }

            var token = authHeader.Substring("Bearer ".Length).Trim();
            try
            {
                var handler = new JwtSecurityTokenHandler();
                if (!handler.CanReadToken(token))
                {
                    return FirebaseAuthenticationHandler.SchemeName;
                }

                var jwtToken = handler.ReadJwtToken(token);
                var issuer = jwtToken.Issuer;

                if (issuer.Contains("firebase", StringComparison.OrdinalIgnoreCase) ||
                    issuer.Contains("securetoken.google.com"))
                {
                    return FirebaseAuthenticationHandler.SchemeName;
                }

                return "Internal";
            }
            catch
            {
                return FirebaseAuthenticationHandler.SchemeName;
            }
        };
    });

// ── CORS ──────────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
                builder.Configuration
                    .GetSection("Cors:AllowedOrigins")
                    .Get<string[]>() ?? Array.Empty<string>())
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials(); // required for httpOnly cookies
    });
});

// ─────────────────────────────────────────────────────────────────────────────
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.UseCors();
app.UseHttpsRedirection();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthentication();
app.UseMiddleware<TenantFromJwtMiddleware>();
app.UseMiddleware<RateLimitingMiddleware>();
app.UseAuthorization();
app.UseExceptionHandler(options => { });

app.MapControllers();

app.MapGet("/health", () => Results.Ok("Healthy"));

app.MapHealthChecks("/ready", new HealthCheckOptions
{
    ResponseWriter = HealthCheckResponseWriter.WriteAsync
});

app.Run();
