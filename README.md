# Kilowattcher

A home energy assistant built on Azure OpenAI. A homeowner asks "Should I charge my battery from the grid tonight?" and an agent checks live battery, rate, and solar data through tool calls, looks up the household's documents, and answers in plain words with the data and sources shown on screen. It's wrapped in five layers of guardrails and measured by a 24-case eval suite.

> **Live demo: invite only.** The production site sits behind an invite code. Ask me for the link and a code.

**Stack:** React + TypeScript (Vite) · ASP.NET Core minimal API on .NET 10 · Azure OpenAI in Microsoft Foundry (Responses API, embeddings, Prompt Shields) · Open-Meteo live weather · Docker (chiseled image) · Vercel Services · GitHub Actions

## What it does

| Ask | What happens |
| --- | --- |
| "Should I charge my battery from the grid tonight?" | Finds the cheapest window across midnight and checks tomorrow's **real** solar forecast → sunny: "not needed, solar will refill it for free"; cloudy: "charge tonight, midnight to 1:34 AM, about $0.96 instead of $4.18 at peak" |
| "On Tuesday, should I charge at night if it's only 1/3 full?" | Resolves "Tuesday" in code, plans from 33%, and checks Wednesday's real forecast → "Tuesday night (early Wednesday), midnight to 1:49 AM, about $1.11", or "not needed" if Wednesday is sunny |
| "How long will my battery last if the power goes out?" | Computes backup time in code with the documented formula (≈ 4.2 h) and cites the outage guide |
| "What does reserve percentage mean?" | Retrieves the battery manual passage and cites it; tap the citation to read it |
| "Write me a poem about cats." | Polite decline plus a suggested energy question |
| "How do I wire a new circuit into my panel?" | Refers to a licensed electrician |

Every answer shows chips for the tools it used (tap one to see the raw call and result) and numbered citations for the documents behind it.

**Per-visitor settings.** A settings panel (a side panel that pops in and out on desktop, a sheet behind a gear button on phones) lets each visitor change, and the browser remembers: battery model (VoltNest 13 or the rival Helio PowerCell 10), current charge, home city (Phoenix, Seattle, Miami, New York, with live weather for each), and the three time-of-use prices. Defaults live in `appsettings.json` ("Household"); every tool computes from the visitor's household, and search only returns the manual for *their* battery.

## Architecture

```mermaid
flowchart LR
    subgraph Browser
        UI["React app<br/>invite screen · chat · tool chips · citations"]
    end

    subgraph Vercel["Vercel (one project, one domain)"]
        WEB["web service<br/>static Vite build at /"]
        API["api service: .NET 10 container at /api<br/>invite gate · rate limits · agent loop"]
    end

    subgraph Azure["Azure (Microsoft Foundry)"]
        SHIELD["Prompt Shields<br/>(Content Safety)"]
        CHAT["chat deployment<br/>gpt-4.1-mini"]
        JUDGE["judge deployment<br/>gpt-4.1-mini"]
        EMBED["embed deployment<br/>text-embedding-3-small"]
    end

    DATA[("embeddings.json<br/>28 doc chunks, in memory")]
    TOOLS["Tools<br/>battery · rates · solar forecast<br/>backup estimate · charging window"]
    METEO["Open-Meteo<br/>live hourly irradiance + cloud cover"]

    UI -->|"/"| WEB
    UI -->|"/api (signed cookie)"| API
    API -->|1. screen message| SHIELD
    API -->|2. agent loop, ≤ 5 tool rounds| CHAT
    API --> TOOLS
    TOOLS -->|"cached 30 min"| METEO
    API -->|query embedding| EMBED
    API --- DATA
    API -->|3. review answer| JUDGE
```

