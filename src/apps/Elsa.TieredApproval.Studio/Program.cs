using Elsa.Studio.Authentication.Abstractions.Models;
using Elsa.Studio.Authentication.ElsaIdentity.BlazorServer.Extensions;
using Elsa.Studio.Authentication.ElsaIdentity.HttpMessageHandlers;
using Elsa.Studio.Authentication.ElsaIdentity.UI.Extensions;
using Elsa.Studio.Authentication.Themes.Extensions;
using Elsa.Studio.Authentication.UI.Extensions;
using Elsa.Studio.Authentication.UI.Options;
using Elsa.Studio.Contracts;
using Elsa.Studio.Core.BlazorServer.Extensions;
using Elsa.Studio.Dashboard.Extensions;
using Elsa.Studio.Extensions;
using Elsa.Studio.Models;
using Elsa.Studio.Options;
using Elsa.Studio.Shell.Extensions;
using Elsa.Studio.Workflows.ActivityPickers.Treeview;
using Elsa.Studio.Workflows.Dashboard.Extensions;
using Elsa.Studio.Workflows.Designer.Extensions;
using Elsa.Studio.Workflows.Designer.Options;
using Elsa.Studio.Workflows.Extensions;
using Microsoft.Extensions.Options;

// Trimmed copy of elsa-studio 3.9.0 src/hosts/Elsa.Studio.Host.Server/Program.cs: ElsaIdentity login + dashboard + workflows only.
var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor(options =>
{
    options.RootComponents.RegisterCustomElsaStudioElements();
    options.RootComponents.MaxJSRootComponents = 1000;
});

// Elsa Identity: username/password against the Elsa backend, login UI at /login.
builder.Services.AddStudioAuthenticationMode(options => options.Provider = StudioAuthenticationProvider.ElsaIdentity);
builder.Services.AddElsaIdentity();
builder.Services.AddElsaIdentityUI();

var backendApiConfig = new BackendApiConfig
{
    ConfigureBackendOptions = options => configuration.GetSection("Backend").Bind(options),
    ConfigureHttpClientBuilder = options =>
    {
        options.AuthenticationHandler = typeof(ElsaIdentityAuthenticatingApiHttpMessageHandler);
        options.ConfigureHttpClient = (_, client) => client.Timeout = TimeSpan.FromHours(1);
    },
};

builder.Services.AddCore(options => configuration.GetSection(StudioThemeOptions.SectionName).Bind(options));
builder.Services.AddShell(options => configuration.GetSection("Shell").Bind(options));
builder.Services
    .AddAuthenticationUI(configuration.GetSection(LoginThemeOptions.SectionName))
    .AddElsaStudioLoginThemes();
builder.Services.AddRemoteBackend(backendApiConfig);
builder.Services.AddDashboardModule(backendApiConfig);
builder.Services.AddWorkflowsModule(backendApiConfig);
builder.Services.AddWorkflowsDashboardModule();
builder.Services.AddScoped<IActivityPickerComponentProvider, TreeviewActivityPickerComponentProvider>();
builder.Services.Configure<DesignerOptions>(configuration.GetSection("DesignerOptions"));
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 5 * 1024 * 1000); // 5MB

var app = builder.Build();

_ = app.Services.GetRequiredService<IOptions<LoginThemeOptions>>().Value; // fail fast on a bad login theme, as upstream does

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
app.Run();
