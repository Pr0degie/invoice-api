namespace InvoiceApi.Models.Dtos.Stats;

public record StatsDto(
    decimal TotalOutstanding,
    decimal TotalPaid,
    decimal TotalDraft,
    int OverdueCount,
    int DraftCount,
    int SentCount,
    int PaidCount,
    IReadOnlyList<MonthlyRevenueDto> MonthlyRevenue,
    IReadOnlyList<TopRecipientDto> TopRecipients
);

public record MonthlyRevenueDto(string Month, decimal Paid, decimal Sent);

public record TopRecipientDto(string Name, decimal Total, int Count);