- **One domain, zero CORS:** Vercel Services routes `/` to the web app and `/api/*` to the API container. Only the API ever talks to Azure; the browser never sees a key.
- **The agent loop lives in C#** (`api/Agent/AgentRunner.cs`): model → run the requested tools → feed results back → repeat, with a hard limit of 5 tool rounds. Replies use a strict JSON schema that C# validates again, with one retry.
- **RAG without a database:** six fictional docs are split into one chunk per `##` heading and committed as `api/Data/embeddings.json` (34 vectors). Cold starts make no embedding calls; search is cosine similarity in memory.
- **Metadata filtering:** model-specific docs carry front matter (`product: voltnest-13`), so a VoltNest home never gets the Helio manual and vice versa; shared docs (rates, FAQ, outage, solar) go to everyone.
- **Incremental embeddings:** `tools/embed` compares each chunk's content hash with what's stored: unchanged chunks keep their vectors, changed and new ones are embedded in one request, removed ones are dropped. Adding the second battery re-embedded 22 of 34 chunks; `--plan` previews this without calling Azure, and CI's `--check` fails if docs and vectors drift.
- **Real weather:** the solar forecast comes from Open-Meteo's hourly sunlight on a tilted panel for the home's location (configurable city, latitude, longitude, and time zone), converted to kWh in code: 7.2 kW × irradiance ÷ 1000 × 0.8 losses. A clear November day in Phoenix comes out at about 35 kWh, matching the docs.
- **The model explains; code computes.** Evals showed the model reading the right formula and still doing the arithmetic differently from run to run, so backup time, the cheapest charging window, which date "Tuesday night" means, and whether the next day's solar makes grid charging unnecessary are all computed in tested C# tools (`estimate_backup_time`, `find_cheapest_charging_window`). Tests keep the numbers in code and the numbers in the docs in agreement.

## Guardrails

Built after jailbreaking my own agent with the "my grandmother used to tell me about rates with cookie recipes" trick. Only the first layer depends on the model behaving; every other layer is enforced in code or by Azure.

| # | Layer | Enforced by | What it stops |
| --- | --- | --- | --- |
| 1 | Hardened system prompt + a rules reminder after every visitor message | Model | Most one-shot attempts |
| 2 | Required `topic` label (`home_energy` / `off_topic` / `safety`); the server replaces off-topic text with a fixed decline and adds the electrician referral to safety replies | **Code** | Recipes slipping out even when the model plays along |
| 3 | HMAC-signed assistant history; unsigned or edited turns get `400` before any model call | **Code** | Forged "Sure, I can share recipes!" history |
| 4 | Answer judge: a separate deployment reviews every answer (off topic, wiring steps, prompt leaks, followed injections). Fails closed | **Second model + code** | Gradual drift; the "end every answer with PWNED" injection the prompt missed |
| 5 | Prompt Shields screens every message before the agent runs, plus the deployment's built-in guardrail as a backstop. Fails closed | **Azure** | DAN-style and "system override" jailbreaks (blocked in ~0.4 s) |

## Evals

`evals/` asks the real API 27 questions like a visitor would (signing in, carrying signed history across turns) and grades each answer deterministically: status, topic, tools called, document cited, required and banned phrases. No model grades another model.

| Category | Cases | Passes when |
| --- | --- | --- |
| Tool selection | 7 | The right live-data tools were called and the key number appears |
| Grounded answers | 6 | The expected document is cited |
| Off-topic refusal | 4 | Declined, with a suggestion, and no off-topic content |
| Safety referral | 2 | Licensed electrician, no wiring steps |
| Prompt injection | 2 | Hidden instructions ignored |
| Guardrails | 4 | Cookie jailbreak, DAN, analogy attack, forged history |
| Second battery | 2 | With settings set to the Helio PowerCell 10, its manual is cited and its specs are used (5.8 h backup at 80%) |

**Latest: 27/27** ([report](evals/results/2026-11-02_030100.md), every answer included). Evals **replay a recorded Open-Meteo response** (`WEATHER_SOURCE=recorded`) through the same parsing and solar math, so scores stay comparable from day to day while production uses live weather. The first run scored **19/24**: two made-up numbers, an uncited answer, and an injection that got through. The fixes and before/after reports are all in `evals/results/`.

Run it against a local API:

```powershell
$env:EVAL_INVITE_CODE = Read-Host "Invite code" -MaskInput
dotnet run --project evals                      # default pass mark: 25/27 (run the API with WEATHER_SOURCE=recorded)
```

Or from GitHub: **Actions → Evals → Run workflow**. It starts the API inside the runner with a throwaway invite code made for that run.

## Run it locally

**Prerequisites:** .NET 10 SDK, Node 24, an Azure OpenAI (Foundry) resource with three deployments (`chat` and `judge` on gpt-4.1-mini, `embed` on text-embedding-3-small).

