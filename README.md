# Mapping Handoff

Mapping is a competence-mapping tool with a .NET 8 API and a React/Vite review dashboard. It takes raw competence names, normalizes them, asks an LLM to map them into the area/category/subcategory hierarchy, stores the result for manual review, and lets reviewers approve, reject, adjust, archive, export, or remap entries.

## Project Layout

| Path | Purpose |
| --- | --- |
| `Mapping-LIA` | ASP.NET Core Web API, EF Core data access, database seeding, LLM mapping, review workflow, and background mapping queue. |
| `Mapping-Frontend` | React 19 + Vite dashboard for mapping and reviewing competences. |
| `Mapping-LIA/.github/workflows/main_mapping-lia.yml` | Backend build/publish/deploy workflow for Azure App Service. |
| `Mapping-Frontend/.github/workflows/azure-deploy.yml` | Frontend build/deploy workflow for Azure App Service. |

## Runtime Flow

The normal path is:

1. A user enters one or more competences in the frontend.
2. The frontend calls `POST /api/area-mapper/map` for single items or `POST /api/area-mapper/map-lines` for line-based batch mapping.
3. The backend normalizes the text, asks Azure OpenAI for a semantic hierarchy match, validates the match, and saves valid results as `PendingReview`.
4. Reviewers use the review page to approve, reject, edit categorization, mark approved items as imported/completed, assign items to Other, export approved items, or remap approved items.

## Backend

Backend path: `Mapping-LIA`

### Requirements

- .NET 8 SDK
- SQL Server LocalDB or SQL Server
- Access to the configured Azure OpenAI resource

### Configuration

`Mapping-LIA/appsettings.json` is intentionally not included in this public repository because it contains environment-specific configuration and secrets. Create it locally or configure the equivalent values through environment variables, user secrets, or Azure App Settings.

Required settings:

- `ConnectionStrings:DefaultConnection`: SQL Server or LocalDB connection string.
- `AzureOpenAI:Endpoint`: Azure OpenAI resource endpoint.
- `AzureOpenAI:Deployment`: chat/model deployment used by the matcher and validator.
- `AzureOpenAI:ApiVersion`: Azure OpenAI API version.
- `AzureOpenAI:ApiKey`: API key. Do not commit this value.
- `Normalization`: aliases, stopwords, and normalization behavior used before matching.
- `Jwt`: preserved for future auth reactivation, but auth is disabled in code right now. Do not commit signing secrets.

### Run Locally

From `Mapping-LIA`:

```powershell
dotnet restore
dotnet build
dotnet run 
```

The HTTPS launch profile serves the API at `https://localhost:7079` and opens Swagger in Development. The frontend Vite proxy expects this URL by default.

On startup, `Program.cs` seeds the hierarchy/reference data and runs legacy/import transfer routines:

- `DbInitializer.SeedData(context)`
- `DbInitializer.MigrateLegacyCompetencesAsync(context, logger)`
- `DbInitializer.TransferImportCompetencesAsync(context, normalizer, logger)`

Because this runs during app startup, be careful when changing seed/transfer code. A startup failure can prevent the API from serving requests.

### Main API Areas

- `POST /api/area-mapper/map`: maps one competence immediately.
- `POST /api/area-mapper/map-lines`: queues a plain-text batch, one competence per line.
- `GET /api/area-mapper/map-lines/{jobId}`: checks queued batch status.
- `GET /api/review/*`: lists pending, approved, rejected, archive, legacy, imported, counts, and metadata.
- `POST /api/review/{id}/approve`: approves one pending competence.
- `POST /api/review/{id}/reject`: rejects one competence with notes.
- `PATCH /api/review/{id}/update-categorization`: changes area/category/subcategory and optionally name.
- `POST /api/review/imported/bulk`: marks approved competences as imported/completed.
- `POST /api/review/pending/bulk`: moves approved competences back to pending.
- `DELETE /api/review/{id}`: deletes one approved competence so it can be mapped again.

Prefer `map-lines` for batch imports. `map-form` still exists but plain text is more reliable.

## Frontend

Frontend path: `Mapping-Frontend`

### Requirements

- Node.js 20 or newer
- npm

### Run Locally

