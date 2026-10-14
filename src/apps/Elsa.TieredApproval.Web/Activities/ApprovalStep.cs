using System.Text.Json;
using Elsa.Extensions;
using Elsa.Http;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;

namespace Elsa.TieredApproval.Web.Activities;

/// <summary>
/// Emails the assignee one link per allowed action and waits until one of them is used.
/// Each action is its own bookmark; the action and target level live in the bookmark metadata (server side), never in the link.
/// </summary>
[Activity("TieredApproval", "Approvals", "Emails the assignee one link per allowed action (Approve, Reject, Revision to a lower level) and waits for the decision.")]
[FlowNode(ApprovalLevels.Approve, ApprovalLevels.Reject, ApprovalLevels.Revision)]
public class ApprovalStep : Activity<ApprovalDecision>
{
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromDays(7);
    private const int MaxCommentLength = 2000;

    [Input(Description = "Approval level: 10 Staff, 20 Section Head, 30 Dept Head, 40 Division Head.")]
    public Input<int> Level { get; set; } = null!;

    [Input(Description = "Name of the person who must act.")]
    public Input<string> AssigneeName { get; set; } = null!;

    [Input(Description = "Email address the action links are sent to.")]
    public Input<string> AssigneeEmail { get; set; } = null!;

    [Input(Description = "Request title.")]
    public Input<string> Title { get; set; } = null!;

    [Input(Description = "Request description.")]
    public Input<string?> Description { get; set; } = null!;

    [Input(Description = "Name of the requester.")]
    public Input<string?> RequesterName { get; set; } = null!;

    [Input(Description = "Optional note shown to the assignee, e.g. why the request came back.")]
    public Input<string?> ContextNote { get; set; } = null!;

    [Input(Description = "Decisions taken so far: shown to the assignee as a table, and the base of Updated history.")]
    public Input<ICollection<ApprovalDecision>?> History { get; set; } = null!;

    [Output(Description = "A new list: History plus this step's decision. Bind it to the history variable to record the decision.")]
    public Output<ICollection<ApprovalDecision>>? UpdatedHistory { get; set; }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var level = Level.Get(context);
        var title = Title.Get(context);
        var urls = context.GetRequiredService<IAbsoluteUrlProvider>();
        var expiresAt = context.WorkflowExecutionContext.SystemClock.UtcNow + LinkLifetime;
        var links = new List<(string Label, string Url)>();

        foreach (var (action, targetLevel) in ApprovalLevels.ActionsFor(level))
        {
            var label = ApprovalLevels.ActionLabel(level, action, targetLevel);
            var bookmark = context.CreateBookmark(new CreateBookmarkArgs
            {
                Callback = OnDecisionAsync,
                Metadata = new Dictionary<string, string>
                {
                    ["action"] = action,
                    ["targetLevel"] = targetLevel?.ToString() ?? "",
                    // Shown on the confirm page so the approver can see which request the link acts on.
                    ["label"] = label,
                    ["levelName"] = ApprovalLevels.Name(level),
                    ["title"] = title
                }
            });

            // The signed token resumes exactly this bookmark. Links point at the host's confirmation page, not at the
            // resume endpoint itself, so mail scanners that prefetch GET links cannot take a decision.
            var token = context.GenerateBookmarkTriggerToken(bookmark.Id, LinkLifetime);
            links.Add((label, urls.ToAbsoluteUrl($"approvals/confirm?t={Uri.EscapeDataString(token)}").ToString()));
        }

        var body = Templates.ApprovalNeeded(level, AssigneeName.Get(context), title, Description.GetOrDefault(context), RequesterName.GetOrDefault(context), ContextNote.GetOrDefault(context), History.GetOrDefault(context) ?? [], links, context.WorkflowExecutionContext.Id, expiresAt);
        var subject = $"Action needed ({ApprovalLevels.Name(level)}): {title}";
        await context.GetRequiredService<EmailSender>().SendAsync(AssigneeEmail.Get(context), null, subject, body, context.CancellationToken);
    }

    // Resolved by method name when a bookmark is resumed, so it must stay a named method (no lambda).
    private async ValueTask OnDecisionAsync(ActivityExecutionContext context)
    {
        var metadata = context.WorkflowExecutionContext.ResumedBookmarkContext?.Bookmark.Metadata ?? throw new InvalidOperationException("Resumed without bookmark metadata.");
        var action = metadata["action"];

        // Resume input values arrive as JsonElement. No strict conversion: a non-string comment (true, {}) must not fault the request.
        context.WorkflowInput.TryGetValue("comment", out var raw);
        var comment = raw switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            string text => text,
            _ => null
        };

        var decision = new ApprovalDecision
        {
            Level = Level.Get(context),
            Action = action,
            TargetLevel = metadata.TryGetValue("targetLevel", out var targetText) && int.TryParse(targetText, out var target) ? target : null,
            Comment = comment is { Length: > MaxCommentLength } ? comment[..MaxCommentLength] : comment,
            ActorName = AssigneeName.Get(context),
            ActorEmail = AssigneeEmail.Get(context),
            DecidedAt = context.WorkflowExecutionContext.SystemClock.UtcNow
        };

        Result.Set(context, decision);
        // A new list, never the input's: that may be the variable's shared default value.
        UpdatedHistory.Set(context, new List<ApprovalDecision>(History.GetOrDefault(context) ?? []) { decision });

        // Completing clears the sibling bookmarks, so the other links of this email stop working too.
        await context.CompleteActivityWithOutcomesAsync(action);
    }
}
