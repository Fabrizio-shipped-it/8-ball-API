using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using PoolManager.Services;

namespace PoolManager.Infrastructure;

/// <summary>
/// Base para los controllers autenticados. Centraliza cómo se pasa del token
/// al jugador de nuestra base, que es la identidad que usan los chequeos de
/// pertenencia. Antes cada controller lo resolvía por su cuenta (o no lo hacía).
/// </summary>
[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    /// El "sub" del JWT de Keycloak.
    protected string GetKeycloakId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? throw new UnauthorizedAccessException("No se pudo obtener el ID del usuario");
    }

    protected bool IsAdmin => User.IsInRole("admin");

    /// Id interno del jugador que hace el request.
    /// Devuelve null si todavía no se auto-registró (nunca llamó a GET /players/me).
    protected async Task<int?> GetCallerPlayerId(PlayerService playerService)
    {
        return await playerService.GetIdByKeycloakId(GetKeycloakId());
    }

    /// Respuesta estándar para el caso "tenés token válido pero no sos un jugador todavía".
    protected IActionResult NotRegistered() =>
        BadRequest(new { error = "Todavía no estás registrado como jugador. Llamá primero a GET /players/me" });
}
