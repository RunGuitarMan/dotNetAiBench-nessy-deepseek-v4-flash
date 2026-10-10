namespace Motiva.Domain;

/// <summary>Компания (B01/B02).</summary>
public sealed class Company
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Часовой пояс компании (B10), например Europe/Tallinn.</summary>
    public string TimeZoneId { get; set; } = "Europe/Tallinn";
}

/// <summary>Сотрудник (B01). Уникален по (company, masterId); не удаляется.</summary>
public sealed class Employee
{
    public long Id { get; set; }
    public Guid CompanyId { get; set; }
    public int MasterId { get; set; }
    public bool IsActive { get; set; } = true;
    public List<string> Tags { get; set; } = new();
}

/// <summary>Личный кошелёк сотрудника (B01d/B22). Ровно один на сотрудника.</summary>
public sealed class Wallet
{
    public long Id { get; set; }
    public long EmployeeId { get; set; }
}

/// <summary>Баланс кошелька по ресурсу (B22). Безотрицательный, ≤1e15.</summary>
public sealed class WalletBalance
{
    public long WalletId { get; set; }
    public long ResourceId { get; set; }
    public long Balance { get; set; }
}

/// <summary>Ресурс (B06). Код уникален в компании без учёта регистра; не освобождается.</summary>
public sealed class Resource
{
    public long Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ResourceStatus Status { get; set; } = ResourceStatus.Active;
}
