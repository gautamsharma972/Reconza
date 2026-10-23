using SettleFlow.Web.Data;

namespace SettleFlow.Web.Services;

public sealed record PlanLimits(string Name, string PriceIndia, string PriceIntl, int MaxFileMb, int MonthlyRows, int Workflows, int Seats, int? MonthlyRuns);

public static class Plans
{
    public static readonly IReadOnlyDictionary<Plan, PlanLimits> Limits = new Dictionary<Plan, PlanLimits>
    {
        // Keep the persisted enum value as Trial for backwards compatibility; this is a
        // permanent usage-limited free plan, so the customer-facing name is "Free".
        [Plan.Trial] = new("Free", "Free", "Free", 5, 5_000, 2, 1, 10),
        [Plan.Starter] = new("Starter", "₹999/mo", "$19/mo", 10, 50_000, 3, 2, null),
        [Plan.Business] = new("Business", "₹2,999/mo", "$49/mo", 25, 250_000, 10, 5, null),
        [Plan.Team] = new("Team", "₹6,999/mo", "$99/mo", 50, 1_000_000, 50, 15, null),
    };

    public static PlanLimits For(Plan p) => Limits[p];
}
