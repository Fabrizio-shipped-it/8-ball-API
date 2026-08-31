using System.Security.Claims;
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
public class PlayersController : ApiControllerBase
{
    private readonly PlayerService _playerService;
    private readonly S3Service _s3Service;

    public PlayersController(PlayerService playerService, S3Service s3Service)
    {
        _playerService = playerService;
        _s3Service = s3Service;
    }

    /// <summary>Lista todos los jugadores. Solo admin.</summary>
    [HttpGet]
    [Authorize(Roles = "admin")]
    [ProducesResponseType(typeof(List<PlayerResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAll([FromQuery] string? name)
    {
        var players = await _playerService.GetAll(name);
        return Ok(players);
    }

    /// <summary>Obtiene tu perfil. En el primer acceso lo crea a partir del token.</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(PlayerResponseDto), StatusCodes.Status200OK)]
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

    /// <summary>Crea un jugador. Solo admin.</summary>
    [HttpPost]
    [Authorize(Roles = "admin")]
    [ProducesResponseType(typeof(PlayerResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create([FromBody] CreatePlayerDto dto)
    {
        // Admin crea un jugador, se genera un keycloakId placeholder
        var keycloakId = Guid.NewGuid().ToString();
        var player = await _playerService.Create(dto, keycloakId);
        return CreatedAtAction(nameof(GetMe), new { id = player.Id }, player);
    }

    /// <summary>Actualiza tu perfil. La key de imagen se valida contra tu carpeta de S3.</summary>
    [HttpPatch("me")]
    [ProducesResponseType(typeof(PlayerResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateMe([FromBody] UpdatePlayerDto dto)
    {
        var keycloakId = GetKeycloakId();

        if (dto.ProfilePictureKey != null)
        {
            var playerId = await _playerService.GetIdByKeycloakId(keycloakId);
            if (playerId == null) return NotRegistered();

            // 1) La key tiene que estar dentro de TU carpeta.
            //    Antes el cliente mandaba una URL arbitraria y se podía apuntar
            //    el perfil propio a la imagen de otro jugador.
            if (!S3Service.KeyBelongsToPlayer(dto.ProfilePictureKey, playerId.Value))
                return BadRequest(new ErrorDto("La imagen no pertenece a tu perfil"));

            // 2) El objeto tiene que existir de verdad.
            //    Firmar una URL de subida es una operación local: S3 no se entera
            //    hasta que el archivo llega. Sin este chequeo se podía guardar la
            //    key de una subida que nunca ocurrió.
            if (!await _s3Service.ObjectExists(dto.ProfilePictureKey))
                return BadRequest(new ErrorDto("No se encontró la imagen. ¿Completaste la subida?"));
        }

        var player = await _playerService.Update(keycloakId, dto);
        if (player == null) return NotFound(new ErrorDto("Jugador no encontrado"));
        return Ok(player);
    }

    /// <summary>Elimina un jugador. Solo admin, y solo si no tiene partidas.</summary>
    [HttpDelete("{id}")]
    [Authorize(Roles = "admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(int id)
    {
        var (success, error, conflict) = await _playerService.Delete(id);

        if (!success)
            return conflict
                ? Conflict(new ErrorDto(error!))
                : NotFound(new ErrorDto(error!));

        return NoContent();
    }
}
