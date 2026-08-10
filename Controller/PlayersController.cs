using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PoolManager.DTOs;
using PoolManager.Services;
using Microsoft.AspNetCore.RateLimiting;

namespace PoolManager.Controllers;


[ApiController]
[Route("[controller]")]
[Authorize]
[EnableRateLimiting("general")]
public class PlayersController : ControllerBase
{
    private readonly PlayerService _playerService;

    public PlayersController(PlayerService playerService)
    {
        _playerService = playerService;
    }

    // GET /players (admin only)
    [HttpGet]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> GetAll([FromQuery] string? name)
    {
        var players = await _playerService.GetAll(name);
        return Ok(players);
    }

    // GET /players/me
    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        var keycloakId = GetKeycloakId();
        var player = await _playerService.GetByKeycloakId(keycloakId);

        if (player == null)
        {
            // Auto-registro: primera vez que el usuario accede
            var name = User.FindFirstValue(ClaimTypes.Name)
                    ?? User.FindFirstValue("preferred_username")
                    ?? "Unknown";
            player = await _playerService.GetOrCreateFromToken(keycloakId, name);
        }

        return Ok(player);
    }

    // POST /players (admin only)
    [HttpPost]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Create([FromBody] CreatePlayerDto dto)
    {
        // Admin crea un jugador, se genera un keycloakId placeholder
        var keycloakId = Guid.NewGuid().ToString();
        var player = await _playerService.Create(dto, keycloakId);
        return CreatedAtAction(nameof(GetMe), new { id = player.Id }, player);
    }

    // PATCH /players/me
    [HttpPatch("me")]
    public async Task<IActionResult> UpdateMe([FromBody] UpdatePlayerDto dto)
    {
        var keycloakId = GetKeycloakId();
        var player = await _playerService.Update(keycloakId, dto);
        if (player == null) return NotFound();
        return Ok(player);
    }

    // DELETE /players/:id (admin only)
    [HttpDelete("{id}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var deleted = await _playerService.Delete(id);
        if (!deleted) return NotFound();
        return NoContent();
    }

    private string GetKeycloakId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? throw new UnauthorizedAccessException("No se pudo obtener el ID del usuario");
    }
}