using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Elsa.Workflows.UIHints;

namespace Elsa.TieredApproval.Web.Activities;

[Activity("TieredApproval", "Email", "Sends an HTML email through the configured SMTP server or pickup directory. Send failures fault the activity.")]
public class SendEmail : CodeActivity
{
    [Input(Description = "Recipient email address.")]
    public Input<string> To { get; set; } = null!;

    [Input(Description = "Optional CC recipients.", UIHint = InputUIHints.MultiText)]
    public Input<ICollection<string>?> Cc { get; set; } = null!;

    [Input(Description = "Subject line.")]
    public Input<string> Subject { get; set; } = null!;

    [Input(Description = "HTML body. Encode any user-supplied text before passing it in.", UIHint = InputUIHints.MultiLine)]
    public Input<string> HtmlBody { get; set; } = null!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sender = context.GetRequiredService<EmailSender>();
        await sender.SendAsync(To.Get(context), Cc.GetOrDefault(context), Subject.Get(context), HtmlBody.Get(context), context.CancellationToken);
    }
}
