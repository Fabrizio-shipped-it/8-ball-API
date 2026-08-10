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
            ServiceURL = configuration["S3:ServiceUrl"],
            ForcePathStyle = true // Necesario para MinIO
        };

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
        // Intenta acceder al bucket directamente
        await _s3Client.EnsureBucketExistsAsync(_bucketName);
    }
    catch
    {
        // Si falla, intenta crearlo manualmente
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
    public string GetUploadUrl(string fileName)
    {
        var key = $"players/{Guid.NewGuid()}/{fileName}";

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = key,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.AddMinutes(15),
            ContentType = "image/*"
        };

        var url = _s3Client.GetPreSignedURL(request);

        return url;
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