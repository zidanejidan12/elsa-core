# Elsa.TieredApproval.Studio

Minimal Elsa Studio 3.9.0 (Blazor Server) host for watching the tiered-approval workflow run on the local
`Elsa.TieredApproval.Web` server. It is a trimmed copy of the upstream
[`Elsa.Studio.Host.Server`](https://github.com/elsa-workflows/elsa-studio/tree/3.9.0/src/hosts/Elsa.Studio.Host.Server)
that uses NuGet packages and keeps only Elsa Identity login, the dashboard, and the workflow definitions/instances modules.

## Run

1. Start the server first: `dotnet run --project src/apps/Elsa.TieredApproval.Web` (http://localhost:5180).
2. Start Studio: `dotnet run --project src/apps/Elsa.TieredApproval.Studio` (http://localhost:5190).
3. Open http://localhost:5190 and sign in with `admin` / `password` (the server's development admin user).
4. **Workflows > Definitions > Tiered approval** shows the flowchart: the submit and validation steps, the four approval
   steps, "Send back to", and the emails. The diagram is in the
   [server README](../Elsa.TieredApproval.Web/README.md#flow).
5. Start a request (see the server README), then open **Workflows > Instances** and pick the run. The instance view highlights
   the path the run took and lists its journal.

The backend URL is `Backend:Url` in `appsettings.json` (`http://localhost:5180/elsa/api`). Studio calls the backend
from its own server process, so the Elsa server needs no CORS setup.

## Notes

- The workflow is defined in code (a C# `WorkflowBase` class marked `AsReadonly()`), so Studio opens it read-only: you can
  inspect the diagram and its instances, but not edit or publish it. Inputs computed in C# show as `Delegate` expressions,
  without their code.
- The node positions come from the code (`metadata.designer.position`), so the diagram is laid out without manual work.
- The server enables real-time updates (`UseRealTimeWorkflows()`, `app.UseWorkflowsSignalRHubs()`), so an open instance
  view updates live. Studio connects to the hub with its access token.
- The server enables the dashboard API (`UseDashboardApi()`, `UseWorkflowRuntimeDashboard()`), which feeds the home page widgets.
- The server's smoke test (`src/apps/Elsa.TieredApproval.Web/smoke-test.ps1`) also builds and starts this host, and checks
  that its home page, its sign-in page and its Blazor circuit endpoint answer.
