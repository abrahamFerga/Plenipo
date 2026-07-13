using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace Plenipo.Connectors.S3;

/// <summary>The tenant's S3 connection, resolved from protected connector settings per call.</summary>
public sealed record S3Connection(
    string AccessKeyId, string SecretAccessKey, string Bucket, string Region, string? ServiceUrl);

/// <summary>One object as the tools see it.</summary>
public sealed record S3Entry(string Key, long Size);

/// <summary>A downloaded object's content.</summary>
public sealed record S3Content(Stream Content, string ContentType);

/// <summary>
/// The slice of S3 the connector needs (list/download) — a seam so keyless tests fake S3 while
/// production speaks the AWS SDK (which also covers S3-compatibles like MinIO and R2 via
/// <see cref="S3Connection.ServiceUrl"/>).
/// </summary>
public interface IS3ObjectClient
{
    public Task<IReadOnlyList<S3Entry>> ListAsync(
        S3Connection connection, string? prefix, CancellationToken cancellationToken = default);

    public Task<S3Content?> DownloadAsync(
        S3Connection connection, string key, CancellationToken cancellationToken = default);
}

/// <summary>AWS SDK implementation. Clients are built per call — settings are per tenant, never cached across them.</summary>
public sealed class S3ObjectClient : IS3ObjectClient
{
    private static AmazonS3Client Create(S3Connection connection)
    {
        var config = new AmazonS3Config();
        if (!string.IsNullOrWhiteSpace(connection.ServiceUrl))
        {
            config.ServiceURL = connection.ServiceUrl; // MinIO / R2 / any S3-compatible endpoint
            config.ForcePathStyle = true;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(connection.Region);
        }

        return new AmazonS3Client(connection.AccessKeyId, connection.SecretAccessKey, config);
    }

    public async Task<IReadOnlyList<S3Entry>> ListAsync(
        S3Connection connection, string? prefix, CancellationToken cancellationToken = default)
    {
        using var client = Create(connection);
        var response = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = connection.Bucket,
            Prefix = prefix,
            MaxKeys = 100,
        }, cancellationToken);

        return [.. (response.S3Objects ?? []).Select(o => new S3Entry(o.Key, o.Size ?? 0))];
    }

    public async Task<S3Content?> DownloadAsync(
        S3Connection connection, string key, CancellationToken cancellationToken = default)
    {
        using var client = Create(connection);
        try
        {
            var response = await client.GetObjectAsync(connection.Bucket, key, cancellationToken);
            return new S3Content(response.ResponseStream, response.Headers.ContentType ?? "application/octet-stream");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
