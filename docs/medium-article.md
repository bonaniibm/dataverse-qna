# I built a read-only AI assistant for Dataverse admins. Here's how it works.

*Ask "why is saving an Opportunity slow?" or "compare the Sales role between UAT and PROD" and get an answer backed by live data, without giving an AI write access to your environment.*

---

Every Dynamics 365 / Power Platform admin and AMS engineer knows the drill. A ticket lands: *"Saving cases has been slow since this morning."* You open the Plug-in Registration Tool to see what runs on Update. You check whether trace logging is on (it usually isn't). You look at system jobs. You check what was deployed last night. You compare a security role between UAT and PROD in XrmToolBox. Each step is a different tool, and the answer only emerges when you put the pieces together in your head.

The Copilots built into model-driven apps are great at business data ("summarise this account"), but they don't reason about the platform underneath: plug-in pipelines, the async queue, privilege depths, solution layers.

So I built **Dataverse QnA**: a local, read-only assistant that connects to one or more Dataverse environments and answers technical questions by running purpose-built diagnostic queries, then explaining what the evidence shows.

The code is open source: **https://github.com/bonaniibm/dataverse-qna**

![Demo: a health check on a test environment, then asking about async queue health and opening the evidence table](https://raw.githubusercontent.com/bonaniibm/dataverse-qna/main/docs/media/dataverse-qna-demo.gif)

## What it can answer

A few real questions I've asked it against my developer environments:

- **"What runs when an appointment is updated, and is anything likely to slow saves down?"** It lists every plug-in step, classic workflow, business rule and Dataverse-triggered flow in execution order. It flags anti-patterns such as synchronous Update steps without filtering attributes. And it tells you plainly that it can't measure durations because plug-in trace logging is off.
- **"Is the async queue healthy, and is anything running right now?"** It shows running jobs, waiting-for-resources counts, the oldest queued job, throughput, queue-wait and run-time percentiles, and failures. It also correctly recognised that 35 "waiting" jobs were recurring platform housekeeping, not a stuck queue.
- **"Compare the Basic User role between DEV and TEST."** It produces a deterministic diff. In my case there were 5 real permission differences and 48 differences that only existed because one environment had extra solutions installed. That distinction matters, and naive comparisons miss it.
- **"Run a health check and tell me the top two things to fix."** It runs a rule library covering plug-in steps, real-time workflows, async backlog, trace-log settings, environment variables and System Administrator counts. Microsoft's own first-party steps are excluded, because you can't change them anyway.

## The core design principle: the LLM plans, code computes

The most important decision was splitting responsibilities:

- **The language model decides which diagnostics to run and explains the results.**
- **Deterministic C# code fetches the data and does all the maths**: percentiles, privilege diffs, pipeline ordering, health rules.

An LLM should never diff 2,000 privileges or estimate a p95 in its head. Every number in an answer comes from a tool result, and the UI shows the full evidence table and the exact query behind it. If the data isn't available (trace logging off, no App Insights), the assistant says so and tells you how to get it.

## Architecture

![Architecture: React UI and ASP.NET Core host on localhost, a provider-neutral agent loop, 18 read-only tools, deterministic engines and a read-only Dataverse reader, calling Dataverse, Entra ID, App Insights and Microsoft Foundry](https://raw.githubusercontent.com/bonaniibm/dataverse-qna/main/docs/media/architecture.png)

Everything runs on the admin's machine:

- **React UI**: chat, one-click quick checks, a tool-activity timeline, and an evidence panel with sortable tables and CSV export.
- **ASP.NET Core host (.NET 10)**: binds to `127.0.0.1` only and requires a per-launch token, so other local processes and websites can't call it.
- **Agent loop**: provider-neutral. It works with Claude through the Anthropic SDK, or with OpenAI-compatible models on Microsoft Foundry (I'm currently running Grok 4.6). It redacts sensitive text before anything reaches the model and writes every tool call to a local audit log.
- **18 diagnostic tools**, each written with knowledge of the Dataverse system tables: `get_message_pipeline`, `get_plugin_performance`, `get_async_backlog`, `get_job_failures`, `get_flow_runs`, `compare_roles`, `compare_solutions`, `get_recent_changes`, `run_health_checks`, and a few guarded generic read tools.
- **SQLite** for conversations, result datasets and the audit trail.

## How a question becomes an answer

![Sequence: the admin asks a question, the model calls get_message_pipeline, then performance and change-history tools in parallel; full results go to the evidence panel while the model gets summaries; the model returns summary, findings, likely cause, next steps and sources](https://raw.githubusercontent.com/bonaniibm/dataverse-qna/main/docs/media/agent-flow.png)

Two details keep this fast and cheap:

1. **Large results never go into the prompt.** A tool might read 20,000 rows. The model receives a one-line summary and the top 25 rows, while the full dataset goes to the evidence panel. A typical question costs about 10–15K input tokens.
2. **Parallel tool calls.** Independent checks (performance, failures, recent changes) run at the same time, with a cap of four concurrent requests per environment so the tool never becomes the cause of throttling.

## Read-only by design, and enforced in code

An admin's token *can* write. So "read-only" can't just be a line in the system prompt. It's enforced in several layers:

- There are **no write tools**. The model simply has nothing to call that could change data.
- Tools only get a **read-only Dataverse reader**. Its HTTP pipeline throws on anything other than `GET`, and it can only call an allow-list of read functions (`WhoAmI`, `RetrieveTotalRecordCount`, `RetrieveRolePrivilegesRole`, …).
- **Unit tests** prove that POST/PATCH/PUT/DELETE and non-allow-listed functions are rejected.
- **Tool output is treated as untrusted data.** A plug-in exception message that says "ignore previous instructions" is just text. And since there's no write path, a prompt injection can at worst skew an answer, never change an environment.

When a fix needs a change (enable trace logging, add filtering attributes, adjust a role), the assistant writes the steps for a human to do in the admin center or the Plug-in Registration Tool.

## Sign-in and multi-tenant support

AMS engineers support many customers, so multi-tenant sign-in had to be first-class:

![Sign-in flow: add an environment URL, an anonymous probe reveals the owning tenant, MSAL signs in to that tenant with a shared multi-tenant public client, consent on first use, then a delegated token used by the read-only reader](https://raw.githubusercontent.com/bonaniibm/dataverse-qna/main/docs/media/signin-flow.png)

- One **shared multi-tenant app registration**: a public client, so there's no secret to leak.
- You paste an environment URL. The app makes an anonymous request and reads the `401` challenge to discover which tenant owns it.
- **MSAL** signs you in to *that* tenant in the browser. Tokens are cached encrypted with Windows DPAPI.
- Tokens are **delegated**, so Dataverse security applies: the assistant can only see what you can see.

## Lessons from testing against real environments

A few things I only learned by running it against live orgs:

- **Naive role comparison is misleading.** Comparing System Customizer across two environments produced 13,000+ differences. Almost all of them were privileges for tables that only existed in one environment. Separating "table not installed" from "real permission difference" brought it down to the 421 that actually matter.
- **Match privileges by name, never by object type code.** Custom tables get different type codes in each environment.
- **Health checks must exclude first-party code.** My first health check flagged hundreds of Microsoft plug-in steps. That's noise: admins can't change them. After filtering out more than 2,000 first-party steps, the findings became actionable.
- **Change timelines need de-duplication.** Platform features re-save some steps on every publish; collapsing repeats took one timeline from 500 rows to 4.
- **Model choice matters more than I expected.** On the same question, one model correctly identified recurring platform jobs as healthy. Another called them "stuck for 34 days" and suggested cancelling them. Because the tools are deterministic, swapping models is a one-line config change, and the evidence is always there to check the model's reasoning.
- **Plug-in trace logs are a thin source.** They're kept for about 24 hours, the message text is visible only to System Administrators, and in production the setting is usually off. App Insights export is the better long-term telemetry source.

## Try it

```bash
git clone https://github.com/bonaniibm/dataverse-qna
cd dataverse-qna/web && npm install && npm run build && cd ..
dotnet build
dotnet src/DvQna.Host/bin/Debug/net10.0/dvqna.dll
```

Add an environment URL, sign in, and use the quick checks. They work without any LLM. For the chat, point it at a model deployment on Microsoft Foundry. The README has the details, and the full technical design is in `docs/technical-design.md`.

## What's next

- An **MCP server** mode, so the same tools work from Claude Desktop, VS Code or Copilot Studio.
- **App Insights telemetry** templates for form-load and plug-in performance over time.
- **Snapshots and baselines**, so "what changed since last week?" works without telemetry.

If you're an admin or AMS engineer, I'd love to hear which questions you'd want it to answer. Issues and PRs are welcome on GitHub.

*Built with .NET 10, React, MSAL and Microsoft Foundry. Not affiliated with or endorsed by Microsoft.*
