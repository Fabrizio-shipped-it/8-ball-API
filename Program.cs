using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using PoolManager.Data;
using PoolManager.Services;
using Microsoft.OpenApi;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using PoolManager.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// --- Servicios ---

// PostgreSQL + EF Core
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var useIamAuth = builder.Configuration.GetValue<bool>("AWS:UseIamAuth");

// Reintento ante fallas transitorias.
// Aurora Serverless puede tardar en despertar y una conexión puede cortarse por
// un failover. Sin esto, cualquiera de esas dos cosas se propaga como un 500 al
// cliente aunque el segundo intento hubiera funcionado.
static void ConfigurarReintentos(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder npgsql)
{
    npgsql.EnableRetryOnFailure(
        maxRetryCount: 3,
        maxRetryDelay: TimeSpan.FromSeconds(10),
        errorCodesToAdd: null);
}

if (useIamAuth)
{
    // En AWS: generar token IAM como contraseña (se renueva cada 14 min)
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
    dataSourceBuilder.UsePeriodicPasswordProvider(async (settings, ct) =>
    {
        return await Amazon.RDS.Util.RDSAuthTokenGenerator.GenerateAuthTokenAsync(
            settings.Host, settings.Port, settings.Username);
    }, TimeSpan.FromMinutes(14), TimeSpan.FromSeconds(10));
    var dataSource = dataSourceBuilder.Build();

    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(dataSource, ConfigurarReintentos));
}
else
{
    // En local: conexión normal con contraseña en la connection string
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(connectionString, ConfigurarReintentos));
}

builder.Services.AddScoped<PlayerService>();
builder.Services.AddScoped<MatchService>();
builder.Services.AddSingleton<S3Service>();


// Autenticación JWT con Keycloak.
//
// Keycloak se expone por una única dirección pública (HTTPS via Caddy), así que
// la URL con la que se descarga el discovery document y la que figura como
// emisor dentro del token son la misma. Cuando convivían una IP privada y una
// pública había que reescribir las URLs del discovery; eso ya no aplica.
var keycloakAuthority = builder.Configuration["Keycloak:Authority"]!;

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = keycloakAuthority;
        options.Audience = builder.Configuration["Keycloak:ClientId"];

        options.RequireHttpsMetadata = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidIssuer = keycloakAuthority,
            ValidAudience = builder.Configuration["Keycloak:ClientId"],
            RoleClaimType = ClaimTypes.Role
        };

        // Keycloak no emite los roles como claims planos: los mete anidados en
        // "realm_access": { "roles": [...] }. El middleware de .NET no sabe leer
        // esa estructura, así que sin este mapeo User.IsInRole("admin") siempre
        // da false y TODOS los endpoints [Authorize(Roles = "admin")] responden
        // 403, incluso para un admin legítimo.
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                if (context.Principal?.Identity is not ClaimsIdentity identity)
                    return Task.CompletedTask;

                var realmAccess = identity.FindFirst("realm_access")?.Value;
                if (string.IsNullOrWhiteSpace(realmAccess))
                    return Task.CompletedTask;

                try
                {
                    using var doc = JsonDocument.Parse(realmAccess);
                    if (doc.RootElement.TryGetProperty("roles", out var roles)
                        && roles.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var role in roles.EnumerateArray())
                        {
                            var name = role.GetString();
                            if (!string.IsNullOrWhiteSpace(name))
                                identity.AddClaim(new Claim(ClaimTypes.Role, name));
                        }
                    }
                }
                catch (JsonException)
                {
                    // Token con realm_access malformado: se sigue sin roles.
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// Controllers (MVC)
builder.Services.AddControllers();

// Formato único de error para toda la API: { "error": "..." }
//
// Los errores de validación y de deserialización JSON los produce el model binding,
// ANTES de entrar al controller, así que no se pueden atrapar con un try/catch.
// Por defecto ASP.NET devuelve un ProblemDetails que incluye el path del JSON,
// el número de línea y el tipo .NET esperado — información interna que no le
// sirve al cliente y que describe la implementación.
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var message = context.ModelState
            .Where(kv => kv.Value is not null && kv.Value.Errors.Count > 0)
            .SelectMany(kv => kv.Value!.Errors)
            .Select(e => e.ErrorMessage)
            // Un ErrorMessage vacío significa que falló la deserialización (hay
            // una Exception adjunta). Ese texto nunca se expone.
            .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
            ?? "El formato del request no es válido";

        return new BadRequestObjectResult(new { error = message });
    };
});

// Swagger
builder.Services.AddEndpointsApiExplorer();



builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "8-Ball Pool Manager API",
        Version = "v1",
        Description = "API para gestionar jugadores y partidas de pool"
    });

    const string securityScheme = "Bearer";

    options.AddSecurityDefinition(securityScheme, new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Ingresá el token JWT"
    });

    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
{
    {
        new OpenApiSecuritySchemeReference(securityScheme, document, null),
        new List<string>()
    }
    });
});



// Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddFixedWindowLimiter("general", opt =>
    {
        opt.PermitLimit = 100;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });

    // Límite más estricto para las operaciones de S3.
    //
    // Antes existía una policy "auth" de 5 req/15 min que no se aplicaba a ningún
    // endpoint: era código muerto que el readme anunciaba como feature. Se la
    // reemplaza por un límite sobre /storage, que es el vector real de abuso:
    // cada llamada firma una URL y habilita a escribir un objeto en el bucket.
    options.AddFixedWindowLimiter("storage", opt =>
    {
        opt.PermitLimit = 20;
        opt.Window = TimeSpan.FromMinutes(5);
        opt.QueueLimit = 0;
    });

    // Sin esto el 429 sale con el body vacío y el cliente no sabe qué pasó.
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Demasiadas solicitudes. Esperá un momento y volvé a intentar." },
            cancellationToken);
    };
});

// Health checks
//   /health       → liveness, sin base. Es el que mira el ALB.
//   /health/ready → readiness, con base. Ver Infrastructure/DatabaseHealthCheck.cs
// Se registra explícitamente para que el AppDbContext (scoped) se le inyecte
// por la vía normal de DI y no por activación implícita.
builder.Services.AddScoped<DatabaseHealthCheck>();

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

var app = builder.Build();


// Crear bucket de S3/MinIO si no existe
using (var scope = app.Services.CreateScope())
{
    var s3 = scope.ServiceProvider.GetRequiredService<S3Service>();
    await s3.EnsureBucketExists();
}

// --- Middleware ---
// El orden importa. Cada middleware solo ve lo que ocurre "más abajo" en la
// cadena, así que el manejo de errores tiene que ir primero para atrapar todo.

// 1) Excepciones no controladas.
//    Antes estaba después del rate limiter, así que una excepción ahí arriba
//    escapaba sin formato. Ahora envuelve toda la aplicación.
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        var exception = context.Features
            .Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;

        // El TraceId es el mismo que aparece en CloudWatch. Es lo único que se
        // expone del error: permite correlacionar el reporte de un usuario con
        // el stack trace real sin filtrarle nada de la implementación.
        var traceId = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier;

        logger.LogError(exception, "Error no controlado. TraceId={TraceId}", traceId);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Error interno del servidor",
            traceId
        });
    });
});

// 2) Respuestas de error SIN cuerpo.
//    404 de ruta inexistente, 401 sin token, 403 sin rol, 405 de método
//    equivocado: el framework las emite vacías. Un cliente recibía un status
//    pelado y ninguna pista. Acá se les pone el mismo formato { "error": ... }
//    que usa el resto de la API.
app.UseStatusCodePages(async statusCodeContext =>
{
    var response = statusCodeContext.HttpContext.Response;

    // Si algo ya escribió un cuerpo (un controller devolviendo NotFound(new {error}))
    // no se pisa.
    if (response.HasStarted) return;

    var mensaje = response.StatusCode switch
    {
        StatusCodes.Status400BadRequest => "Request inválido",
        StatusCodes.Status401Unauthorized => "Token ausente, inválido o vencido",
        StatusCodes.Status403Forbidden => "No tenés permisos para esta operación",
        StatusCodes.Status404NotFound => "El recurso solicitado no existe",
        StatusCodes.Status405MethodNotAllowed => "Método HTTP no permitido para esta ruta",
        StatusCodes.Status415UnsupportedMediaType => "Content-Type no soportado",
        StatusCodes.Status429TooManyRequests => "Demasiadas solicitudes",
        _ => "La solicitud no pudo completarse"
    };

    response.ContentType = "application/json";
    await response.WriteAsJsonAsync(new { error = mensaje });
});

app.UseSwagger();
app.UseSwaggerUI();

app.UseRateLimiter();

app.UseAuthentication();  // Primero valida el token
app.UseAuthorization();   // Después verifica permisos/roles

app.MapControllers();

// Liveness: no toca la base. Es el health check del balanceador.
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
}).AllowAnonymous();

// Readiness: incluye la base. Para diagnóstico y monitoreo, no para el ALB.
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
}).AllowAnonymous();

app.Run();