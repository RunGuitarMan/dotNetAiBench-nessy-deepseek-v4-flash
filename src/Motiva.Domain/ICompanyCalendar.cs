namespace Motiva.Domain;

/// <summary>Портовый сервис календаря компании (B10/B13/B33). Учитывает пояс компании, возвращает UTC.</summary>
public interface ICompanyCalendar
{
    /// <summary>Календарный сезон (год) в поясе компании, в котором находится момент.</summary>
    int SeasonOf(DateTimeOffset instant);

    /// <summary>Начало периода (UTC), содержащего момент, с включённым началом и исключённым концом (B13c).</summary>
    DateTimeOffset PeriodStart(DateTimeOffset instant, Periodicity periodicity);
}

/// <summary>Реализация календаря в фиксированном поясе компании (B10: Europe/Tallinn).</summary>
public sealed class CompanyCalendar : ICompanyCalendar
{
    private readonly TimeZoneInfo _zone;

    public CompanyCalendar(string timeZoneId)
    {
        _zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
    }

    public int SeasonOf(DateTimeOffset instant) => InZone(instant).Year;

    public DateTimeOffset PeriodStart(DateTimeOffset instant, Periodicity periodicity)
    {
        var local = InZone(instant);
        var start = periodicity switch
        {
            Periodicity.Day => new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified),
            Periodicity.Month => new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified),
            Periodicity.Quarter => new DateTime(local.Year, QuarterStartMonth(local.Month), 1, 0, 0, 0, DateTimeKind.Unspecified),
            Periodicity.HalfYear => new DateTime(local.Year, local.Month <= 6 ? 1 : 7, 1, 0, 0, 0, DateTimeKind.Unspecified),
            Periodicity.Year => new DateTime(local.Year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
            _ => throw new ArgumentOutOfRangeException(nameof(periodicity)),
        };
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(start, _zone));
    }

    private DateTime InZone(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, _zone).DateTime;

    private static int QuarterStartMonth(int month) => month switch
    {
        <= 3 => 1,
        <= 6 => 4,
        <= 9 => 7,
        _ => 10,
    };
}
