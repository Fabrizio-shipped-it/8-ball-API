namespace PoolManager.DTOs;

/// <param name="UploadUrl">URL pre-firmada para hacer PUT del archivo.</param>
/// <param name="Key">Key resultante en S3. Es lo que se manda a PATCH /players/me.</param>
/// <param name="ContentType">Content-Type con el que se firmó. El PUT debe usar el mismo.</param>
/// <param name="ExpiresInSeconds">Vigencia de la URL.</param>
public record UploadUrlDto(string UploadUrl, string Key, string ContentType, int ExpiresInSeconds);

/// <param name="DownloadUrl">URL pre-firmada para hacer GET del archivo.</param>
/// <param name="ExpiresInSeconds">Vigencia de la URL.</param>
public record DownloadUrlDto(string DownloadUrl, int ExpiresInSeconds);
