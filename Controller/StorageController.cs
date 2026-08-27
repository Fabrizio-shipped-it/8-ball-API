using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PoolManager.Infrastructure;
using PoolManager.Services;

namespace PoolManager.Controllers;

[ApiController]
[Route("[controller]")]
[Authorize]
[EnableRateLimiting("general")]
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

    // GET /storage/upload-url?fileName=foto.jpg&contentType=image/jpeg
    [HttpGet("upload-url")]
    public async Task<IActionResult> GetUploadUrl(
        [FromQuery] string fileName,
        [FromQuery] string contentType = "image/jpeg")
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return BadRequest(new { error = "fileName es requerido" });

        if (!AllowedContentTypes.Contains(contentType))
            return BadRequest(new { error = "Tipo de imagen no permitido. Usá jpeg, png o webp" });

        var playerId = await GetCallerPlayerId(_playerService);
        if (playerId == null) return NotRegistered();

        // La key se arma con el ID REAL del jugador, no con un GUID suelto.
        // Antes cada subida generaba una carpeta nueva sin relación con nadie,
        // así que el bucket quedaba lleno de directorios huérfanos.
        var (url, key, ct) = _s3Service.GetUploadUrl(playerId.Value, fileName, contentType);

        return Ok(new { uploadUrl = url, key, contentType = ct, expiresInSeconds = 300 });
    }

    // GET /storage/download-url?key=players/12/9f3a-foto.jpg
    [HttpGet("download-url")]
    public async Task<IActionResult> GetDownloadUrl([FromQuery] string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return BadRequest(new { error = "key es requerido" });

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
            return NotFound(new { error = "Imagen no encontrada" });

        if (isOwn && !await _s3Service.ObjectExists(key))
            return NotFound(new { error = "Imagen no encontrada" });

        return Ok(new { downloadUrl = _s3Service.GetDownloadUrl(key), expiresInSeconds = 3600 });
    }
}
