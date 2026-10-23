# Reconza

Upload the reports you already get, reconcile them, and get the settlement workbook out.

Reconza is a reconciliation engine for operations teams that live in spreadsheets. It focuses on
**weekly driver settlements** (trips + fuel + deductions + customer reports) and includes a
**marketplace payout** template too. It is not a general-purpose automation builder.

## Run it

Requires the .NET 10 SDK. No database server needed for development (SQLite).

```bash
dotnet run --project src/SettleFlow.Web --urls http://localhost:5187
```

Open http://localhost:5187, start a free trial, create a workflow from **Weekly driver settlement**,
then click **Run with demo files**. The demo files are deliberately messy. They include title rows,
`T-1001` vs `t1001`, `d001 ` vs `D-004`, `dd/MM/yyyy` dates, `₹1,234.00` text amounts, duplicates,
an orphan fuel fill, an unreadable amount, a trip missing from the customer's report and a short-paid trip.

```bash
dotnet test
```

To also run the MySQL end-to-end test, point it at an empty, disposable database. The test drops and recreates it.

```bash
RECONZA_MYSQL="Server=127.0.0.1;Database=reconza_mysql_test;User=…;Password=…;SslMode=Required" dotnet test
```

To use PostgreSQL, set `ConnectionStrings__Postgres`.

## How it works

```
upload ─▶ validate file ─▶ find header row ─▶ map columns ─▶ normalize values
       ─▶ de-duplicate (composite key) ─▶ match to primary (composite key)
       ─▶ compare amounts (per key, with tolerance) ─▶ settle per group ─▶ Excel report
```

| Concept | Meaning |
|---|---|
| **Field** | A canonical column, such as `DriverId`, `TripRef`, `Date` or `Amount`, with a type and header synonyms |
| **Source** | One uploaded file per run. Its role is Primary, Deduction, Addition or ReferenceOnly |
| **Key fields** | A composite key. Repeats are flagged `Duplicate` and excluded from totals |
| **Match fields** | Composite key used to link rows to the primary source. Rows without a match are flagged `Unmatched`. Primary rows absent from the source are flagged `MissingInSource` |
| **Compare** | Sums a field per match key, compares it with the primary, and flags `AmountMismatch` beyond the tolerance |
| **Group by** | Gives one settlement line per value: net = Primary + Additions − Deductions |

**There is no AI in the arithmetic.** Matching and settlement are deterministic and unit-tested
(`SettleFlow.Core`). AI is used only to *suggest* column mappings, and the user confirms them before anything is saved.

## Background processing

Uploading a run is fast. The request checks plan limits, inspects and virus-scans every file, stores the files
encrypted, and queues the run. `RunWorker` does the parsing, reconciliation and report generation in the background:

- **Durable queue.** The queue lives in the database, so runs survive restarts and deploys.
- **No double processing.** A worker claims a run with an atomic conditional `UPDATE`, so two workers or app instances never process the same run.
- **Heartbeats and retries.** A running job updates a heartbeat. A run whose heartbeat goes stale (for example after a crash) is re-queued, up to `Workers:MaxAttempts` attempts.
- **Live progress.** The run page polls for progress and switches to the results automatically.

Settings are under `Workers` in `appsettings.json`: `Concurrency`, `PollSeconds`, `StaleAfterSeconds` and `MaxAttempts`.

## Upload scanning

Every upload goes through `UploadInspector`:

1. **Structural checks.** Extension allow-list, size limit and magic bytes. Workbooks are rejected if they contain
   macros (including legacy XLS `_VBA_PROJECT`), embedded OLE objects, ActiveX, external workbook links or custom ribbon code.
   A zip-bomb guard and a binary-in-CSV check also run here.
2. **ClamAV.** Files are streamed to `clamd` over its INSTREAM protocol, so no temp files are written. `Scanning:Mode` controls what happens:
   - `Off`: no antivirus scan.
   - `Optional`: scan when ClamAV is reachable, and accept files marked "not scanned" when it isn't. This is the dev default.
   - `Required`: refuse uploads while ClamAV is down. **Use this in production.**

Infected files are never stored, and each block is written to the audit log. Each file's scan result appears on the run page.

To enable ClamAV on macOS:

```bash
brew install clamav && cp $(brew --prefix)/etc/clamav/freshclam.conf.sample $(brew --prefix)/etc/clamav/freshclam.conf && sed -i '' 's/^Example/#Example/' $(brew --prefix)/etc/clamav/freshclam.conf && freshclam
```

Then configure `clamd.conf` with `TCPSocket 3310` and start `clamd`. In Docker, use `clamav/clamav` and expose port 3310.

## AI column mapping (Ollama, open source)

