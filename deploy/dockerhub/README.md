# EAMS ssl sender

**SSL certificates from ManageEngine Key Manager Plus, delivered by email — automatically.**
A certificate is renewed in Key Manager Plus, and the people who install it get its files in their inbox:
PEM, CRT, CER, DER, P7B, PFX, JKS or KEY in one ZIP archive, by subscription, on renewal or on a schedule.

- Source, documentation and releases: [github.com/danrey-bilo/eams-ssl-sender](https://github.com/danrey-bilo/eams-ssl-sender)
- User guide (English and Russian): [docs](https://github.com/danrey-bilo/eams-ssl-sender/tree/main/docs)
- Changelog: [CHANGELOG.md](https://github.com/danrey-bilo/eams-ssl-sender/blob/main/CHANGELOG.md)

## Tags

| Tag | What it is |
|---|---|
| `latest` | The newest release |
| `2.6` | The newest 2.6.x |
| `2.6.1` | A fixed version — recommended for servers where updates are planned |

## Run with Portainer

1. Download `email-ssl-sender-vX.Y.Z.zip` from [Releases](https://github.com/danrey-bilo/eams-ssl-sender/releases/latest):
   `docker-compose.yml`, `.env.example` with hints and a step-by-step README.
2. Fill in `.env.example`: the master key, two database passwords, the Cloudflare Tunnel token and the first
   administrator (the commands to generate them are in the file).
3. **Stacks → Add stack → Upload** `docker-compose.yml` → **Load variables from .env file** → **Deploy the stack**.
   The image is pulled from here; the `migrate` service prepares the database before the service starts.
4. To update: **Update the stack** with **Re-pull image and redeploy** (with `SENDER_IMAGE=danreero/eams-ssl-sender:latest`),
   or set a new version in `SENDER_IMAGE`.

Command-line installation with secrets in files: [README](https://github.com/danrey-bilo/eams-ssl-sender#installation-from-the-command-line).

## What you need

- PostgreSQL 16 (part of the stack), a mailbox with SMTP access, Key Manager Plus 7.1.1+ with a certificate issued
  by a certificate authority and its REST API key.
- Cloudflare Tunnel or your own reverse proxy with TLS — the service publishes no ports to the outside.

## Inside the image

ASP.NET Core 10 API with the React web panel on Ubuntu 24.04 chiseled: no shell and no package manager, runs as a
non-root user, read-only file system, no Linux capabilities. Web panel in English, Ukrainian and Russian; sign-in with a
password and an authenticator app.

## License

Free to use. Copying, modifying and redistributing the code is not allowed —
[LICENSE](https://github.com/danrey-bilo/eams-ssl-sender/blob/main/LICENSE).
