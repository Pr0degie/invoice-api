using System.Globalization;
using FluentAssertions;
using InvoiceApi.Services;

namespace InvoiceApi.Tests;

public class BusinessDateTests
{
    [Theory]
    // New Year's Eve 23:30 UTC is already 00:30 on 1 January in Germany (CET, UTC+1)
    [InlineData("2026-12-31T23:30:00Z", 2027, 1, 1)]
    // Summer time (CEST, UTC+2): 22:30 UTC is 00:30 the next day
    [InlineData("2026-07-15T22:30:00Z", 2026, 7, 16)]
    // Winter time (CET, UTC+1): 22:30 UTC is still 23:30 the same day
    [InlineData("2026-01-15T22:30:00Z", 2026, 1, 15)]
    public void FromUtc_ReturnsTheGermanCalendarDay(string utc, int year, int month, int day)
    {
        var instant = DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture);

        BusinessDate.FromUtc(instant).Should().Be(new DateOnly(year, month, day));
    }
}
