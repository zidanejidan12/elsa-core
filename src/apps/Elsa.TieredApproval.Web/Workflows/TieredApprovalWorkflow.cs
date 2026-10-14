using System.Net;
using Elsa.Expressions.Models;
using Elsa.Extensions;
using Elsa.Http;
using Elsa.TieredApproval.Web.Activities;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Memory;
using static Elsa.TieredApproval.Web.ApprovalLevels;
using Endpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

namespace Elsa.TieredApproval.Web.Workflows;

/// <summary>
/// POST {BasePath}/tiered-approval/requests starts a request that climbs Section Head -> Dept Head -> Division Head.
/// Any approver can reject, or send it back to any lower level (including the requester), after which it climbs again from there.
/// A flowchart, so Elsa Studio can draw it. It has loops, so it needs token-based flowchart execution (see Program.cs).
/// </summary>
public class TieredApprovalWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Tiered approval";
        // Studio shows it but offers no publish: a version saved from Studio could not hold the C# delegates below.
        builder.AsReadonly();

        // Created with WithVariable(name, value) so they get workflow storage and survive suspend/resume.
        var request = builder.WithVariable<ApprovalRequest?>("Request", null);
        var errors = builder.WithVariable("Errors", new List<string>());
        // Every ApprovalStep reads it and writes it back with its decision appended.
        var history = builder.WithVariable("History", new List<ApprovalDecision>());

        // The raw body is only needed during the start burst, so it gets no variable storage.
        var body = new Variable<string?>("Body", null);
        builder.WithVariable(body);

        ApprovalDecision LastDecision(ExpressionExecutionContext context) => history.Get(context)!.Last();

        var submit = Node(new HttpEndpoint
        {
            Path = new("tiered-approval/requests"),
            SupportedMethods = new([HttpMethods.Post]),
            CanStartWorkflow = true,
            Authorize = new(true),
            // Raw string, parsed in the next step: Elsa's own JSON error path would leave the instance suspended forever.
            ParsedContent = new(body),
            // Too large: complete without content (instead of Elsa's 413, which also leaves the instance suspended) -> 400 below.
            RequestSizeLimit = new(ApprovalRequest.MaxBodyBytes),
            ExposeRequestTooLargeOutcome = true
        }, "SubmitRequest", "Submit request", 0, 1);

        // Parses and validates the body; only a valid request is kept in the Request variable.
        var isValid = Node(new FlowDecision(context =>
        {
            request.Set(context, ApprovalRequest.Parse(body.Get(context), out var validationErrors));
            errors.Set(context, validationErrors);
            return validationErrors.Count == 0;
        }), "ValidRequest", "Valid request?", 1, 1);

        var badRequest = Node(new WriteHttpResponse
        {
            StatusCode = new(HttpStatusCode.BadRequest),
            ContentType = new("application/json"),
            Content = new(context => new { errors = errors.Get(context) })
        }, "Respond400", "Respond 400", 2, 0);

        // Sent before the 202, so an SMTP outage reaches the caller as Elsa's 500 instead of a 202 for a faulted request.
        var submitted = Node(new SendEmail
        {
            To = new(context => request.Get(context)!.Requester!.Email!),
            Subject = new(context => $"Submitted: {request.Get(context)!.Title}"),
            HtmlBody = new(context => Templates.Submitted(request.Get(context)!, context.GetWorkflowExecutionContext().Id))
        }, "EmailSubmitted", "Email requester: submitted", 2, 1);

        // Respond before the first blocking step: the HTTP context is gone once the workflow resumes.
        var accepted = Node(new WriteHttpResponse
        {
            StatusCode = new(HttpStatusCode.Accepted),
            ContentType = new("application/json"),
            Content = new(context => new { workflowInstanceId = context.GetWorkflowExecutionContext().Id })
        }, "Respond202", "Respond 202", 3, 1);

        ApprovalStep Step(int level, int column, int row) => Node(new ApprovalStep
        {
            Level = new(level),
            AssigneeName = new(context => request.Get(context)!.AssigneeFor(level).DisplayName),
            AssigneeEmail = new(context => request.Get(context)!.AssigneeFor(level).Email!),
            Title = new(context => request.Get(context)!.Title!),
            Description = new(context => request.Get(context)!.Description),
            RequesterName = new(context => request.Get(context)!.Requester!.DisplayName),
            ContextNote = new(context => Templates.Note(history.Get(context)!.LastOrDefault())),
            History = new(history),
            UpdatedHistory = new(history)
        }, Name(level).Replace(" ", ""), $"{Name(level)} ({level})", column, row);

        var staff = Step(Staff, 3, 2);
        var sectionHead = Step(SectionHead, 4, 1);
        var deptHead = Step(DeptHead, 5, 1);
        var divisionHead = Step(DivisionHead, 6, 1);

        var sendBack = Node(new FlowSwitch
        {
            Cases = [.. new[] { Staff, SectionHead, DeptHead }.Select(level => new FlowSwitchCase(level.ToString(), context => LastDecision(context).TargetLevel == level))]
        }, "SendBackTo", "Send back to", 7, 2);

        SendEmail Final(string id, string name, int row, Func<ExpressionExecutionContext, string> status) => Node(new SendEmail
        {
            To = new(context => request.Get(context)!.Requester!.Email!),
            Cc = new(context => ActedApprovers(request.Get(context)!, history.Get(context)!)),
            Subject = new(context => $"{status(context)}: {request.Get(context)!.Title}"),
            HtmlBody = new(context => Templates.Final(request.Get(context)!, status(context), history.Get(context)!, context.GetWorkflowExecutionContext().Id))
        }, id, name, 7, row);

        var approved = Final("EmailApproved", "Email: approved", 1, _ => Approved);
        var rejected = Final("EmailRejected", "Email: rejected", 0, context => LastDecision(context).Level == Staff ? Withdrawn : Rejected);

        builder.Root = new Flowchart
        {
            Activities = { submit, isValid, badRequest, submitted, accepted, staff, sectionHead, deptHead, divisionHead, sendBack, approved, rejected },
            Connections =
            {
                Edge(submit, "Done", isValid),
                Edge(submit, "Request too large", isValid),
                Edge(isValid, "False", badRequest),
                Edge(isValid, "True", submitted),
                Edge(submitted, "Done", accepted),
                Edge(accepted, "Done", sectionHead),

                Edge(sectionHead, Approve, deptHead),
                Edge(deptHead, Approve, divisionHead),
                Edge(divisionHead, Approve, approved),
                // At Staff, Approve is "Resubmit" and Reject is "Withdraw".
                Edge(staff, Approve, sectionHead),

                Edge(staff, Reject, rejected),
                Edge(sectionHead, Reject, rejected),
                Edge(deptHead, Reject, rejected),
                Edge(divisionHead, Reject, rejected),

                Edge(sectionHead, Revision, sendBack),
                Edge(deptHead, Revision, sendBack),
                Edge(divisionHead, Revision, sendBack),
                Edge(sendBack, Staff.ToString(), staff),
                Edge(sendBack, SectionHead.ToString(), sectionHead),
                Edge(sendBack, DeptHead.ToString(), deptHead)
            }
        };
    }

    /// <summary>Sets the id, the label and the designer position (Studio reads metadata.designer.position) on a flowchart node.</summary>
    private static T Node<T>(T activity, string id, string name, int column, int row) where T : IActivity
    {
        activity.Id = id;
        activity.Name = name;
        activity.Metadata["designer"] = new Dictionary<string, object> { ["position"] = new Dictionary<string, object> { ["x"] = column * 360, ["y"] = row * 160 } };
        return activity;
    }

    private static Connection Edge(IActivity source, string outcome, IActivity target) => new(new Endpoint(source, outcome), new Endpoint(target));

    private static ICollection<string>? ActedApprovers(ApprovalRequest request, IEnumerable<ApprovalDecision> history) =>
        history
            .Where(x => x.Level != Staff && !string.Equals(x.ActorEmail, request.Requester!.Email, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.ActorEmail)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
