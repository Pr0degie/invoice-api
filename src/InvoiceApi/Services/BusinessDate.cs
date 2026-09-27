namespace InvoiceApi.Services;

/// <summary>
/// Calendar day for business dates (issue/due/paid date, "today" for overdue and stats).
/// Invoices are German documents, so the day is the one in Europe/Berlin — never the
/// server clock, which runs in UTC inside the container (§ 14 UStG: Ausstellungsdatum;
/// around midnight UTC would otherwise stamp yesterday, on 1 January the old year).
/// Timestamps (CreatedAt, RevokedAt, …) stay UTC and don't go through this.
/// </summary>
public static class BusinessDate
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    public static DateOnly Today(TimeProvider? clock = null) => FromUtc((clock ?? TimeProvider.System).GetUtcNow());

    public static DateOnly FromUtc(DateTimeOffset utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, Zone).DateTime);
}
