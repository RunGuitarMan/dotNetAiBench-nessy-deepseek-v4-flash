using System.Text.RegularExpressions;

namespace Motiva.Domain;

/// <summary>Чистые правила входных значений (T05): коды, теги, диапазоны, числа.</summary>
public static partial class DomainRules
{
    public const long MaxSingleValue = 1_000_000_000L;      // цель/delta/сумма/очки ≤ 1e9
    public const long MaxAccumulated = 1_000_000_000_000_000L; // баланс/бюджет ≤ 1e15
    public const int MaxMasterId = int.MaxValue;

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex CodeRegex();

    [GeneratedRegex(@"^[a-z0-9-]{1,32}$")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^[ -~]{1,100}$")]
    private static partial Regex SourceNoRegex();

    public static bool IsValidCode(string? code) =>
        code is not null && CodeRegex().IsMatch(code);

    public static bool IsValidTag(string? tag) =>
        tag is not null && TagRegex().IsMatch(tag);

    public static bool IsValidSourceNo(string? no) =>
        no is not null && no.Length is >= 1 and <= 100 && SourceNoRegex().IsMatch(no);

    public static bool IsValidName(string? name) => name is { Length: >= 1 and <= 200 };

    public static bool IsValidDescription(string? description) => description is null || description.Length <= 2000;

    /// <summary>Отдельное положительное входное значение (1..1e9).</summary>
    public static bool IsValidPositive(long value) => value is >= 1 and <= MaxSingleValue;

    /// <summary>Накопительное значение (0..1e15).</summary>
    public static bool IsValidAccumulated(long value) => value is >= 0 and <= MaxAccumulated;

    public static bool IsValidMasterId(int masterId) => masterId > 0;
}
