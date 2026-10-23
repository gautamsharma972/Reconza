# Deploying Reconza on Plesk (Windows hosting)

There are two packages. Use **one**:

| Package | Runs in | Server needs |
|---|---|---|
| **`reconza-portable.zip`** (recommended) | **32-bit and 64-bit** application pools | **.NET 10 Hosting Bundle** (ASP.NET Core Runtime 10.x) |
| `reconza-win-x64.zip` | 64-bit application pools only | Nothing extra; .NET is included |

**Is .NET 10 on the server?**
- In Plesk, open **Hosting Settings** and look in the **.NET Core / ASP.NET Core** version list. You need **10.x**.
- Or ask your host: "Is the ASP.NET Core 10 Hosting Bundle installed?"
- If it isn't, use `reconza-win-x64.zip` in a 64-bit app pool, or ask the host to install the bundle.

The portable package starts through IIS's shared `dotnet` runtime, matching the pool's bitness (32 or 64-bit).
Both packages need IIS's **ASP.NET Core Module**, which the Hosting Bundle installs.

It uses the **MySQL or MariaDB** database you create in Plesk, with uploaded files stored encrypted on the local disk
in `App_Data`. If you'd rather skip database setup, set `Database.Provider` to `Sqlite` and the database becomes a
single file in `App_Data`.

---

## 1. Create the site in Plesk

1. **Websites & Domains → Add Domain** (or use an existing one). Turn on **SSL/TLS** with Let's Encrypt and
   enable **Permanent SEO-safe 301 redirect from HTTP to HTTPS**.
2. **Hosting Settings → .NET / ASP.NET:** select **ASP.NET Core** (wording varies by Plesk version).
3. **Dedicated IIS Application Pool:** turn it on if your plan allows. In the pool settings:
   - **Enable 32-bit applications:** either setting works with `reconza-portable.zip`. With `reconza-win-x64.zip`,
     it must be **Off**.
   - **.NET CLR version: No Managed Code** (if shown).
   - **Idle timeout:** set it to 0 or as high as allowed. See "Background processing" below.

## 2. Upload the files

1. Unzip the package (`reconza-portable.zip` or `reconza-win-x64.zip`) on your computer.
2. Upload **all of its contents** into the site's document root (usually `httpdocs`), using Plesk
   **File Manager → Upload** or FTP. `web.config` and `Reconza.Web.dll` must be directly inside `httpdocs`.
3. Give the application pool user **write** permission on `httpdocs` so the app can create `App_Data`. In
   Plesk: **File Manager → right-click httpdocs → Change Permissions → Plesk IIS WP User / application pool
   user → Modify/Write**. You can create the `App_Data` folder yourself and grant write on just that folder.

## 3. Create the MySQL / MariaDB database

1. In Plesk, go to **Databases → Add Database**:
   - **Database server:** MariaDB or MySQL, whichever your Plesk offers
   - **Database name:** `reconza` (Plesk may add a prefix)
   - **Database user:** a new user such as `reconza_user`, with a strong generated password.
     Give it access to this database only.
2. Note the exact database name, user name and password. Use the server address Plesk shows, usually `localhost`.

3. **Create the tables:** open the database in **phpMyAdmin** (Plesk → Databases → reconzadb → phpMyAdmin), choose
   **Import**, select **`reconza-database.sql`** from this package, and click **Go**. You should get 17 tables.
4. Keep `"AutoMigrate": false` in `appsettings.Server.json`. The app then never alters the database itself; it only
   checks at start-up that the tables match this version.

**On updates:** if a release changes the database, it will say so and include an upgrade script to import the same way.
On MySQL 8 or PostgreSQL you can instead set `"AutoMigrate": true` and let the app update the tables itself.

## 4. Create `appsettings.Server.json` (required)

In `httpdocs`, copy `appsettings.Server.example.json` to **`appsettings.Server.json`** and fill in:

| Setting | What to enter |
|---|---|
| `App.PublicUrl` | Your site's address, starting `https://`, e.g. `https://www.reconza.com` |
| `Database.Provider` | `MySql` |
| `Database.AutoMigrate` | `false` (tables come from `reconza-database.sql`) |
| `ConnectionStrings.MySql` | `Server=localhost;Port=3306;Database=<db name>;User=<db user>;Password=<password>;SslMode=Preferred;AllowPublicKeyRetrieval=true;CharSet=utf8mb4`. If the database is on another server, use `SslMode=Required` and leave out `AllowPublicKeyRetrieval`. |
| `DataProtection.MasterKey` | 32 random bytes in base64 (see below). **Back it up in your password manager.** If it's lost, stored files can't be read. |
| `Company.*` | Legal name, address, contact email, jurisdiction (city and state for the courts), effective date of your terms. These appear on the Terms, Privacy, Refund and Contact pages. |
| `Email.*` | SMTP details for invites, password resets and notifications. Plesk's own mail server works: create a mailbox such as `no-reply@yourdomain.com` and use `mail.yourdomain.com` with port 587. |
| `Billing.*` | Leave empty until you have Razorpay or Stripe keys. The Billing page then shows "contact us to upgrade". |

To generate the master key in PowerShell on any Windows PC:

```powershell
$b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)
```

The app **refuses to start** if `PublicUrl`, `MasterKey` or the company details are missing. That stops a site
with placeholder legal pages from ever going public. If you get a 500.30 error after uploading, this is the usual cause.

## 5. Start and check

1. In Plesk, **restart the application pool** (or **Hosting Settings → Apply**).
2. Open these:
   - `https://yourdomain.com/healthz` should show `Healthy`.
   - `https://yourdomain.com/` shows the homepage.
   - `/sitemap.xml` should list your https URLs.
3. **Sign up** to create your own company account. The first user of each company is its owner.
4. Check that `/terms`, `/privacy`, `/refund-policy` and `/contact` show your company details.

