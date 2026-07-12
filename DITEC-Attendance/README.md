# DITEC — QR + Face Attendance System

A lightweight, self-contained attendance system built on ASP.NET Core (.NET 8).

- **QR login**: each employee gets a pregenerated badge QR. Scanning it at the kiosk pulls up their identity and today's log.
- **Face verification**: after the QR scan, the kiosk opens the camera and verifies the person's face against the descriptor captured at enrollment (runs client-side with `face-api.js`; the server only compares the resulting numeric descriptor).
- **In / Out rules**: In-time can be logged once per day, only up to 11:00 AM. Out-time can be logged any number of times, only from 5:00 PM onward. Outside that window, the kiosk logs nothing and explains why.

## Why it's lightweight

- No EF Core, no ORM — a single `Db.cs` file with plain ADO.NET against **SQLite** (one file, zero setup locally; a remote **Turso** database in production).
- Only two NuGet packages: `Nelknet.LibSQL.Data` (an ADO.NET provider for libSQL/Turso — same connection/command/reader API as `Microsoft.Data.Sqlite`, but it can also open a `libsql://` connection over HTTPS) and `QRCoder`.
- No SPA framework — three static HTML pages with vanilla JS.
- Face recognition runs **in the browser** (small ~6MB model set loaded from CDN), so the server does no ML work at all — just a Euclidean-distance comparison between two float arrays.
- Single project, single process, single `dotnet run` to start.

## Project layout

```
DITEC-Attendance/
  Program.cs              Minimal API (all endpoints)
  Data/Db.cs               SQLite access (raw ADO.NET)
  Data/Models.cs            Shared record/DTO types
  appsettings.json          Attendance window + DB path config
  wwwroot/
    index.html / js/kiosk.js     The scan-and-verify kiosk screen
    admin.html / js/admin.js     Registration, badge printing, face enrollment, reports
    css/style.css                Shared "checkpoint" visual theme
```

## Running it

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
cd DITEC-Attendance
dotnet restore
dotnet run
```

The app listens on **http://localhost:5080** (change this in `appsettings.json` under `Kestrel:Endpoints:Http:Url`).

- **Kiosk (scan/verify)**: `http://localhost:5080/index.html`
- **Admin (register, badges, reports)**: `http://localhost:5080/admin.html`

A `ditec.db` SQLite file is created automatically on first run in the project folder — no database server to install.

## First-time setup

1. Open **Admin** → fill in Employee Code, Name, Department → **Register & Generate Badge**.
2. A badge modal appears with a printable QR — print it or save it as the employee's ID card.
3. Click **Face** next to the employee in the directory table, look at the camera, and **Capture Face**. This stores a 128-number face descriptor, not an image.
4. Go to **Kiosk** and scan the printed badge to test the full flow.

## Deploying to Render + Turso

Render's disks are ephemeral — anything written to `ditec.db` on a native filesystem gets wiped on every deploy or restart — so production uses [Turso](https://turso.tech) (managed libSQL) for persistent storage instead. Locally, nothing changes: run `dotnet run` and it still just uses a `ditec.db` file, no Turso account needed.

### 1. Create the Turso database

Install the Turso CLI and create a database (pick any region close to your Render region):

```bash
curl -sSfL https://get.tur.so/install.sh | bash
turso auth login
turso db create ditec-attendance
turso db show ditec-attendance --url        # → libsql://ditec-attendance-<org>.turso.io
turso db tokens create ditec-attendance      # → a long JWT auth token
```

Keep the URL and token — you'll paste them into Render as environment variables. The app creates its own tables on first run (`Db.Initialize()`), so there's no separate migration step.

### 2. Deploy the service to Render

Render has no native .NET runtime, so this repo ships a `Dockerfile` (multi-stage build → `mcr.microsoft.com/dotnet/aspnet:8.0` at runtime) and a `render.yaml` Blueprint.

**Option A — Blueprint (recommended):** push this repo to GitHub, then in the Render dashboard choose **New → Blueprint** and point it at the repo. Render will read `render.yaml`, create the web service, and prompt you for the two secret env vars below.

**Option B — Manual web service:** **New → Web Service**, connect the repo, set **Runtime** to **Docker** (Render auto-detects the `Dockerfile`), and add the environment variables yourself.

Either way, set these in the service's **Environment** tab:

| Key | Value |
|---|---|
| `TURSO_DATABASE_URL` | the `libsql://...` URL from `turso db show` |
| `TURSO_AUTH_TOKEN` | the token from `turso db tokens create` |
| `ASPNETCORE_ENVIRONMENT` | `Production` |

Render injects `PORT` automatically — `Program.cs` reads it and binds Kestrel to `http://0.0.0.0:$PORT`, so nothing else to configure there. On the free plan, HTTPS/TLS termination is handled by Render, which also satisfies the camera-access-needs-HTTPS requirement mentioned below.

### 3. Verify

Once deployed, hit `https://<your-service>.onrender.com/admin.html`, register a test employee, and confirm it shows up via `turso db shell ditec-attendance "SELECT * FROM Employees"` — that confirms the app is actually writing to Turso rather than a local file.

### Notes on this setup

- The connection logic (in `Program.cs`) checks for `TURSO_DATABASE_URL`; if it's unset, it falls back to the local `Attendance:DatabasePath` file — that's what keeps local dev dependency-free.
- Each `Db` method opens a fresh connection per call (same pattern the original SQLite version used). For this app's traffic (a kiosk + admin panel), the added per-request round-trip to Turso is not a bottleneck; if this ever needs to scale up, moving to a longer-lived connection or Turso's embedded-replica mode (local reads, synced writes) would be the next step.
- I haven't been able to compile-check this in my current environment (no .NET SDK / NuGet access here), so please run `dotnet build` locally before pushing — the `Nelknet.LibSQL.Data` package is pre-1.0, so it's worth double-checking against the latest version on NuGet too.

## Notes & things to adjust for production use

- **Camera access requires HTTPS** in most browsers once you're off `localhost` — put this behind a reverse proxy (nginx/Caddy) with TLS, or run Kestrel with a cert, before deploying to a real network.
- Face matching threshold and the In/Out cutoff times are in `appsettings.json` under `Attendance:*` — tune `FaceMatchThreshold` (lower = stricter) based on real-world testing.
- The kiosk and admin pages load `html5-qrcode` and `face-api.js` (plus its model weights) from public CDNs, so the kiosk machine needs internet access the first time (browser caches them after).
- This is a single-tenant, no-login admin panel by design (kept lightweight). If it'll be exposed beyond a trusted local network, put it behind your own auth (reverse proxy basic-auth, or wrap `/api/employees*` routes with ASP.NET Core auth middleware).
