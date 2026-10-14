using System.Net;
using System.Text;
using System.Text.Json;

namespace Elsa.TieredApproval.Web;

/// <summary>All HTML produced by the app. Every user-supplied value goes through <see cref="E"/>.</summary>
public static class Templates
{
    private const string Style = "font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#222";
    private const string ButtonStyle = "display:inline-block;margin:4px 8px 4px 0;padding:8px 16px;border-radius:4px;background:#0b5cad;color:#fff;text-decoration:none";

    public static string E(object? value) => WebUtility.HtmlEncode(value?.ToString() ?? "");

    /// <summary>Plain-text context line for the next assignee, e.g. "Revision requested by Dept Head (Dana): fix budget".</summary>
    public static string? Note(ApprovalDecision? last)
    {
        if (last == null)
            return null;

        var what = (last.Level, last.Action) switch
        {
            (_, ApprovalLevels.Revision) => "Revision requested",
            (ApprovalLevels.Staff, ApprovalLevels.Approve) => "Resubmitted",
            (_, ApprovalLevels.Approve) => "Approved",
            _ => ApprovalLevels.ActionName(last.Level, last.Action)
        };
        var note = $"{what} by {ApprovalLevels.Name(last.Level)} ({last.ActorName})";
        return string.IsNullOrWhiteSpace(last.Comment) ? note : $"{note}: {last.Comment}";
    }

    public static string Submitted(ApprovalRequest request, string instanceId) =>
        $"""
         <div style="{Style}">
         <p>Hello {E(request.Requester!.DisplayName)},</p>
         <p>Your request <b>{E(request.Title)}</b> was submitted and is now with the {E(ApprovalLevels.Name(ApprovalLevels.SectionHead))} ({E(request.AssigneeFor(ApprovalLevels.SectionHead).DisplayName)}).</p>
         {Summary(request, instanceId)}
         </div>
         """;

    public static string ApprovalNeeded(int level, string assigneeName, string title, string? description, string? requesterName, string? contextNote, ICollection<ApprovalDecision> history, IEnumerable<(string Label, string Url)> links, string instanceId, DateTimeOffset expiresAt)
    {
        var buttons = string.Concat(links.Select(x => $"""<a href="{E(x.Url)}" style="{ButtonStyle}">{E(x.Label)}</a>"""));
        var note = string.IsNullOrWhiteSpace(contextNote) ? "" : $"""<p style="padding:8px;background:#fff4ce">{E(contextNote)}</p>""";
        var staffHint = level == ApprovalLevels.Staff
            ? "<p>The title and description cannot be edited: put your changes in the comment when you resubmit, or withdraw and submit a new request.</p>"
            : "";

        return $"""
                <div style="{Style}">
                <p>Hello {E(assigneeName)},</p>
                <p>Your action is needed as <b>{E(ApprovalLevels.Name(level))} ({level})</b> on the request <b>{E(title)}</b> from {E(requesterName)}.</p>
                {note}
                <p style="white-space:pre-wrap">{E(description)}</p>
                {staffHint}
                <p>{buttons}</p>
                {(history.Count == 0 ? "" : $"<p>History so far:</p>{HistoryTable(history)}")}
                <p style="color:#666;font-size:12px">Reference: {E(instanceId)}. Each link opens a confirmation page, works once, and expires {E(expiresAt.ToString("u"))}.
                Anyone holding this email can act on the request, so do not forward it.</p>
                </div>
                """;
    }

    public static string Final(ApprovalRequest request, string status, IReadOnlyCollection<ApprovalDecision> history, string instanceId) =>
        $"""
         <div style="{Style}">
         <p>Hello {E(request.Requester!.DisplayName)},</p>
         <p>Your request <b>{E(request.Title)}</b> is <b>{E(status)}</b>.</p>
         {Summary(request, instanceId)}
         {HistoryTable(history)}
         </div>
         """;

    /// <summary>
    /// Landing page for an emailed link. A GET only renders; the decision is sent by an explicit POST, so mail link scanners cannot act
    /// (the host also refuses GET on the resume URL embedded below).
    /// </summary>
    public static string ConfirmPage(string label, string? levelName, string? title, string resumeUrl) =>
        $$"""
          <!DOCTYPE html>
          <html><head><meta charset="utf-8"><meta name="robots" content="noindex"><title>Confirm: {{E(label)}}</title></head>
          <body style="{{Style}};max-width:640px;margin:40px auto">
          <h2>Confirm: {{E(label)}}</h2>
          <p>{{E(levelName)}} decision on the request <b>{{E(title)}}</b>.</p>
          <form id="form">
          <p><label for="comment">Comment (optional)</label><br><textarea id="comment" rows="5" cols="60" maxlength="2000"></textarea></p>
          <p><button type="submit" style="{{ButtonStyle}};border:0;cursor:pointer">Confirm {{E(label)}}</button></p>
          </form>
          <p id="result" role="status"></p>
          <script>
          document.getElementById('form').addEventListener('submit', async e => {
            e.preventDefault();
            const button = e.target.querySelector('button');
            const result = document.getElementById('result');
            button.disabled = true;
            try {
              const response = await fetch({{JsonSerializer.Serialize(resumeUrl)}}, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ input: { comment: document.getElementById('comment').value } })
              });
              result.textContent = response.ok ? 'Thank you, your decision was recorded. You can close this page.' : 'Something went wrong (HTTP ' + response.status + '). Please try again.';
              if (response.ok) document.getElementById('form').remove(); else button.disabled = false;
            } catch {
              result.textContent = 'Network error. Please try again.';
              button.disabled = false;
            }
          });
          </script>
          </body></html>
          """;

    public static string LinkNotValidPage() =>
        $"""
         <!DOCTYPE html>
         <html><head><meta charset="utf-8"><title>Link no longer valid</title></head>
         <body style="{Style};max-width:640px;margin:40px auto">
         <h2>This link is no longer valid</h2>
         <p>It has already been used, expired, or another action was already taken on this step.</p>
         </body></html>
         """;

    private static string HistoryTable(IEnumerable<ApprovalDecision> history)
    {
        var rows = new StringBuilder();

        foreach (var d in history)
        {
            var target = d.TargetLevel is { } t ? $"{ApprovalLevels.Name(t)} ({t})" : "";
            rows.Append($"<tr><td>{d.Level}</td><td>{E(ApprovalLevels.Name(d.Level))}</td><td>{E(d.ActorName)} &lt;{E(d.ActorEmail)}&gt;</td><td>{E(ApprovalLevels.ActionName(d.Level, d.Action))}</td><td>{E(target)}</td><td>{E(d.Comment)}</td><td>{E(d.DecidedAt.ToString("u"))}</td></tr>");
        }

        return $"""
                <table border="1" cellpadding="4" cellspacing="0" style="border-collapse:collapse">
                <thead><tr><th>Level</th><th>Role</th><th>Actor</th><th>Action</th><th>Target</th><th>Comment</th><th>Timestamp (UTC)</th></tr></thead>
                <tbody>{rows}</tbody>
                </table>
                """;
    }

    private static string Summary(ApprovalRequest request, string instanceId) =>
        $"""<p style="white-space:pre-wrap">{E(request.Description)}</p><p style="color:#666;font-size:12px">Reference: {E(instanceId)}</p>""";
}
