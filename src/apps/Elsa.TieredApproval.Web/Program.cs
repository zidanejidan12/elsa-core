using Elsa.Extensions;
using Elsa.Http;
using Elsa.Http.Options;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;
using Elsa.SasTokens.Contracts;
using Elsa.TieredApproval.Web;
using Elsa.Workflows.Api;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Distributed.Extensions;
using Elsa.Workflows.Runtime.Filters;
using Medallion.Threading.FileSystem;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var contentRoot = builder.Environment.ContentRootPath;
var isDevelopment = builder.Environment.IsDevelopment();

var appData = Directory.CreateDirectory(Path.Combine(contentRoot, "App_Data")).FullName;
var connectionString = $"Data Source={Path.Combine(appData, "elsa.sqlite.db")};Cache=Shared;";

builder.Services.Configure<SmtpOptions>(configuration.GetSection("Smtp"));
builder.Services.PostConfigure<SmtpOptions>(options =>
{
    if (!string.IsNullOrWhiteSpace(options.PickupDirectory))
        options.PickupDirectory = Path.GetFullPath(options.PickupDirectory, contentRoot);
});
builder.Services.AddSingleton<EmailSender>();

builder.Services.AddElsa(elsa => elsa
    .AddActivitiesFrom<Program>()
    .AddWorkflowsFrom<Program>()
    // Lets the serialized workflow definition resolve its typed variables (otherwise they degrade to object with a warning).
    .AddVariableTypeAndAlias<ApprovalRequest>(nameof(ApprovalRequest), "Tiered approval")
    .AddVariableTypeAndAlias<ApprovalDecision>(nameof(ApprovalDecision), "Tiered approval")
    .UseIdentity(identity =>
    {
        identity.TokenOptions += options => configuration.GetSection("Identity:Tokens").Bind(options);

        // Development only: user admin/password and "Authorization: ApiKey 00000000-0000-0000-0000-000000000000".
        if (isDevelopment)
            identity.UseDevelopmentAdminUserProvider();
    })
    .UseDefaultAuthentication(auth =>
    {
        if (isDevelopment)
            auth.UseDevelopmentAdminApiKey();
    })
    .UseWorkflowManagement(management => management.UseEntityFrameworkCore(ef => ef.UseSqlite(connectionString)))
    .UseWorkflowRuntime(runtime =>
    {
        runtime.UseEntityFrameworkCore(ef => ef.UseSqlite(connectionString));

        // Every resume runs inside a per-instance lock, so two links of the same email used at the same moment cannot both
        // take effect: the second run finds its bookmark gone and does nothing.
        runtime.UseDistributedRuntime();
        // ponytail: file locks coordinate one node only; plug in a Redis/SQL lock provider before running several instances.
        runtime.DistributedLockingOptions = options => options.AllowLocalLockProviderInDistributedRuntime = true;
        // Next to the database, not under the process working directory (which differs for services and scheduled tasks).
        runtime.DistributedLockProvider = _ => new FileDistributedSynchronizationProvider(new DirectoryInfo(Path.Combine(appData, "locks")));
    })
    .UseWorkflowsApi()
    .UseHttp(http => http.ConfigureHttpOptions = options => configuration.GetSection("Http").Bind(options)));

var app = builder.Build();

var routePrefix = app.Services.GetRequiredService<IOptions<ApiEndpointOptions>>().Value.RoutePrefix;
var resumePath = new PathString($"/{routePrefix.Trim('/')}/bookmarks/resume");
var workflowsPath = app.Services.GetRequiredService<IOptions<HttpActivityOptions>>().Value.BasePath ?? "/workflows";

app.Use((context, next) =>
{
    // Elsa also resumes on GET. Only the confirm page's POST may act, so a scanner that fetches a URL found in the page cannot.
    if (context.Request.Path.Equals(resumePath, StringComparison.OrdinalIgnoreCase) && !HttpMethods.IsPost(context.Request.Method))
    {
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        return Task.CompletedTask;
    }

    // The start run sends email before its first commit, and the 202 completes only after that commit. A client that gives up
    // or disconnects meanwhile must not cancel the run half way and lose the instance (Elsa's resume endpoint uses
    // CancellationToken.None for the same reason). Shutdown still cancels.
    if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments(workflowsPath))
        context.RequestAborted = app.Lifetime.ApplicationStopping;

    return next(context);
});

app.MapWorkflowsApi(routePrefix);
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseJsonSerializationErrorHandler();
app.UseWorkflows();

// Landing page for emailed action links. Rendering only: the decision is taken by the page's POST to the Elsa resume endpoint.
// The label, level and title come from the server-side bookmark, so a tampered link cannot relabel an action.
app.MapGet("/approvals/confirm", async (string? t, ITokenService tokens, IBookmarkStore bookmarks, IAbsoluteUrlProvider urls, CancellationToken cancellationToken) =>
{
    var bookmark = !string.IsNullOrWhiteSpace(t) && tokens.TryDecryptToken<BookmarkTokenPayload>(t, out var payload)
        ? await bookmarks.FindAsync(new BookmarkFilter { BookmarkId = payload.BookmarkId, WorkflowInstanceId = payload.WorkflowInstanceId }, cancellationToken)
        : null;

    if (bookmark?.Metadata is not { } metadata || !metadata.TryGetValue("label", out var label))
        return Results.Content(Templates.LinkNotValidPage(), "text/html; charset=utf-8", statusCode: StatusCodes.Status410Gone);

    // Built like the emailed link, so a path in Http:BaseUrl (sub-path deployment) is kept.
    var resumeUrl = urls.ToAbsoluteUrl($"{resumePath.Value!.TrimStart('/')}?t={Uri.EscapeDataString(t!)}").ToString();
    metadata.TryGetValue("levelName", out var levelName);
    metadata.TryGetValue("title", out var title);
    return Results.Content(Templates.ConfirmPage(label, levelName, title, resumeUrl), "text/html; charset=utf-8");
});

app.Run();
