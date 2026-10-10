using Motiva.Domain;

namespace Motiva.Application;

/// <summary>Проверенный инициатор из JWT (D201).</summary>
public sealed record Actor(ActorType Type, string Subject, Guid CompanyId, int? MasterId, bool IsAdmin)
{
    /// <summary>initiator_key = "{type}:{subject}" (D201).</summary>
    public string InitiatorKey => $"{(Type == ActorType.User ? "user" : "service")}:{Subject}";
}

/// <summary>Текущий инициатор (API — из HttpContext; Worker — системный контекст).</summary>
public interface ICurrentActor
{
    Actor? Actor { get; }
}

/// <summary>Типы ошибок приложения, маппящиеся на HTTP-статусы (T04/D206).</summary>
public enum ErrorCode
{
    ValidationError,
    NotFound,
    Forbidden,
    Conflict,
    PreconditionFailed,
    PreconditionRequired,
    Gone,
    InsufficientFunds,
    ResourceUnavailable,
    ServiceUnavailable,
    Internal,
}

public sealed class MotivaException : Exception
{
    public MotivaException(ErrorCode code, string message, string? machineCode = null)
        : base(message)
    {
        Code = code;
        MachineCode = machineCode;
    }

    public ErrorCode Code { get; }
    public string? MachineCode { get; }

    /// <summary>Транспортная идемпотентность (T05): 503 + Retry-After.</summary>
    public TimeSpan? RetryAfter { get; init; }

    public static MotivaException Validation(string message) => new(ErrorCode.ValidationError, message, "validation_error");
    public static MotivaException NotFound(string message) => new(ErrorCode.NotFound, message, "not_found");
    public static MotivaException Forbidden(string message) => new(ErrorCode.Forbidden, message, "forbidden");
    public static MotivaException Conflict(string message) => new(ErrorCode.Conflict, message, "conflict");
    public static MotivaException PreconditionFailed(string message) => new(ErrorCode.PreconditionFailed, message, "precondition_failed");
    public static MotivaException PreconditionRequired(string message) => new(ErrorCode.PreconditionRequired, message, "precondition_required");
    public static MotivaException InsufficientFunds(string message) => new(ErrorCode.InsufficientFunds, message, "insufficient_funds");
    public static MotivaException ResourceUnavailable(string message) => new(ErrorCode.ResourceUnavailable, message, "resource_unavailable");
}

/// <summary>Экономический отказ корректной операции (T04): 201 OperationOutcome{status:declined}.</summary>
public sealed record DeclinedOutcome(string Reason);
