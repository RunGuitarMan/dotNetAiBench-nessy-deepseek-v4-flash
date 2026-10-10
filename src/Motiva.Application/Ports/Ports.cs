using Motiva.Domain;

namespace Motiva.Application.Ports;

/// <summary>
/// Атомарная единица работы поверх хранилища (B17/B19/B20/D211). Реализуется в Infrastructure (EF Core).
/// Application-входы выполняют авторизацию, валидацию и бизнес-повторы ДО любых изменений (D207).
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    IQueryable<T> Set<T>() where T : class;

    void Add<T>(T entity) where T : class;
    void Update<T>(T entity) where T : class;
    void Remove<T>(T entity) where T : class;

    /// <summary>Применить изменения как одну транзакцию (B17). Возвращает количество изменённых строк.</summary>
    Task<int> SaveChangesAsync(CancellationToken ct = default);

    /// <summary>Начать явную транзакцию с уровнем изоляции.</summary>
    Task<ITransaction> BeginTransactionAsync(
        System.Data.IsolationLevel level,
        CancellationToken ct = default);

    /// <summary>Ключ компании текущего контекста (для проверок принадлежности).</summary>
    Guid TenantCompanyId { get; }

    /// <summary>
    /// Выполнить операцию в одной транзакции с изоляцией Serializable и повторной попыткой
    /// при сериализационных конфликтах (B17/B19/B20). Никакой частичный результат не остаётся.
    /// </summary>
    Task<T> ExecuteSerializableAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default);

    /// <summary>То же для операций без результата (финализация, администрирование).</summary>
    Task ExecuteSerializableAsync(Func<CancellationToken, Task> operation, CancellationToken ct = default);
}

/// <summary>Транзакция (порт-барьер для тестов встраивается в реализацию).</summary>
public interface ITransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);
    Task RollbackAsync(CancellationToken ct = default);
}

/// <summary>Время (обёртка TimeProvider); единственный источник системного момента (B14/T05).</summary>
public interface ITime
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Хранилище объектных байтов (S3/priv.) для выписок (D208).</summary>
public interface IObjectStorage
{
    /// <summary>Upload с conditional write (put-if-absent). Возвращает true, если объект записан; false — уже существует.</summary>
    Task<bool> PutIfAbsentAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct = default);

    /// <summary>Существует ли объект и целостен ли (content-md5).</summary>
    Task<bool> ExistsAsync(string bucket, string key, CancellationToken ct = default);

    Task<Stream> GetAsync(string bucket, string key, CancellationToken ct = default);

    Task DeleteAsync(string bucket, string key, CancellationToken ct = default);

    Task<string> CreatePresignedGetAsync(string bucket, string key, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Имя bucket по умолчанию (tenant-схема).</summary>
    string Bucket(Guid companyId);
}

/// <summary>Генератор выписки (T07/D208): снимок, CSV, S3, CAS Ready.</summary>
public interface IStatementGenerator
{
    /// <summary>Сформировать выписку. Возвращает true при успешном Ready, false при необходимости повтора.</summary>
    Task<bool> GenerateAsync(long statementId, string workerId, CancellationToken ct = default);
}

/// <summary>Кеш неперсональных данных (Valkey): каталог и live-рейтинг (T06/D209).</summary>
public interface ICache
{
    /// <summary>write-if-asOf-newer; не перезаписывает свежий снимок (I202).</summary>
    Task<bool> WriteIfNewerAsync(string key, string value, DateTimeOffset asOf, TimeSpan ttl, CancellationToken ct = default);

    Task<string?> ReadAsync(string key, CancellationToken ct = default);
}

/// <summary>Свойства подключения к хранилищу объектов.</summary>
public sealed record ObjectStorageOptions
{
    public string Endpoint { get; init; } = string.Empty;
    public string AccessKey { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;
    public string Bucket { get; init; } = string.Empty;
    public bool UseSsl { get; init; }
}
