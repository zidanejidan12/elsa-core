using System.Net.Mail;
using System.Text.Json;

namespace Elsa.TieredApproval.Web;

public class Person
{
    public string? Name { get; set; }
    public string? Email { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Email ?? "" : Name;
}

/// <summary>The JSON body posted to start a request.</summary>
public class ApprovalRequest
{
    public const int MaxBodyBytes = 64 * 1024;
    public const int MaxTitleLength = 200;
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 4000;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string? Title { get; set; }
    public string? Description { get; set; }
    public Person? Requester { get; set; }
    public Dictionary<int, Person>? Approvers { get; set; }

    /// <summary>The person acting at the given level: the requester at Staff, otherwise the configured approver.</summary>
    public Person AssigneeFor(int level) => level == ApprovalLevels.Staff ? Requester! : Approvers![level];

    /// <summary>
    /// Trust-boundary parsing and validation of the raw start body. Returns the request when valid, otherwise null and the errors.
    /// Parsing here (not in HttpEndpoint) keeps malformed JSON on the app's 400 path, so the instance still finishes.
    /// </summary>
    public static ApprovalRequest? Parse(string? json, out List<string> errors)
    {
        ApprovalRequest? request;

        try
        {
            request = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ApprovalRequest>(json, JsonOptions);
        }
        catch (JsonException)
        {
            errors = ["The body must be a JSON object with title, description, requester and approvers."];
            return null;
        }

        errors = Validate(request);
        return errors.Count == 0 ? request : null;
    }

    private static List<string> Validate(ApprovalRequest? request)
    {
        if (request == null)
            return [$"A JSON body of at most {MaxBodyBytes / 1024} KB is required."];

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Title))
            errors.Add("title is required.");
        else if (request.Title.Length > MaxTitleLength)
            errors.Add($"title must be at most {MaxTitleLength} characters.");

        if (request.Description?.Length > MaxDescriptionLength)
            errors.Add($"description must be at most {MaxDescriptionLength} characters.");

        ValidatePerson(request.Requester, "requester", errors);

        foreach (var level in ApprovalLevels.ApproverLevels)
            ValidatePerson(request.Approvers?.GetValueOrDefault(level), $"approvers.{level} ({ApprovalLevels.Name(level)})", errors);

        foreach (var level in request.Approvers?.Keys.Except(ApprovalLevels.ApproverLevels) ?? [])
            errors.Add($"approvers.{level} is not an approval level; use {string.Join(", ", ApprovalLevels.ApproverLevels)}.");

        return errors;
    }

    private static void ValidatePerson(Person? person, string field, List<string> errors)
    {
        if (!IsValidEmail(person?.Email))
            errors.Add($"{field}: email is required and must be a valid email address.");

        if (person?.Name?.Length > MaxNameLength)
            errors.Add($"{field}: name must be at most {MaxNameLength} characters.");
    }

    // Bare addresses only: rejects display-name forms ("Eve <a@b.c>") and anything MailAddress would rewrite.
    private static bool IsValidEmail(string? email) => MailAddress.TryCreate(email, out var address) && address.Address == email;
}

/// <summary>One decision taken at one level; the workflow keeps the full list as the request history.</summary>
public class ApprovalDecision
{
    public int Level { get; set; }
    public string Action { get; set; } = "";
    public int? TargetLevel { get; set; }
    public string? Comment { get; set; }
    public string ActorName { get; set; } = "";
    public string ActorEmail { get; set; } = "";
    public DateTimeOffset DecidedAt { get; set; }
}
