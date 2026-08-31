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
[ProducesResponseType(typeof(ErrorDto), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ErrorDto), StatusCodes.Status429TooManyRequests)]
public class MatchesController : ApiControllerBase
{
    private readonly MatchService _matchService;
    private readonly PlayerService _playerService;

    public MatchesController(MatchService matchService, PlayerService playerService)
    {
        _matchService = matchService;
        _playerService = playerService;
    }

    /// <summary>
    /// Crea una partida. Solo podés crear partidas en las que participás,
    /// salvo que seas admin.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(MatchResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateMatchDto dto)
    {
        var callerId = await GetCallerPlayerId(_playerService);
        if (callerId == null) return NotRegistered();

        var (match, error, kind) = await _matchService.Create(dto, callerId.Value, IsAdmin);
        if (kind != MatchError.None) return MapError(kind, error);

        return CreatedAtAction(nameof(GetById), new { id = match!.Id }, match);
    }

    /// <summary>
    /// Lista tus partidas. Filtros opcionales por fecha (YYYY-MM-DD) y estado
    /// (upcoming / ongoing / completed). El parámetro all=true, que devuelve
    /// las de todos los jugadores, está reservado a admin.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<MatchResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? date,
        [FromQuery] string? status,
        [FromQuery] bool all = false)
    {
        var callerId = await GetCallerPlayerId(_playerService);
        if (callerId == null) return NotRegistered();

        if (all && !IsAdmin)
            return StatusCode(StatusCodes.Status403Forbidden,
                new ErrorDto("Solo un admin puede listar todas las partidas"));

        var matches = await _matchService.GetAll(date, status, callerId.Value, IsAdmin, all);
        return Ok(matches);
    }

    /// <summary>
    /// Detalle de una partida. Si no participás, devuelve 404 en vez de 403:
    /// un 403 confirmaría que la partida existe.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(MatchResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id)
    {
        var callerId = await GetCallerPlayerId(_playerService);
        if (callerId == null) return NotRegistered();

        var (match, kind) = await _matchService.GetById(id, callerId.Value, IsAdmin);
        if (kind != MatchError.None) return NotFound(new ErrorDto("Match no encontrado"));

        return Ok(match);
    }

    /// <summary>
    /// Actualiza una partida o declara el ganador. Solo participantes o admin.
    /// </summary>
    [HttpPatch("{id}")]
    [ProducesResponseType(typeof(MatchResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateMatchDto dto)
    {
        var callerId = await GetCallerPlayerId(_playerService);
        if (callerId == null) return NotRegistered();

        var (match, error, kind) = await _matchService.Update(id, dto, callerId.Value, IsAdmin);
        if (kind != MatchError.None) return MapError(kind, error);

        return Ok(match);
    }

    /// <summary>Elimina una partida. Solo admin, y solo si no está en curso.</summary>
    [HttpDelete("{id}")]
    [Authorize(Roles = "admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status409Conflict)]
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
        var payload = new ErrorDto(error ?? "Request inválido");

        return kind switch
        {
            MatchError.NotFound => NotFound(payload),
            MatchError.Conflict => Conflict(payload),
            MatchError.Forbidden => StatusCode(StatusCodes.Status403Forbidden, payload),
            _ => BadRequest(payload)
        };
    }
}