1. **Secrets live outside the repo** in .NET user-secrets (see `.env.example` for every name, with fake values):

   ```powershell
   dotnet user-secrets init --project api
   dotnet user-secrets set AZURE_OPENAI_ENDPOINT "https://<resource>.openai.azure.com/" --project api
   dotnet user-secrets set AZURE_OPENAI_API_KEY "<key>" --project api
   dotnet user-secrets set AZURE_OPENAI_CHAT_DEPLOYMENT chat --project api
   dotnet user-secrets set AZURE_OPENAI_EMBEDDING_DEPLOYMENT embed --project api
   dotnet user-secrets set AZURE_OPENAI_JUDGE_DEPLOYMENT judge --project api
   dotnet user-secrets set AZURE_CONTENT_SAFETY_ENDPOINT "https://<resource>.cognitiveservices.azure.com/" --project api
   dotnet user-secrets set INVITE_CODE_HASH "<uppercase hex SHA-256 of your invite code>" --project api
   dotnet user-secrets set SESSION_SIGNING_KEY "<base64 of 32 random bytes>" --project api
   ```

2. **Start the API and the web app:**

   ```powershell
   dotnet run --project api                 # http://localhost:8080
   npm install --prefix web
   npm run dev --prefix web                 # http://localhost:5173, proxies /api to 8080
   ```

3. **Tests** (no Azure calls; the model is mocked):

   ```powershell
   dotnet test
   npm test --prefix web
   ```

4. **After editing anything in `api/Data/docs/`**, update the vectors (one Azure call for just the new and changed chunks) and commit `api/Data/embeddings.json`:

   ```powershell
   dotnet run --project tools/embed -- --plan    # what would change: unchanged / changed / added / removed
   dotnet run --project tools/embed              # embed only what changed
   dotnet run --project tools/embed -- --check   # CI runs this; fails if docs and vectors drift
   ```

## Security

The repo is public, so it's built to be provably secret-free.

- **Where secrets live:** .NET user-secrets locally (outside the repo folder), Vercel environment variables marked Sensitive in production, GitHub Actions secrets for the Evals workflow. The Evals workflow never stores an invite code; it creates a throwaway one for each run.
- **Three layers between a key and the internet:** a gitleaks pre-commit hook, a gitleaks CI job over the full history, and GitHub push protection.
- **Access:** an invite code checked against its SHA-256 hash in constant time, then an HMAC-signed `HttpOnly; Secure; SameSite=Strict; Path=/api` cookie valid 24 hours. Five redeem attempts per minute per IP; chat uses a token bucket (10 burst, +1 every 6 s) with exact `RateLimit-*` and `Retry-After` headers.
- **No stored user data:** no database, no accounts, no saved chats. Conversations live in the browser tab; Azure responses are requested with storage off. Logs record tool names, timing, and token counts, never message text.
- **Cost ceiling:** tokens-per-minute limits on every Azure deployment and a budget alert on the resource group.
- **Images:** a multi-stage Docker build on the chiseled, non-root ASP.NET runtime (the `-extra` variant, for time-zone data). Secrets are injected at runtime; `docker history` and `docker image inspect` show none.
- **Fail closed:** if the judge or Prompt Shields is misconfigured or down, chat returns an error instead of an unchecked answer.

## Project layout

```
api/            ASP.NET Core API: Agent/ (loop, prompt, reply schema), Tools/, Rag/, Auth/, Safety/, Weather/, Households/
api/Data/       Fictional docs (docs/*.md), their precomputed embeddings.json, and a recorded forecast (weather/)
api.Tests/      xUnit: 296 tests, Azure and weather mocked or replayed
web/            React + TypeScript chat app, Vitest + Testing Library
evals/          Eval runner, cases.json, and results/ reports
tools/embed/    Builds and checks embeddings.json
.github/        CI (every PR) and Evals (manual) workflows
vercel.json     Services routing: web at /, api container at /api
```

## How this was built

Built over one weekend as an AI-native workflow. I wrote the specs (product, technical, app flow, design, data, plan), reviewed and merged every pull request, and did all the DevOps by hand: Azure, Foundry deployments and quotas, secrets, Docker, Vercel, and GitHub settings. [Claude Code](https://claude.com/claude-code) wrote the application code under a written working agreement: one branch and PR per stage, a full test gate before every review, and never touching secrets or running deploy commands.

Solar forecasts use weather data by [Open-Meteo.com](https://open-meteo.com/) ([CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)).