From `Mapping-Frontend`:

```powershell
npm install
npm run dev
```

Vite serves the app on `http://localhost:5173` by default. If that port is occupied it may use another allowed local port; the backend CORS policy currently allows `5173`, `5174`, and `5175`.

During local development, Vite proxies `/api` requests to `https://localhost:7079`. For deployed builds, set `VITE_API_BASE_URL` so the static frontend calls the deployed backend URL.

### Useful Scripts

```powershell
npm run dev
npm run lint
npm run build
npm run preview
npm start
```

`npm start` runs `server.js`, which serves the built `dist` folder and falls back to `index.html` for client-side routes. It does not proxy API calls.

## Authentication Posture

Authentication is intentionally disabled for the current internal setup.

Backend JWT registration and middleware are commented out in `Mapping-LIA/Program.cs`. Frontend token handling is also commented out in `Mapping-Frontend/src/api/client.js`, and routes are allowed by the current `ProtectedRoute` wrapper.

When auth is reactivated, switch backend and frontend together:

1. Restore backend JWT authentication/authorization registration.
2. Restore `app.UseAuthentication()` and `app.UseAuthorization()`.
3. Restore frontend token request/response handling.
4. Restore login route behavior and protected route checks.
5. Re-test all write endpoints, especially review mutations and mapping endpoints.

Do not enable only one side of auth; that will either leave the UI unable to call the API or leave routes protected only in the browser.

## Remapping And Confidence

Remap behavior is intentionally simple and should be understood before changing it.

In `Mapping-Frontend/src/pages/ReviewPage.jsx`, remapping:

1. Requires exactly one selected competence.
2. Deletes the existing approved competence through `DELETE /api/review/{id}`.
3. Calls `POST /api/area-mapper/map` again with the same competence name.
4. Reloads the review list and counts.

The previous confidence is not carried forward. The backend reruns the full matching and validation pipeline. In `Mapping-LIA/Services/AreaMapper/AreaMapperService.cs`, final confidence is calculated as:

```csharp
var finalConfidence = llmValidation.Confidence ?? matchResult.Score;
```

The value is then rounded to four decimals before storing. This means remapping can produce a different confidence for the same name because the LLM semantic match and validation are executed again. That behavior is intentional for now: remap should represent the latest model assessment instead of preserving stale confidence from the deleted row.

## Known Oddities And Maintainer Notes

- The solution is intentionally compact: one backend Web API project and one frontend project.
- Backend and frontend have separate build/deploy workflows.
- The backend targets `net8.0` and has nullable reference types enabled.
- Auth code exists but is commented out across backend and frontend. Treat it as a future reactivation path, not dead code.
- `appsettings.json` is ignored and should stay local/private. Configure secrets through local user secrets, environment variables, or Azure App Settings.
- There is no automated test project in this handoff. Current validation is build, lint, and manual workflow testing.
- LLM-backed mapping is not deterministic. Small differences in confidence or suggested category/subcategory can happen between runs.
- Batch mapping uses an in-memory queue and background worker. Completed job status is retained only temporarily.
- Build outputs such as `bin`, `obj`, `dist`, `.vs`, and `node_modules` are local artifacts and should not be treated as source.

## CI And Deployment

Backend workflow:

- File: `Mapping-LIA/.github/workflows/main_mapping-lia.yml`
- Builds with .NET 8 on `windows-latest`
- Publishes and deploys to Azure Web App `mapping-backend-ns99h3`
- Requires `AZURE_WEBAPP_PUBLISH_PROFILE`

Frontend workflow:

- File: `Mapping-Frontend/.github/workflows/azure-deploy.yml`
- Builds with Node 20 on `ubuntu-latest`
- Deploys to Azure Web App `mapping-frontend-ns99h3`
- Requires `AZURE_WEBAPP_PUBLISH_PROFILE_FRONTEND`
- Uses `VITE_API_BASE_URL` from repository secrets during build

Recommended manual smoke test:

1. Start the backend with the HTTPS launch profile.
2. Start the frontend with `npm run dev`.
3. Map one competence.
4. Confirm it appears in Pending Review.
5. Approve it.
6. Remap it and confirm it returns as a new pending item with newly calculated confidence.
