using Motiva.Application;

namespace Motiva.Api;

/// <summary>Текущий инициатор из HttpContext (T03).</summary>
public sealed class HttpCurrentActor(IHttpContextAccessor accessor) : ICurrentActor
{
    public Actor? Actor => accessor.HttpContext?.Items["actor"] as Actor;
}

/// <summary>Системный контекст Worker — без бизнес-инициатора.</summary>
public sealed class SystemActor : ICurrentActor
{
    public Actor? Actor => null;
}
