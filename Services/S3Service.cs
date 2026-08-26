using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;

namespace PoolManager.Services;

public class S3Service
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;
    private readonly ILogger<S3Service> _logger;

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
                // Sin esto el error se pierde y recién aparece como fallo al firmar URLs.
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

    // Genera una URL pre-firmada para SUBIR una imagen
    public (string url, string key, string contentType) GetUploadUrl(string fileName, string contentType)
    {
        var key = $"players/{Guid.NewGuid()}/{fileName}";

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = key,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.AddMinutes(15),
            ContentType = contentType
        };

        var url = _s3Client.GetPreSignedURL(request);

        return (url, key, contentType);
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
            Expires = DateTime.UtcNow.AddHours(1)
        };

        return _s3Client.GetPreSignedURL(request);
    }
}