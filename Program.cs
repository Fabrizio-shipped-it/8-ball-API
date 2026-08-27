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
        options.UseNpgsql(dataSource));
}
else
{
    // En local: conexión normal con contraseña en la connection string
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(connectionString));
}


    



builder.Services.AddScoped<PlayerService>();
builder.Services.AddScoped<MatchService>();
builder.Services.AddSingleton<S3Service>();


// Autenticación JWT con Keycloak
var keycloakAuthority = builder.Configuration["Keycloak:Authority"]!;
var keycloakPublicAuthority = builder.Configuration["Keycloak:PublicAuthority"] ?? keycloakAuthority;

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = keycloakAuthority;
        options.Audience = builder.Configuration["Keycloak:ClientId"];

        options.RequireHttpsMetadata = false;

        // Si Authority (IP privada) difiere de PublicAuthority (IP pública/Elastic IP),
        // reescribir las URLs que Keycloak devuelve en el discovery document
        // para que el middleware busque las JWKS keys por la IP privada.
        if (keycloakAuthority != keycloakPublicAuthority)
        {
            options.BackchannelHttpHandler = new KeycloakUrlRewriteHandler(
                keycloakPublicAuthority, keycloakAuthority);
        }

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidIssuer = keycloakPublicAuthority,
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

    options.AddFixedWindowLimiter("auth", opt =>
    {
        opt.PermitLimit = 5;
        opt.Window = TimeSpan.FromMinutes(15);
        opt.QueueLimit = 0;
    });
});

// Healthcheck
builder.Services.AddHealthChecks();

var app = builder.Build();


// Crear bucket de S3/MinIO si no existe
using (var scope = app.Services.CreateScope())
{
    var s3 = scope.ServiceProvider.GetRequiredService<S3Service>();
    await s3.EnsureBucketExists();
}

// --- Middleware ---

app.UseSwagger();
app.UseSwaggerUI();


app.UseRateLimiter();


/// Manejo global de excepciones
/// Qué hace: si cualquier excepción no controlada ocurre, en vez de devolver el stack trace completo (que revela rutas, nombres de clases, etc.), 
/// devuelve un JSON genérico {"error": "Error interno del servidor"} y loguea el error real internamente.
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;

        logger.LogError(exception, "Error no controlado");

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = "Error interno del servidor" });
    });
});


app.UseAuthentication();  // Primero valida el token
app.UseAuthorization();   // Después verifica permisos/roles

app.MapControllers();

app.MapHealthChecks("/health").AllowAnonymous();

app.Run();