`HybridMappingSuggester` runs the rule-based matcher first, then asks a local model via [Ollama](https://ollama.com) to
fill gaps such as `Chauffeur Cd` → Driver ID or `Hire Chgs (INR)` → Amount.

- **Structured output.** Ollama's `format` JSON schema is used and temperature is 0.
- **Validation.** The model's answers are never trusted blindly:
  - an exact rule match always wins;
  - made-up column names are dropped;
  - a column is never mapped twice;
  - a proposal is rejected if the sample values don't fit the field type, for example text proposed for an Amount field.
- **Fallback.** If Ollama is down or slow, the rule-based suggestions are shown with a notice.
- **Privacy.** Only the headers and 5 sample rows are sent, and only to the `Ollama:BaseUrl` you configure.

```bash
ollama pull llama3.2:3b
```

Configure it under `Ollama` in `appsettings.json` (`Enabled`, `BaseUrl`, `Model`). `llama3.2:3b` takes about 10 seconds per file
on a laptop. Larger models such as `qwen2.5:7b` are more accurate but slower. Set `OLLAMA_TEST=1` to run the live test.

## Layout

```
src/SettleFlow.Core     engine, parsing (ExcelDataReader), report (ClosedXML), presets, sample data
src/SettleFlow.Web      ASP.NET Core Razor Pages, EF Core, Identity, multi-tenant
tests/SettleFlow.Tests  engine, parsing, injection, upload checks, ClamAV protocol, AI merge rules,
                        queue end-to-end + crash recovery, tenant isolation
```

## Security

- **Tenant isolation.** EF global query filters on every tenant-owned table. Writes to another tenant's rows throw an error. This is covered by a test.
- **Encryption at rest.** Uploads and reports are encrypted with a per-tenant Data Protection key. Storage keys are tenant-prefixed and protected against path traversal.
- **Upload checks.** Uploads are checked against an extension allow-list, plan size limits and magic bytes. Macro workbooks (`vbaProject`) are rejected. A zip-bomb ratio guard, a row limit and binary-in-CSV detection are also applied.
- **Formula injection.** Exported text starting with `= + - @ | %` or a tab or CR is apostrophe-prefixed, and totals are written as values, not formulas.
- **Retention.** A background job deletes files older than the tenant's retention setting. Users can also delete them per run. Summaries and the audit log are kept.
- **Auth and headers.** Identity with lockout. Antiforgery is on all POSTs. CSP, `X-Frame-Options` and `nosniff` headers are set.
- **Audit log.** Records workflow edits, mappings, runs (with file SHA-256), exception reviews, downloads, deletions and member changes.

## Email

Sign-up confirmations, invites, password resets and "your checked report is ready" notices are sent over SMTP using MailKit. Any provider works,
including Amazon SES, Resend, Postmark, Zoho and Google Workspace. In development, `Email:Mode=Pickup` writes `.eml`
files to `App_Data/mail` instead of sending anything.

| Variable | Example |
|---|---|
| `Email__Mode` | `Smtp` |
| `Email__SmtpHost` / `Email__SmtpPort` | `email-smtp.ap-south-1.amazonaws.com` / `587` |
| `Email__Security` | `StartTls` (port 587) or `SslOnConnect` (port 465) |
| `Email__Username` / `Email__Password` | SMTP credentials |
| `Email__FromAddress` / `Email__FromName` | `no-reply@yourdomain.com` / `Reconza` |
| `Email__TimeoutSeconds` | `30` |

Links in emails are built from `App__PublicUrl` and never from the request's Host header. Sign-up confirmation and
reset links expire after 3 hours; invites expire after 3 days. Confirmation, reset and invite links are single-use.

## Billing (Razorpay for ₹, Stripe for $)

Customers pay on the providers' hosted pages, so Reconza never handles card data. **A plan changes only when a
signature-verified webhook arrives**, never because of a browser redirect. Each webhook is applied once
(`ProcessedWebhookEvents`). Webhooks older than 5 minutes are rejected (Stripe). A failed payment keeps the plan
during a grace period and shows a banner. A cancelled subscription drops the tenant back to Trial limits, and their data is kept.

**Stripe (international, USD):**
1. Create one product per plan, each with a monthly recurring USD price: Starter $19, Business $49, Team $99.
2. Add a webhook endpoint `https://<your-app>/webhooks/stripe` with these events:
   - `checkout.session.completed`
   - `customer.subscription.created`
   - `customer.subscription.updated`
   - `customer.subscription.deleted`
3. Enable the Customer Portal under Billing → Customer portal. It handles upgrades, cancellations and invoices.
4. Set these variables:
   - `Billing__Stripe__SecretKey`
   - `Billing__Stripe__WebhookSecret`
   - `Billing__Stripe__Prices__Starter`, `Billing__Stripe__Prices__Business` and `Billing__Stripe__Prices__Team`

**Razorpay (India, INR, with UPI Autopay, cards and e-mandate):**
1. Enable Subscriptions and create monthly plans: Starter ₹999, Business ₹2,999, Team ₹6,999.
2. Add a webhook `https://<your-app>/webhooks/razorpay` with these events:
   - `subscription.activated`, `subscription.charged` and `subscription.updated`
   - `subscription.pending` and `subscription.halted`
   - `subscription.cancelled` and `subscription.completed`
   - `subscription.paused` and `subscription.resumed`
3. Set these variables:
   - `Billing__Razorpay__KeyId`
   - `Billing__Razorpay__KeySecret`
   - `Billing__Razorpay__WebhookSecret`
   - `Billing__Razorpay__Plans__Starter`, `Billing__Razorpay__Plans__Business` and `Billing__Razorpay__Plans__Team`

Use **test-mode keys** first. Stripe's card `4242 4242 4242 4242` and Razorpay's test UPI `success@razorpay` let you go
through a whole subscription without real money. To deliver Stripe webhooks to your laptop, run `stripe listen --forward-to localhost:5187/webhooks/stripe`.

## Release & deploy (Plesk / Windows shared hosting)

```bash
scripts/publish.sh
```

This runs the tests and produces `dist/reconza-win-x64.zip`. The package is self-contained and has no debug
symbols. The script refuses to package dev settings, local databases, `App_Data`, or local machine paths.

Upload the package and follow **[DEPLOY-PLESK.md](DEPLOY-PLESK.md)**, which is included in the zip.

Server secrets and business details live in `appsettings.Server.json` on the server; start from
`appsettings.Server.example.json`. Updates never overwrite that file.

The production defaults in `appsettings.Production.json` suit a single server:
- SQLite with versioned migrations, or MySQL 8 via `Database:Provider=MySql` and `ConnectionStrings:MySql`
  (Oracle's MySql.EntityFrameworkCore, with its own migrations);
- encrypted files, reports and the encrypted key ring in the server database;
- optional ClamAV scanning and AI suggestions off.

Any default can be overridden with environment variables. For example, `DATABASE_URL` switches to PostgreSQL.

Production refuses to start without these settings:
- `App:PublicUrl` (`https://…`)
- `DataProtection:MasterKey`
- the company details shown on the Terms, Privacy, Refund and Contact pages
- working SMTP settings for sign-up confirmations, invitations, password resets and completion notices

These checks stop the site from going live with placeholder legal pages or a missing encryption key.

## Deploy (Render free + Neon)

`render.yaml` is a Render Blueprint for a free web service in Singapore. With it:

- Encrypted files go in Postgres (`Storage__Provider=Database`).
- AI mapping and ClamAV are off.
- The encryption master key is generated by Render.
- The public URL comes from `RENDER_EXTERNAL_URL`.

To deploy:

1. Create a free Neon project in the AWS Singapore region. Copy the **direct** (non-pooled) connection string.
2. In Render, choose **New → Blueprint**, pick this repo, and paste the string into `DATABASE_URL`.
3. Copy `DataProtection__MasterKey` from the service's Environment tab into your password manager.

On the free plan the service sleeps after 15 idle minutes, and the first request after that takes about a minute.

## Deploy (Railway)

The repository holds four services:

| Service | Source | Notes |
|---|---|---|
| `web` | `Dockerfile`, `railway.json` | Health check on `/readyz`, port from `$PORT` |
| `ollama` | `deploy/ollama/` | Pulls `llama3.2:3b` on first start. Needs about 4 GB RAM and a volume at `/root/.ollama` |
| `clamav` | `deploy/clamav/` | Listens on 3310 over IPv6 and IPv4. A volume at `/var/lib/clamav` keeps signatures |
| Postgres | Railway database | `DATABASE_URL` is referenced from it |
| Bucket | Railway bucket (S3-compatible) | Holds the encrypted uploads and reports |

Required variables on `web`:

```
DATABASE_URL=${{Postgres.DATABASE_URL}}
DataProtection__MasterKey=<openssl rand -base64 32>   # keep a copy in your password manager; losing it makes stored files unreadable
App__PublicUrl=https://<your-domain>
Storage__S3__ServiceUrl / Storage__S3__Bucket / Storage__S3__AccessKey / Storage__S3__SecretKey / Storage__S3__Region
Email__* (see above), Billing__* (see above)
```

At startup the app runs EF Core migrations against PostgreSQL. It refuses to start in Production if any of these hold:
- a durable database/storage provider, the master key, SMTP or a public URL is missing;
- the public URL is `localhost`;
- manual plan changes are allowed.

When ClamAV is deployed, set `Scanning__Mode=Required`. Uploads are then refused while ClamAV is down.

## Not built yet

- **Tax invoices with GST.** Razorpay and Stripe issue receipts; GST-compliant invoices need Razorpay Invoices or Stripe Tax.
- **Admin console** for support staff.
