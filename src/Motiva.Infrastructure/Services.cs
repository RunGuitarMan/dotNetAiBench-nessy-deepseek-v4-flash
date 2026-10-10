using System.Text.Json;
using Microsoft.Extensions.Options;
using Minio;
using Motiva.Application.Ports;
using StackExchange.Redis;

namespace Motiva.Infrastructure;

/// <summary>Системные часы (порт ITime). В тестах заменяется детерминированной реализацией.</summary>
public sealed class SystemClock : ITime
{
    public DateTimeOffset UtcNow => TimeProvider.System.GetUtcNow();
}

public sealed record RedisOptions
{
    public string ConnectionString { get; set; } = "localhost:6379";
}

/// <summary>
/// Кеш неперсональных данных (Valkey). write-if-asOf-newer: старый снимок не перезаписывает свежий (I202);
/// просроченное/повреждённое значение — miss (T06).
/// </summary>
public sealed class ValkeyCache(IConnectionMultiplexer redis) : ICache
{
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task<bool> WriteIfNewerAsync(string key, string value, DateTimeOffset asOf, TimeSpan ttl, CancellationToken ct = default)
    {
        var existingRaw = await _db.StringGetAsync(key).WaitAsync(ct);
        if (!existingRaw.IsNull)
        {
            try
            {
                var existing = JsonSerializer.Deserialize<CacheEnvelope>(existingRaw.ToString());
                if (existing is not null && existing.AsOf >= asOf.ToUnixTimeMilliseconds())
                {
                    return false; // свежий снимок уже есть — не перезаписываем (I202)
                }
            }
            catch (JsonException)
            {
                // повреждённое значение — miss, перезаписываем
            }
        }
        var envelope = JsonSerializer.Serialize(new CacheEnvelope(asOf.ToUnixTimeMilliseconds(), value));
        var ok = await _db.StringSetAsync(key, envelope, ttl).WaitAsync(ct);
        return ok;
    }

    public async Task<string?> ReadAsync(string key, CancellationToken ct = default)
    {
        var raw = await _db.StringGetAsync(key).WaitAsync(ct);
        if (raw.IsNull)
        {
            return null;
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<CacheEnvelope>(raw.ToString());
            if (envelope is null)
            {
                return null;
            }

            return envelope.Data;
        }
        catch (JsonException)
        {
            return null; // повреждённое значение — miss (T06)
        }
    }

    private sealed record CacheEnvelope(long AsOf, string Data);
}

public sealed class MinioStorage : IObjectStorage
{
    private readonly ObjectStorageOptions _o;
    private readonly Minio.IMinioClient _client;

    public MinioStorage(IOptions<ObjectStorageOptions> options)
    {
        _o = options.Value;
        _client = new Minio.MinioClient()
            .WithEndpoint(_o.Endpoint)
            .WithCredentials(_o.AccessKey, _o.SecretKey)
            .WithSSL(_o.UseSsl)
            .Build();
    }

    public string Bucket(Guid companyId) => _o.Bucket;

    public async Task<bool> PutIfAbsentAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct = default)
    {
        try
        {
            var args = new Minio.DataModel.Args.PutObjectArgs()
                .WithBucket(bucket).WithObject(key)
                .WithStreamData(content).WithObjectSize(content.Length)
                .WithContentType(contentType)
                .WithHeaders(new Dictionary<string, string> { ["If-None-Match"] = "*" });
            await _client.PutObjectAsync(args, ct);
            return true;
        }
        catch (Minio.Exceptions.BucketNotFoundException)
        {
            await _client.MakeBucketAsync(new Minio.DataModel.Args.MakeBucketArgs().WithBucket(bucket), ct);
            return await PutIfAbsentAsync(bucket, key, content, contentType, ct);
        }
        catch (Minio.Exceptions.MinioException)
        {
            // объект уже существует — conditional write не перезаписывает (D208/FT-159)
            return false;
        }
    }

    public async Task<bool> ExistsAsync(string bucket, string key, CancellationToken ct = default)
    {
        var args = new Minio.DataModel.Args.StatObjectArgs().WithBucket(bucket).WithObject(key);
        try
        {
            await _client.StatObjectAsync(args, ct);
            return true;
        }
        catch (Minio.Exceptions.ObjectNotFoundException)
        {
            return false;
        }
        catch (Minio.Exceptions.BucketNotFoundException)
        {
            await _client.MakeBucketAsync(new Minio.DataModel.Args.MakeBucketArgs().WithBucket(bucket), ct);
            return false;
        }
    }

    public Task<Stream> GetAsync(string bucket, string key, CancellationToken ct = default)
        => GetAsyncCore(bucket, key, ct);

    private async Task<Stream> GetAsyncCore(string bucket, string key, CancellationToken ct)
    {
        var ms = new MemoryStream();
        var args = new Minio.DataModel.Args.GetObjectArgs().WithBucket(bucket).WithObject(key)
            .WithCallbackStream(s => s.CopyTo(ms));
        await _client.GetObjectAsync(args, ct);
        ms.Position = 0;
        return ms;
    }

    public Task DeleteAsync(string bucket, string key, CancellationToken ct = default)
    {
        var args = new Minio.DataModel.Args.RemoveObjectArgs().WithBucket(bucket).WithObject(key);
        return _client.RemoveObjectAsync(args, ct);
    }

    public async Task<string> CreatePresignedGetAsync(string bucket, string key, TimeSpan ttl, CancellationToken ct = default)
    {
        var args = new Minio.DataModel.Args.PresignedGetObjectArgs()
            .WithBucket(bucket).WithObject(key).WithExpiry((int)ttl.TotalSeconds);
        return await _client.PresignedGetObjectAsync(args);
    }
}
