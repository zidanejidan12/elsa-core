# Elsa.TieredApproval.Studio

Minimal Elsa Studio 3.9.0 (Blazor Server) host for watching the tiered-approval workflows run on the local
`Elsa.TieredApproval.Web` server. It is a trimmed copy of the upstream
[`Elsa.Studio.Host.Server`](https://github.com/elsa-workflows/elsa-studio/tree/3.9.0/src/hosts/Elsa.Studio.Host.Server)
that uses NuGet packages and keeps only Elsa Identity login, the dashboard, and the workflow definitions/instances modules.

## Run

1. Start the server first: `dotnet run --project src/apps/Elsa.TieredApproval.Web` (http://localhost:5180).
2. Start Studio: `dotnet run --project src/apps/Elsa.TieredApproval.Studio` (http://localhost:5190).
3. Open http://localhost:5190 and sign in with `admin` / `password` (the server's development admin user).

The backend URL is `Backend:Url` in `appsettings.json` (`http://localhost:5180/elsa/api`). Studio calls the backend
from its own server process, so the Elsa server needs no CORS setup.

## Notes

- Workflows defined in code (C# `WorkflowBase` classes) show up in Studio as read-only: you can open them in the
  designer and inspect their instances, but you can't edit them there.
- Instance views refresh by polling. For live updates, enable `UseRealTimeWorkflows()` and `app.UseWorkflowsSignalRHubs()` on the server.
- Dashboard widgets only show up when the server enables `UseWorkflowRuntimeDashboard()` and `UseDashboardApi()`.
  Without them, the home page says the dashboard data is not available.
