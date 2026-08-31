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
// Límite más estricto que el resto de la API: cada llamada acá firma una URL
// que habilita a escribir en el bucket.
[EnableRateLimiting("storage")]
[ProducesResponseType(typeof(ErrorDto), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ErrorDto), StatusCodes.Status429TooManyRequests)]
public class StorageController : ApiControllerBase
{
    private static readonly string[] AllowedContentTypes =
        ["image/jpeg", "image/png", "image/webp"];

    private readonly S3Service _s3Service;
    private readonly PlayerService _playerService;

    public StorageController(S3Service s3Service, PlayerService playerService)
    {
        _s3Service = s3Service;
        _playerService = playerService;
    }

    /// <summary>
    /// Devuelve una URL pre-firmada para subir una imagen a tu carpeta.
    /// Válida 5 minutos.
    /// </summary>
    [HttpGet("upload-url")]
    [ProducesResponseType(typeof(UploadUrlDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetUploadUrl(
        [FromQuery] string fileName,
        [FromQuery] string contentType = "image/jpeg")
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return BadRequest(new ErrorDto("fileName es requerido"));

        if (!AllowedContentTypes.Contains(contentType))
            return BadRequest(new ErrorDto("Tipo de imagen no permitido. Usá jpeg, png o webp"));

        var playerId = await GetCallerPlayerId(_playerService);
        if (playerId == null) return NotRegistered();

        // La key se arma con el ID REAL del jugador, no con un GUID suelto.
        // Antes cada subida generaba una carpeta nueva sin relación con nadie,
        // así que el bucket quedaba lleno de directorios huérfanos.
        var (url, key, ct) = _s3Service.GetUploadUrl(playerId.Value, fileName, contentType);

        return Ok(new UploadUrlDto(url, key, ct, 300));
    }

    /// <summary>
    /// Devuelve una URL pre-firmada para ver una imagen. Solo funciona con keys
    /// propias o con la foto de perfil publicada de algún jugador.
    /// </summary>
    [HttpGet("download-url")]
    [ProducesResponseType(typeof(DownloadUrlDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDownloadUrl([FromQuery] string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return BadRequest(new ErrorDto("key es requerido"));

        var playerId = await GetCallerPlayerId(_playerService);
        if (playerId == null) return NotRegistered();

        // Se firma solo si la key es tuya, o si es la foto de perfil publicada
        // de algún jugador (aparecen en los listados de partidas, así que tienen
        // que ser visibles). Cualquier otra key se rechaza.
        //
        // Antes se firmaba CUALQUIER string, incluida una URL completa pegada por
        // error: presignar es una operación local y S3 nunca valida la key, así
        // que la API devolvía enlaces a objetos ajenos sin chequear nada.
        var isOwn = S3Service.KeyBelongsToPlayer(key, playerId.Value);
        var isPublished = !isOwn && await _playerService.IsPublishedProfileKey(key);

        if (!isOwn && !isPublished)
            return NotFound(new ErrorDto("Imagen no encontrada"));

        if (isOwn && !await _s3Service.ObjectExists(key))
            return NotFound(new ErrorDto("Imagen no encontrada"));

        return Ok(new DownloadUrlDto(_s3Service.GetDownloadUrl(key), 3600));
    }
}
