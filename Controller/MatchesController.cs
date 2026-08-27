using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PoolManager.DTOs;
using PoolManager.Infrastructure;
using PoolManager.Services;

namespace PoolManager.Controllers;

[ApiController]
[Route("[controller]")]
[Authorize]
[EnableRateLimiting("general")]
public class MatchesController : ApiControllerBase
{
    private readonly MatchService _matchService;
    private readonly PlayerService _playerService;

    public MatchesController(MatchService matchService, PlayerService playerService)
    {
        _matchService = matchService;
        _playerService = playerService;
    }

    // POST /matches
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMatchDto dto)
    {
        var callerId = await GetCallerPlayerId(_playerService);
        if (callerId == null) return NotRegistered();

        var (match, error, kind) = await _matchService.Create(dto, callerId.Value, IsAdmin);
        if (kind != MatchError.None) return MapError(kind, error);

        return CreatedAtAction(nameof(GetById), new { id = match!.Id }, match);
    }

    // GET /matches
    // Por defecto devuelve solo TUS partidas. ?all=true es exclusivo de admin.
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? date,
        [FromQuery] string? status,
        [FromQuery] bool all = false)
    {
        var callerId = await GetCallerPlayerId(_playerService);
        if (callerId == null) return NotRegistered();

        if (all && !IsAdmin)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Solo un admin puede listar todas las partidas" });

        var matches = await _matchService.GetAll(date, status, callerId.Value, IsAdmin, all);
        return Ok(matches);
    }

    // GET /matches/:id
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var callerId = await GetCallerPlayerId(_playerService);
        if (callerId == null) return NotRegistered();

        var (match, kind) = await _matchService.GetById(id, callerId.Value, IsAdmin);
        if (kind != MatchError.None) return NotFound(new { error = "Match no encontrado" });

        return Ok(match);
    }

    // PATCH /matches/:id
    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateMatchDto dto)
    {
        var callerId = await GetCallerPlayerId(_playerService);
        if (callerId == null) return NotRegistered();

        var (match, error, kind) = await _matchService.Update(id, dto, callerId.Value, IsAdmin);
        if (kind != MatchError.None) return MapError(kind, error);

        return Ok(match);
    }

    // DELETE /matches/:id
    [HttpDelete("{id}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var (_, error, kind) = await _matchService.Delete(id);
        if (kind != MatchError.None) return MapError(kind, error);

        return NoContent();
    }

    /// Traduce el tipo de error del servicio al status code, sin adivinar
    /// leyendo el texto del mensaje como se hacía antes.
    private IActionResult MapError(MatchError kind, string? error)
    {
        var payload = new { error = error ?? "Request inválido" };

        return kind switch
        {
            MatchError.NotFound => NotFound(payload),
            MatchError.Conflict => Conflict(payload),
            MatchError.Forbidden => StatusCode(StatusCodes.Status403Forbidden, payload),
            _ => BadRequest(payload)
        };
    }
}