## 6. Search engines and analytics

1. **Google Search Console**:
   1. Add a URL-prefix property for `https://www.reconza.com`.
   2. Choose **HTML tag** and paste only the `content` value into `Seo.GoogleVerification` in `appsettings.Server.json`.
   3. Restart the app pool and click **Verify**.
   4. Submit `/sitemap.xml`.
2. **Bing Webmaster Tools**: use **Import from Google Search Console**, or put its code in `Seo.BingVerification`.
3. **Request indexing** for:
   - `/`
   - `/tools/compare-excel-files`
   - `/guides/driver-settlement-excel-template`
   - the three `/solutions/...` pages
4. **Analytics (optional)**: create a free **Cloudflare Web Analytics** site and put its token in `Seo.CloudflareAnalyticsToken`.
   It's cookieless, so no consent banner is needed.
5. **Canonical domain**: make sure both `reconza.com` and `www.reconza.com` point to the site in Plesk (domain alias).
   The app permanently redirects every other host name to the address in `App.PublicUrl`, so search engines see one site.

---

## Updating to a new version

1. Build a new package with `scripts/publish.sh`.
2. In Plesk, **stop the application pool**, or upload an `app_offline.htm` file into `httpdocs`, which shows a
   maintenance page.
3. Upload the new files, overwriting the old ones. **Do not delete** `App_Data` or `appsettings.Server.json`.
4. Remove `app_offline.htm` or start the pool again. Database changes are applied automatically at startup.

## Backups

Back these up together, using Plesk **Backup Manager** (which includes databases):

- the **MySQL database**, which holds accounts, workflows, runs and the encrypted key ring;
- the **`App_Data/files`** folder, which holds the encrypted uploads and reports;
- **`appsettings.Server.json`**.

The master key is in `appsettings.Server.json`. Keep a separate copy of it, because the encrypted data can't be read without it.

## Background processing on shared hosting

Uploads are processed by a background worker inside the app. IIS stops idle sites after about 20 minutes. Queued
runs are **not lost**: they continue on the next request, and the "you uploaded files" request itself wakes the
site. Automatic file deletion under your retention setting also runs whenever the site is awake. For always-on
behaviour, ask your host to disable the idle timeout, or use an uptime monitor that opens `/healthz` every 5 minutes.

## Troubleshooting

| Symptom | Fix |
|---|---|
| **500.30 – app failed to start** | Usually a missing setting. Temporarily set `stdoutLogEnabled="true"` in `web.config`, create a `logs` folder, reload the site, and read the message in `logs\stdout_*.log`. Turn it off again afterwards. |
| **500.19 / module not found** | The host doesn't have the ASP.NET Core Module installed. Ask them to enable ASP.NET Core hosting. |
| **500.31 "Failed to find a compatible framework"** (portable package) | .NET 10 isn't installed on the server. Ask the host for the ASP.NET Core 10 Hosting Bundle, or use `reconza-win-x64.zip` in a 64-bit pool. |
| **500.32 "Failed to load .NET Core host"** | Bitness mismatch with `reconza-win-x64.zip` in a 32-bit pool. Use `reconza-portable.zip`, or turn off 32-bit applications. |
| **"attempt to write a readonly database"** (SQLite) or **"Access denied" writing files** | Grant the application pool user write permission on `App_Data`. |
| **"Retrieval of the RSA public key is not enabled"** | MySQL 8 sign-in needs `SslMode=Preferred;AllowPublicKeyRetrieval=true` (same server) or `SslMode=Required` in the connection string. |
| **MariaDB error at startup (500.30) or in logs, mentioning SQL syntax** | Reconza is tested on MySQL; MariaDB 11.4 is compatible by design but hasn't been tested yet. To go live immediately, set `"Database": { "Provider": "Sqlite" }`. The app then uses a database file in `App_Data`, with no other changes needed. Send the error from `logs\stdout_*.log` so it can be fixed for MariaDB. |
| **"Access denied for user" / "Unknown database"** | Check the database name, user and password in Plesk. Plesk often prefixes them, e.g. `admin_reconza`. |
| **Logs full of `AuthenticationTagMismatchException`, everyone signed out, old files unreadable** | `DataProtection.MasterKey` was changed. Put back the original key from your backup. A new key cannot read data encrypted with the old one. |
| **Empty page with a 500 and nothing in `logs`** | IIS rejected `web.config` before the app started. Use the `web.config` from this package unchanged; it is deliberately minimal for shared hosting. |
| **Uploads over ~28 MB in one run fail** | IIS limits a request to about 30 MB by default. If your host allows it, add `<security><requestFiltering><requestLimits maxAllowedContentLength="220200960" /></requestFiltering></security>` inside `<system.webServer>` in `web.config`. If the site then shows an empty 500, the host has locked that setting, so remove it again. |
| **`InvalidCastException ... DBNull ... AcquireDatabaseLock`** (MariaDB) | Fixed in this version. Also set `"AutoMigrate": false` and import `reconza-database.sql`. |
| **"The database schema is out of date"** at start-up | Import the SQL script that came with this release in phpMyAdmin. |
| **Emails not arriving** | Check the SMTP settings. Add SPF and DKIM records for your domain in Plesk (**Mail Settings**) so mail isn't marked as spam. |

## Optional upgrades later

- **PostgreSQL:** set `DATABASE_URL` to use PostgreSQL instead of SQLite (for multi-server hosting).
- **S3-compatible storage:** `Storage.Provider: S3`.
- **ClamAV virus scanning:** `Scanning.Mode: Required`, with ClamAV on a server you control.
- **AI column suggestions:** `Ollama.Enabled: true`, with Ollama on a server you control.

See `README.md` in the source repository for details.
