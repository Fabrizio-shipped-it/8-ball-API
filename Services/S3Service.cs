using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;

namespace PoolManager.Services;

public class S3Service
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;
    private readonly ILogger<S3Service> _logger;

    /// Ventana corta a propósito. Una URL pre-firmada de S3 NO se puede invalidar
    /// después de usarla (no existe el single-use nativo), así que lo único que
    /// se puede acotar es cuánto tiempo vive.
    private static readonly TimeSpan UploadUrlLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DownloadUrlLifetime = TimeSpan.FromHours(1);

    private static readonly Regex UnsafeFileNameChars = new(@"[^A-Za-z0-9._-]", RegexOptions.Compiled);

    public S3Service(IConfiguration configuration, ILogger<S3Service> logger)
    {
        _logger = logger;

        var s3Config = new AmazonS3Config
        {
            ForcePathStyle = bool.Parse(configuration["S3:ForcePathStyle"] ?? "false")
        };

        var serviceUrl = configuration["S3:ServiceUrl"];
        if (!string.IsNullOrEmpty(serviceUrl))
        {
            s3Config.ServiceURL = serviceUrl;
        }
        else
        {
            s3Config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(
                configuration["S3:Region"] ?? "us-east-1");
        }

        var accessKey = configuration["S3:AccessKey"];
        var secretKey = configuration["S3:SecretKey"];

        if (!string.IsNullOrEmpty(accessKey) && !string.IsNullOrEmpty(secretKey))
        {
            // Local (MinIO): credenciales explícitas desde configuración.
            _s3Client = new AmazonS3Client(accessKey, secretKey, s3Config);
        }
        else
        {
            // AWS: cadena de credenciales por defecto (task role de ECS).
            // Sin esto el constructor lanza ArgumentNullException y el contenedor
            // muere en el arranque antes de responder el health check.
            _logger.LogInformation(
                "S3: sin AccessKey/SecretKey en configuración, usando la cadena de credenciales por defecto (task role).");
            _s3Client = new AmazonS3Client(s3Config);
        }

        _bucketName = configuration["S3:BucketName"] ?? "profile-pictures";
    }

    // Asegura que el bucket exista al iniciar
    public async Task EnsureBucketExists()
    {
        try
        {
            await _s3Client.EnsureBucketExistsAsync(_bucketName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "No se pudo verificar el bucket '{Bucket}'. Se intenta crearlo.", _bucketName);

            try
            {
                await _s3Client.PutBucketAsync(_bucketName);
                _logger.LogInformation("Bucket '{Bucket}' creado.", _bucketName);
            }
            catch (AmazonS3Exception e) when (e.ErrorCode == "BucketAlreadyOwnedByYou")
            {
                // Ya existe, no hay problema
            }
            catch (AmazonS3Exception e) when (
                e.ErrorCode == "InvalidAccessKeyId" ||
                e.ErrorCode == "SignatureDoesNotMatch" ||
                e.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                // Credenciales inválidas: no crashea el arranque, pero queda registrado.
                _logger.LogError(e,
                    "Credenciales de S3 inválidas o sin permisos (ErrorCode={ErrorCode}). " +
                    "Las URLs pre-firmadas van a fallar con SignatureDoesNotMatch. " +
                    "Revisar S3__AccessKey / S3__SecretKey.", e.ErrorCode);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error inesperado creando el bucket '{Bucket}'.", _bucketName);
            }
        }
    }

    /// Prefijo de carpeta de un jugador. Es la unidad de aislamiento:
    /// todo lo que esté fuera de acá no le pertenece.
    public static string PrefixForPlayer(int playerId) => $"players/{playerId}/";

    /// True si la key vive dentro de la carpeta del jugador.
    public static bool KeyBelongsToPlayer(string key, int playerId) =>
        !string.IsNullOrWhiteSpace(key) &&
        key.StartsWith(PrefixForPlayer(playerId), StringComparison.Ordinal) &&
        !key.Contains("..", StringComparison.Ordinal);

    // Genera una URL pre-firmada para SUBIR una imagen a la carpeta del jugador
    public (string url, string key, string contentType) GetUploadUrl(int playerId, string fileName, string contentType)
    {
        // Se conserva el nombre original (legible) pero saneado, y se le antepone
        // un prefijo corto único para que dos subidas del mismo archivo no se pisen.
        var safeName = UnsafeFileNameChars.Replace(Path.GetFileName(fileName), "_");
        if (safeName.Length > 80) safeName = safeName[^80..];
        var unique = Guid.NewGuid().ToString("N")[..8];

        var key = $"{PrefixForPlayer(playerId)}{unique}-{safeName}";

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = key,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.Add(UploadUrlLifetime),
            ContentType = contentType
        };

        return (_s3Client.GetPreSignedURL(request), key, contentType);
    }

    // Genera una URL pre-firmada para VER una imagen
    public string GetDownloadUrl(string key)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = key,
            Verb = HttpVerb.GET,
            // 1 hora, no 24. En ECS las credenciales vienen del task role y son
            // temporales: la URL pre-firmada deja de ser válida cuando expira el
            // session token (~6 h), aunque la firma diga 24 h.
            Expires = DateTime.UtcNow.Add(DownloadUrlLifetime)
        };

        return _s3Client.GetPreSignedURL(request);
    }

    /// Confirma que el objeto realmente se subió.
    /// Firmar una URL es una operación local: S3 nunca se entera. Sin este chequeo
    /// se podía guardar en la base una key de un archivo que no existe.
    public async Task<bool> ObjectExists(string key)
    {
        try
        {
            await _s3Client.GetObjectMetadataAsync(_bucketName, key);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error verificando la existencia de la key '{Key}'.", key);
            return false;
        }
    }
}
