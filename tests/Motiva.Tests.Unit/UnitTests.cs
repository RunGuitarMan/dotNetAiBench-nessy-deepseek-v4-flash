using Motiva.Application;
using Motiva.Domain;
using Xunit;

namespace Motiva.Tests.Unit;

public class CalendarTests
{
    private static readonly CompanyCalendar Cal = new("Europe/Tallinn");

    [Fact]
    public void Season_is_calendar_year_in_company_tz()
    {
        // 2026-12-31 20:00Z = 22:00 EET (UTC+2) → 2026
        Assert.Equal(2026, Cal.SeasonOf(new DateTimeOffset(2026, 12, 31, 20, 0, 0, TimeSpan.Zero)));
        // 2026-12-31 23:30Z = 2027-01-01 01:30 EET → 2027
        Assert.Equal(2027, Cal.SeasonOf(new DateTimeOffset(2026, 12, 31, 23, 30, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("2026-01-15T10:00:00Z", Periodicity.Day, "2026-01-15T00:00:00+02:00")]
    [InlineData("2026-01-15T10:00:00Z", Periodicity.Month, "2026-01-01T00:00:00+02:00")]
    [InlineData("2026-05-20T10:00:00Z", Periodicity.Quarter, "2026-04-01T00:00:00+03:00")]
    [InlineData("2026-11-10T10:00:00Z", Periodicity.HalfYear, "2026-07-01T00:00:00+03:00")]
    [InlineData("2026-06-15T10:00:00Z", Periodicity.HalfYear, "2026-01-01T00:00:00+02:00")]
    public void Period_start_respects_company_calendar(string instant, Periodicity periodicity, string expectedUtc)
    {
        var start = Cal.PeriodStart(DateTimeOffset.Parse(instant), periodicity);
        Assert.Equal(DateTimeOffset.Parse(expectedUtc).ToUniversalTime(), start);
    }

    [Fact]
    public void Start_inclusive_end_exclusive()
    {
        // первая месячная граница: событие ровно в начале периода принадлежит периоду
        var start = Cal.PeriodStart(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.FromHours(2)), Periodicity.Month);
        Assert.Equal(new DateTimeOffset(2026, 1, 31, 22, 0, 0, TimeSpan.Zero), start);
    }
}

public class DomainRulesTests
{
    [Theory]
    [InlineData("ABC_123", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("toolong!@#", false)]
    public void Code_validation(string code, bool expected)
    {
        Assert.Equal(expected, DomainRules.IsValidCode(code));
    }

    [Theory]
    [InlineData("fin-team", true)]
    [InlineData("0", true)]
    [InlineData("UPPER", false)]
    public void Tag_validation(string tag, bool expected)
    {
        Assert.Equal(expected, DomainRules.IsValidTag(tag));
    }

    [Fact]
    public void Number_ranges()
    {
        Assert.True(DomainRules.IsValidPositive(1));
        Assert.True(DomainRules.IsValidPositive(1_000_000_000));
        Assert.False(DomainRules.IsValidPositive(0));
        Assert.False(DomainRules.IsValidPositive(1_000_000_001));
        Assert.True(DomainRules.IsValidAccumulated(0));
        Assert.True(DomainRules.IsValidAccumulated(1_000_000_000_000_000));
        Assert.False(DomainRules.IsValidAccumulated(1_000_000_000_000_001));
    }
}

public class AudienceRulesTests
{
    [Fact]
    public void Empty_sets_do_not_restrict()
    {
        var a = new Audience();
        Assert.True(AudienceRules.IsIncluded(a, new[] { "x" }));
        Assert.True(AudienceRules.IsIncluded(a, Array.Empty<string>()));
    }

    [Fact]
    public void Any_all_none_combined()
    {
        var a = new Audience { Any = new() { "fin" }, All = new() { "eu" }, None = new() { "blocked" } };
        Assert.True(AudienceRules.IsIncluded(a, new[] { "fin", "eu" }));
        Assert.False(AudienceRules.IsIncluded(a, new[] { "fin" })); // нет all
        Assert.False(AudienceRules.IsIncluded(a, new[] { "fin", "eu", "blocked" })); // none
        Assert.False(AudienceRules.IsIncluded(a, new[] { "eu" })); // нет any
    }
}

public class GrantRulesTests
{
    [Fact]
    public void Progress_requires_campaign_only()
    {
        GrantRules.Validate(GrantKind.Progress, 1, null, null);
        Assert.Throws<MotivaException>(() => GrantRules.Validate(GrantKind.Progress, null, null, null));
        Assert.Throws<MotivaException>(() => GrantRules.Validate(GrantKind.Progress, 1, 2, null));
    }

    [Fact]
    public void Accrual_requires_campaign_and_resource()
    {
        GrantRules.Validate(GrantKind.Accrual, 1, 2, null);
        Assert.Throws<MotivaException>(() => GrantRules.Validate(GrantKind.Accrual, 1, null, null));
        Assert.Throws<MotivaException>(() => GrantRules.Validate(GrantKind.Accrual, 1, 2, 3));
    }

    [Fact]
    public void Spend_requires_purchase_system_and_resource()
    {
        GrantRules.Validate(GrantKind.Spend, null, 2, 3);
        Assert.Throws<MotivaException>(() => GrantRules.Validate(GrantKind.Spend, null, null, 3));
        Assert.Throws<MotivaException>(() => GrantRules.Validate(GrantKind.Spend, 1, 2, 3));
    }
}
