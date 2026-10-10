using Motiva.Domain;

namespace Motiva.Application;

/// <summary>Проверка аудитории (B11): должны выполняться оба правила и пустые наборы не ограничивают.</summary>
public static class AudienceRules
{
    public static bool IsIncluded(Audience audience, IReadOnlyCollection<string> employeeTags)
    {
        var tags = new HashSet<string>(employeeTags, StringComparer.Ordinal);
        if (audience.Any.Count > 0 && !audience.Any.Any(tags.Contains))
        {
            return false;
        }

        if (audience.All.Count > 0 && !audience.All.All(tags.Contains))
        {
            return false;
        }

        if (audience.None.Any(tags.Contains))
        {
            return false;
        }

        return true;
    }
}

/// <summary>Сочетания полей grant по kind (D210); отсутствие области не означает wildcard.</summary>
public static class GrantRules
{
    public static void Validate(GrantKind kind, long? campaignId, long? resourceId, long? purchaseSystemId)
    {
        switch (kind)
        {
            case GrantKind.Progress:
                Require(campaignId, "campaignId", "progress");
                Reject(resourceId, "resourceId", "progress");
                Reject(purchaseSystemId, "purchaseSystemId", "progress");
                break;
            case GrantKind.Accrual:
                Require(campaignId, "campaignId", "accrual");
                Require(resourceId, "resourceId", "accrual");
                Reject(purchaseSystemId, "purchaseSystemId", "accrual");
                break;
            case GrantKind.Spend:
                Require(purchaseSystemId, "purchaseSystemId", "spend");
                Require(resourceId, "resourceId", "spend");
                Reject(campaignId, "campaignId", "spend");
                break;
        }
    }

    private static void Require(long? value, string field, string kind) =>
        _ = value is null
            ? throw MotivaException.Validation($"grant '{kind}' требует поле {field}")
            : true;

    private static void Reject(long? value, string field, string kind) =>
        _ = value is not null
            ? throw MotivaException.Validation($"grant '{kind}' запрещает поле {field}")
            : true;
}
