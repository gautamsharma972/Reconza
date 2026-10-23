# Reconza launch checklist

The application fails closed for missing production identity, encryption, durable storage and email settings. Complete these external steps before directing customers to the site.

## Release candidate status — 23 October 2026

- [x] Public and signed-in UI redesign completed without changing reconciliation rules.
- [x] Sign-up confirmation, invitation, invitation resend, password reset and completed-check email paths covered by automated tests.
- [x] SMTP SSL connection, authentication and delivery to `contact@bytehills.com` verified.
- [x] Full Release suite passes (67 tests); NuGet reports no vulnerable direct or transitive packages.
- [x] Portable Plesk package built and checked for secrets, development settings, local data and debug files.
- [ ] Set the final `App.PublicUrl`, create the production MySQL database and generate/back up `DataProtection.MasterKey`.
- [ ] Configure DNS/HTTPS plus SPF, DKIM and DMARC; verify delivery to at least one mailbox outside the sending domain.
- [ ] Configure live billing only after signed webhooks and test-mode subscription flows pass on staging.
- [ ] Complete the legal, backup/restore, malware-scanning and production-domain checks below.

## Required before launch

- Confirm the configured Bytehills Tech LLP legal name, Gangtok address, contact email, jurisdiction and 23 October 2026 policy effective date. Add phone and GSTIN if required.
- Have qualified counsel review the Terms, Privacy and Refund pages for the operating entity and launch markets. The repository copy is a product implementation, not legal approval.
- Configure SMTP and verify sign-up confirmations, account invitations, password resets and completed-check emails arrive with SPF, DKIM and DMARC aligned.
- Use MySQL/PostgreSQL plus `Storage.Provider=Database`, or S3-compatible storage. Back up the database and `DataProtection.MasterKey`, then perform one restore test.
- Configure HTTPS, the canonical production URL and DNS redirects. Confirm `/healthz`, `/readyz`, `/robots.txt` and `/sitemap.xml` on the public domain.
- Run one full staging check: register, create a saved check, upload files, review an issue, download the report and delete retained files.

## Security and operations

- Deploy ClamAV and use `Scanning.Mode=Required` when the infrastructure supports it. Until then, `Optional` records unscanned files while structural workbook checks remain active.
- Set production billing credentials in test mode first. Verify signed Stripe/Razorpay webhooks, cancellation and failed-payment behaviour before switching to live keys.
- Confirm retention defaults, log retention, alerting, database capacity and the recovery owner.

## Marketing and measurement

- Add Google/Bing verification codes and submit the sitemap.
- Optionally add the Cloudflare Web Analytics token. It is cookieless and already disclosed in the Privacy page; do not add a consent banner unless another tracking provider is introduced.
- Re-run Lighthouse and a mobile/desktop visual check against the deployed domain after CDN and production headers are active.
