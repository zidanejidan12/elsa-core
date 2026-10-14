using System.Net;
using Elsa.Extensions;
using Elsa.Http;
using Elsa.TieredApproval.Web.Activities;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Memory;
using static Elsa.TieredApproval.Web.ApprovalLevels;

namespace Elsa.TieredApproval.Web.Workflows;

/// <summary>
/// POST {BasePath}/tiered-approval/requests starts a request that climbs Section Head -> Dept Head -> Division Head.
/// Any approver can reject, or send it back to any lower level (including the requester), after which it climbs again from there.
/// </summary>
public class TieredApprovalWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Tiered approval";

        // Created with WithVariable(name, value) so they get workflow storage and survive suspend/resume.
        var request = builder.WithVariable<ApprovalRequest?>("Request", null);
        var errors = builder.WithVariable("Errors", new List<string>());
        var currentLevel = builder.WithVariable("CurrentLevel", SectionHead);
        var status = builder.WithVariable("Status", Pending);
        var decision = builder.WithVariable<ApprovalDecision?>("Decision", null);
        var history = builder.WithVariable("History", new List<ApprovalDecision>());

        // The raw body is only needed during the start burst, so it gets no variable storage.
        var body = new Variable<string?>("Body", null);
        builder.WithVariable(body);

        builder.Root = new Sequence
        {
            Activities =
            {
                new HttpEndpoint
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
                },
                new Inline(context =>
                {
                    request.Set(context, ApprovalRequest.Parse(body.Get(context), out var validationErrors));
                    errors.Set(context, validationErrors);
                }),
                new If(context => errors.Get(context)!.Count > 0)
                {
                    Then = new WriteHttpResponse
                    {
                        StatusCode = new(HttpStatusCode.BadRequest),
                        ContentType = new("application/json"),
                        Content = new(context => new { errors = errors.Get(context) })
                    },
                    Else = new Sequence
                    {
                        Activities =
                        {
                            // Sent before the 202, so an SMTP outage reaches the caller as Elsa's 500 instead of a 202 for a faulted request.
                            new SendEmail
                            {
                                To = new(context => request.Get(context)!.Requester!.Email!),
                                Subject = new(context => $"Submitted: {request.Get(context)!.Title}"),
                                HtmlBody = new(context => Templates.Submitted(request.Get(context)!, context.GetWorkflowExecutionContext().Id))
                            },
                            // Respond before the first blocking step: the HTTP context is gone once the workflow resumes.
                            new WriteHttpResponse
                            {
                                StatusCode = new(HttpStatusCode.Accepted),
                                ContentType = new("application/json"),
                                Content = new(context => new { workflowInstanceId = context.GetWorkflowExecutionContext().Id })
                            },
                            new While(context => status.Get(context) == Pending)
                            {
                                Body = new Sequence
                                {
                                    Activities =
                                    {
                                        new ApprovalStep
                                        {
                                            Level = new(currentLevel),
                                            AssigneeName = new(context => request.Get(context)!.AssigneeFor(currentLevel.Get(context)).DisplayName),
                                            AssigneeEmail = new(context => request.Get(context)!.AssigneeFor(currentLevel.Get(context)).Email!),
                                            Title = new(context => request.Get(context)!.Title!),
                                            Description = new(context => request.Get(context)!.Description),
                                            RequesterName = new(context => request.Get(context)!.Requester!.DisplayName),
                                            ContextNote = new(context => Templates.Note(history.Get(context)!.LastOrDefault())),
                                            History = new(context => history.Get(context)),
                                            Result = new(decision)
                                        },
                                        new Inline(context =>
                                        {
                                            var d = decision.Get(context)!;
                                            var (nextLevel, nextStatus) = Transition(d.Level, d.Action, d.TargetLevel);

                                            // A new list each time: never mutate the variable's (shared) default value.
                                            history.Set(context, new List<ApprovalDecision>(history.Get(context)!) { d });
                                            currentLevel.Set(context, nextLevel);
                                            status.Set(context, nextStatus);
                                        })
                                    }
                                }
                            },
                            new SendEmail
                            {
                                To = new(context => request.Get(context)!.Requester!.Email!),
                                Cc = new(context => ActedApprovers(request.Get(context)!, history.Get(context)!)),
                                Subject = new(context => $"{status.Get(context)}: {request.Get(context)!.Title}"),
                                HtmlBody = new(context => Templates.Final(request.Get(context)!, status.Get(context)!, history.Get(context)!, context.GetWorkflowExecutionContext().Id))
                            }
                        }
                    }
                }
            }
        };
    }

    private static ICollection<string>? ActedApprovers(ApprovalRequest request, IEnumerable<ApprovalDecision> history) =>
        history
            .Where(x => x.Level != Staff && !string.Equals(x.ActorEmail, request.Requester!.Email, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.ActorEmail)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
