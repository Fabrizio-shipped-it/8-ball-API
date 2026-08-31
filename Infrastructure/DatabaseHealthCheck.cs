using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoolManager.Data;

namespace PoolManager.Infrastructure;

/// <summary>
/// Chequeo de readiness: verifica que la base responda de verdad.
///
/// Deliberadamente NO se engancha a "/health", que es el endpoint que mira el
/// balanceador. Si el health check del ALB dependiera de la base, una demora
/// transitoria de Aurora (por ejemplo un cold start) haría que ECS matara las
/// tareas y entrás en un loop de reinicios por algo que se resuelve solo.
///
/// "/health"       → liveness  : ¿el proceso está vivo? Lo usa el ALB.
/// "/health/ready" → readiness : ¿puede atender? Lo usás vos y el monitoreo.
/// </summary>
public class DatabaseHealthCheck : IHealthCheck
{
    private readonly AppDbContext _context;

    public DatabaseHealthCheck(AppDbContext context)
    {
        _context = context;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // CanConnectAsync abre una conexión real y la cierra. Es más barato
            // que una query, pero suficiente para detectar credenciales vencidas,
            // el gateway caído o el cluster pausado.
            var puedeConectar = await _context.Database.CanConnectAsync(cancellationToken);

            return puedeConectar
                ? HealthCheckResult.Healthy("Base accesible")
                : HealthCheckResult.Unhealthy("No se pudo conectar a la base");
        }
        catch (Exception ex)
        {
            // El mensaje de la excepción NO va en la descripción: puede contener
            // el host y el usuario de la base. Queda en el log interno.
            return HealthCheckResult.Unhealthy("No se pudo conectar a la base", ex);
        }
    }
}
