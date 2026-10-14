namespace Elsa.TieredApproval.Web;

/// <summary>
/// The single source of truth for the approval chain: levels, offered actions, labels and transitions.
/// </summary>
public static class ApprovalLevels
{
    public const int Staff = 10;
    public const int SectionHead = 20;
    public const int DeptHead = 30;
    public const int DivisionHead = 40;

    public const string Approve = "Approve";
    public const string Reject = "Reject";
    public const string Revision = "Revision";

    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Withdrawn = "Withdrawn";

    private static readonly SortedDictionary<int, string> Names = new()
    {
        [Staff] = "Staff",
        [SectionHead] = "Section Head",
        [DeptHead] = "Dept Head",
        [DivisionHead] = "Division Head"
    };

    /// <summary>Levels that must be filled in by the requester (everything above Staff).</summary>
    public static IEnumerable<int> ApproverLevels => Names.Keys.Where(x => x > Staff);

    public static string Name(int level) => Names.TryGetValue(level, out var name) ? name : throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown approval level.");

    public static int? Next(int level) => Names.Keys.Where(x => x > level).Cast<int?>().FirstOrDefault();

    public static IEnumerable<int> Below(int level) => Names.Keys.Where(x => x < level);

    /// <summary>Approve and Reject are always offered; Revision is offered once per strictly lower level (so never at Staff).</summary>
    public static IEnumerable<(string Action, int? TargetLevel)> ActionsFor(int level) =>
        new (string, int?)[] { (Approve, null), (Reject, null) }.Concat(Below(level).Select(x => (Revision, (int?)x)));

    public static string ActionName(int level, string action) => (level, action) switch
    {
        (Staff, Approve) => "Resubmit",
        (Staff, Reject) => "Withdraw",
        _ => action
    };

    public static string ActionLabel(int level, string action, int? targetLevel) =>
        action == Revision && targetLevel is { } target ? $"Revision to {Name(target)} ({target})" : ActionName(level, action);

    /// <summary>Pure transition function: (current level, decision) -> (next level, status).</summary>
    public static (int Level, string Status) Transition(int level, string action, int? targetLevel) => action switch
    {
        Approve when level == DivisionHead => (level, Approved),
        Approve => (Next(level)!.Value, Pending),
        Reject => (level, level == Staff ? Withdrawn : Rejected),
        Revision when targetLevel is { } target && Below(level).Contains(target) => (target, Pending),
        _ => throw new InvalidOperationException($"Action '{action}' (target {targetLevel}) is not allowed at level {level}.")
    };
}
