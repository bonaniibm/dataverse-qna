# Dataverse QnA

A local, **read-only** AI assistant for Dataverse / Dynamics 365 environment admins and AMS engineers. Ask platform questions
("what's running now?", "why is saving an account slow?", "compare the Sales role between UAT and PROD") and get answers built
from live diagnostic data: plug-in steps and trace logs, system jobs, flow runs, security roles, solutions and change history.

See [docs/technical-design.md](docs/technical-design.md) for the full design.

## What's in the box

| Area | Tools |
|---|---|
| Environment | `list_environments`, `get_environment_profile` |
| Jobs and flows | `get_running_jobs`, `get_async_backlog`, `get_job_failures`, `get_flow_runs` |
| Plug-ins | `get_message_pipeline`, `get_plugin_performance`, `get_plugin_failures` |
| Security | `compare_roles`, `get_role_privileges` |
| Drift and ALM | `compare_solutions`, `get_recent_changes` |
| Health | `run_health_checks`, `get_table_stats` |
| Generic (read-only) | `dataverse_read`, `describe_table`, `query_telemetry` (App Insights) |

The agent is Claude Opus 5.5 on Microsoft Foundry. Tools fetch and compute deterministically; Claude decides which to call
and explains the results. Quick checks in the UI run tools directly, so they work even without the LLM.

**Read-only by design:** the Dataverse client only sends `GET` requests and only calls allow-listed read functions. Unit tests enforce this.

## Prerequisites

- Windows 10/11, .NET 10 SDK, Node.js 20+ (build only)
- Access to one or more Dataverse environments
- For sign-in, either:
  - the shared multi-tenant app registration (set `Auth:ClientId`), or
  - for development, the Azure CLI signed in to the environment's tenant (`az login --tenant <tenant-id>`), used automatically when no client ID is configured
- For the AI assistant: a Microsoft Foundry resource with a `claude-opus-5-5` deployment

## Build and run

```bash
cd web && npm install && npm run build && cd ..
dotnet build
dotnet src/DvQna.Host/bin/Debug/net10.0/dvqna.dll
```

The app starts on `http://127.0.0.1:5178` and opens your browser with a per-launch access token.

## Configuration

Settings come from `src/DvQna.Host/appsettings.json`, then `%LOCALAPPDATA%\DataverseQnA\config.json`, then `DVQNA_`-prefixed environment variables.

```json
{
  "Auth": { "ClientId": "<shared app registration client id>" },
  "Llm": {
    "Provider": "foundry",
    "FoundryResource": "<foundry resource name>",
    "FoundryAuth": "entra",            // or "apikey" (then run: dvqna set-foundry-key)
    "FoundryTenantId": "<tenant of the Foundry resource>",
    "Deployment": "claude-opus-5-5",
    "Effort": "medium"
  }
}
```

## CLI

```bash
dvqna env add PROD https://contoso.crm.dynamics.com prod   # tenant is detected automatically
dvqna env list
dvqna discover <tenant-id>                                 # list environments you can access
dvqna tool                                                 # list tools
dvqna tool get_running_jobs '{"env":"PROD"}'               # run a tool without the LLM
dvqna set-foundry-key                                      # store a Foundry key (DPAPI-encrypted)
dvqna status
```

## Tests

```bash
dotnet test
```

## Data stored locally

`%LOCALAPPDATA%\DataverseQnA\`: `profiles.json` (environments, no secrets), `app.db` (conversations, tool-call audit log, result datasets),
`msal_cache.bin` (encrypted token cache), `secrets\` (DPAPI-encrypted keys).
