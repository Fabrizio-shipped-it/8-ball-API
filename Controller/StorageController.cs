using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PoolManager.Services;

namespace PoolManager.Controllers;

[ApiController]
[Route("[controller]")]
[Authorize]
public class StorageController : ControllerBase
{
    private readonly S3Service _s3Service;

    public StorageController(S3Service s3Service)
    {
        _s3Service = s3Service;
    }

    // GET /storage/upload-url?fileName=photo.jpg
    [HttpGet("upload-url")]
    public IActionResult GetUploadUrl([FromQuery] string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return BadRequest(new { error = "fileName es requerido" });

        var url = _s3Service.GetUploadUrl(fileName);

        // Extraer la key de la URL para que el cliente la guarde después
        var uri = new Uri(url);
        var key = uri.AbsolutePath.TrimStart('/').Replace($"profile-pictures/", "");

        return Ok(new { uploadUrl = url, key });
    }

    // GET /storage/download-url?key=players/guid/photo.jpg
    [HttpGet("download-url")]
    public IActionResult GetDownloadUrl([FromQuery] string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return BadRequest(new { error = "key es requerido" });

        var url = _s3Service.GetDownloadUrl(key);
        return Ok(new { downloadUrl = url });
    }
}