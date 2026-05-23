# Mapping Frontend

React + Vite dashboard for the Mapping handoff. The full project handoff is documented in `../README.md`; this file focuses on frontend development and deployment.

## What This App Does

- Sends single and batch competence mapping requests to the backend.
- Shows pending, approved, rejected, legacy, imported, and archive review lists.
- Lets reviewers approve, reject, edit categorization, mark as imported/completed, move back to pending, delete approved rows, remap approved rows, and export approved competences.
- Displays confidence as a percentage badge based on the backend's stored `confidence` value.

## Requirements

- Node.js 20 or newer
- npm
- Mapping backend running locally for development API calls

## Local Development

Start the backend first from `../Mapping-LIA`:

```powershell
dotnet run --launch-profile https
```

Then start the frontend:

```powershell
npm install
npm run dev
```

Vite starts on `http://localhost:5173` by default. If that port is busy, it can use another nearby port. The backend CORS policy currently allows `5173`, `5174`, and `5175`.

Local API calls use the Vite proxy in `vite.config.js`:

```text
/api -> https://localhost:7079
```

## Scripts

```powershell
npm run dev
npm run lint
npm run build
npm run preview
npm start
```

- `npm run dev`: local Vite dev server with API proxy.
- `npm run lint`: ESLint validation.
- `npm run build`: production build into `dist`.
- `npm run preview`: local preview of the production build.
- `npm start`: runs `server.js` to serve `dist` for Azure App Service style hosting.

## Environment

For deployed builds, set:

```text
VITE_API_BASE_URL=https://<backend-app-host>
```

When `VITE_API_BASE_URL` is empty, Axios uses relative URLs. That is fine for local Vite proxy development but not enough for the standalone `server.js` static host unless the frontend and API share an origin.

## Auth State

Authentication is intentionally disabled right now.

- `src/api/client.js` has the token request interceptor and 401 redirect handling commented out.
- `src/App.jsx` keeps `ProtectedRoute`, but it currently always allows access.
- Login UI and token utilities are still present so auth can be restored later.

If auth is reactivated, update the backend and frontend in the same change so API middleware, frontend tokens, redirects, and protected routes agree.

## Remap Behavior

The review page remap action intentionally deletes an approved competence and maps the same name again. It does not preserve the old confidence. The backend reruns LLM matching and validation, so the new pending row can have a different confidence value.

This is documented in more detail in `../README.md`.

## Deployment

The GitHub Actions workflow is `.github/workflows/azure-deploy.yml`.

It:

- Installs Node 20.
- Runs `npm ci`.
- Runs `npm run build` with `VITE_API_BASE_URL` from repository secrets.
- Deploys the app folder to Azure App Service `mapping-frontend-ns99h3`.

Required repository secrets:

- `AZURE_WEBAPP_PUBLISH_PROFILE_FRONTEND`
- `VITE_API_BASE_URL`

## Maintainer Notes

- This project is JavaScript, not TypeScript.
- `server.js` is only a static file server and SPA fallback; it does not proxy backend requests.
- Keep backend endpoint paths centralized in `src/api`.
- Large review lists are fetched in batches. `getInitialBatch` currently asks for up to 1000 rows.
- Build output lives in `dist` and should not be treated as source.
