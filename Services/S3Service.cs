using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;

namespace PoolManager.Services;

public class S3Service
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;

    public S3Service(IConfiguration configuration)
    {
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

        _s3Client = new AmazonS3Client(
            configuration["S3:AccessKey"],
            configuration["S3:SecretKey"],
            s3Config
        );

        _bucketName = configuration["S3:BucketName"] ?? "profile-pictures";
    }

    // Asegura que el bucket exista al iniciar
    public async Task EnsureBucketExists()
    {
        try
        {
            await _s3Client.EnsureBucketExistsAsync(_bucketName);
        }
        catch
        {
            try
            {
                await _s3Client.PutBucketAsync(_bucketName);
            }
            catch (AmazonS3Exception e) when (e.ErrorCode == "BucketAlreadyOwnedByYou")
            {
                // Ya existe, no hay problema
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
            Expires = DateTime.UtcNow.AddHours(24)
        };

        return _s3Client.GetPreSignedURL(request);
    }
}