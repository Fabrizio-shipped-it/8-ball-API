using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PoolManager.Services;
using Microsoft.AspNetCore.RateLimiting;

namespace PoolManager.Controllers;


[ApiController]
[Route("[controller]")]
[Authorize]
[EnableRateLimiting("general")]
public class StorageController : ControllerBase
{
    private readonly S3Service _s3Service;

    public StorageController(S3Service s3Service)
    {
        _s3Service = s3Service;
    }

    // GET /storage/upload-url?fileName=photo.jpg&contentType=image/jpeg
    [HttpGet("upload-url")]
    public IActionResult GetUploadUrl(
        [FromQuery] string fileName,
        [FromQuery] string contentType = "image/jpeg")
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return BadRequest(new { error = "fileName es requerido" });

        var (url, key, ct) = _s3Service.GetUploadUrl(fileName, contentType);

        return Ok(new { uploadUrl = url, key, contentType = ct });
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