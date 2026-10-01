# Security policy

OpenTube guards videos that are meant to stay private. A flaw that lets someone watch what they
were not granted, keep watching after access was revoked, or sign in as someone else is treated as
the most serious kind of bug this project can have.

## Supported versions

Only the latest published images receive security fixes. There are no backports.

| Component | Supported |
| --- | --- |
| `ghcr.io/allanbarcelos/opentube/app:latest` (latest `app-v*` release) | ✅ |
| `ghcr.io/allanbarcelos/opentube/worker:latest` (latest `worker-v*` release) | ✅ |
| `ghcr.io/allanbarcelos/opentube/whisper:cpu` / `:cuda` | ✅ |
| `install.sh` / `uninstall.sh` on `main` (and the gist that mirrors them) | ✅ |
| Older images and tags | ❌ — update with `/opt/<name>/scripts/update.sh` |

## Reporting a vulnerability

**Please do not open a public issue, pull request, or discussion for a security problem.**

Report it privately through GitHub: **Security → Report a vulnerability** on this repository
(private vulnerability reporting). Reports in English or Portuguese are welcome.

Helpful to include:

- what an attacker can do, and with which starting access (anonymous visitor, invited viewer,
  domain member, holder of a share link, administrator);
- steps to reproduce, or a proof of concept against your own installation;
- the image tags or commit you tested;
- any suggested fix.

What to expect:

- an acknowledgement, and a first assessment of severity and scope;
- updates while a fix is prepared, and a request for your review of it when that helps;
- a published fix and a GitHub Security Advisory, crediting you unless you prefer otherwise.

Please give a reasonable time for a fix before disclosing publicly, and test only against an
installation you own. Do not access, change, or delete data that is not yours, and do not run
denial-of-service or volume tests against someone else's server.

## Scope

**In scope** — anything that breaks the promises the application makes:

- **Access control**: watching, listing, searching, or downloading a video (or its captions,
  thumbnail, renditions, or segments) without a valid grant; getting around expiration, view
  limits, revocation, or the limit on simultaneous playbacks. Every such path goes through
  `AccessPolicy` / `CanWatch`, and segments are authorized one by one through Caddy's
  `forward_auth`.
- **Authentication**: signing in without the code or link, reusing a code or link, brute-forcing
  codes past the rate limits, session fixation or theft, taking over another account.
- **Domain grants**: watching through a domain grant with an address outside that domain, or
  without proving the address with the code.
- **Administration**: reaching administrative pages or actions without being an administrator,
  cross-site request forgery, stored or reflected XSS (video titles, captions, comments, the
  watermark image).
- **Uploads and processing**: files that make the worker, FFmpeg, or the caption parser run code,
  read other files, or write outside their own storage keys.
- **Privacy**: exposing viewers' emails, IP addresses, or viewing history to anyone other than
  the administrators.
- **Deployment**: `install.sh` or `update.sh` leaving secrets on disk or in logs, opening ports
  beyond what the chosen mode needs, or letting the origin be reached around Cloudflare in
  Cloudflare mode; the published images carrying secrets.

**Out of scope** — known and documented limits:

- Copying by someone who has legitimate access. Without DRM, a viewer can record the screen or
  capture the stream; the player only deters casual copying (watermark with the viewer's email,
  no download or casting), and the access log records who watched. See
  [Authorized delivery](README.md#authorized-delivery).
- The watermark not showing in native full screen and Picture-in-Picture of some browsers, where
  the browser draws only the video (the viewer's email still appears as a caption).
- Findings that need an already compromised server, Docker host, or administrator account.
- Volumetric denial of service, and missing rate limits on endpoints that expose nothing.
- Vulnerabilities in third-party components (PostgreSQL, MinIO, Caddy, FFmpeg, whisper.cpp, .NET)
  that are not caused by how OpenTube uses them — please report those upstream. The MinIO image
  used is the last community release; see the note in the README.
- Reports from automated scanners without a demonstrated impact.

## Security design, in short

- **No passwords.** Access is by a single-use link or a 6-digit code sent by email. Codes and
  tokens are stored only as hashes and expire in 15 minutes. The invitation email carries no
  code: it points to the sign-in page, where the code is requested.
  Secret-link tokens are also kept encrypted (AES-GCM, with a key derived from the server secret)
  so administrators can copy a link again; the database alone does not reveal them.
  Sessions last 30 days, in an `HttpOnly`, `SameSite=Lax` cookie, and administrators can revoke
  them at once.
- **Rate limits** on codes: 5 requests per email and 50 per origin every 10 minutes, 5 guesses
  per code, and 20 wrong guesses per email in 24 hours, after which only the emailed link signs
  in. Each attempt is reserved in the database before the comparison, so parallel requests
  cannot exceed the limit.
- **One access decision** (`CanWatch`) for the whole application: home, search, player, playlist,
  segments, captions, thumbnail, download. It is the most heavily tested part of the code.
- **Per-segment authorization** in production: revoking access takes effect on the next 4-second
  segment, not when a signed URL expires.
- **Secrets** (database, storage, peppers, SMTP) live only as Docker Swarm secrets, mounted as
  files readable by the process user. Nothing is committed to the repository or written to the
  stack file.
- **Network**: only Caddy publishes a port; everything else talks over an encrypted overlay
  network. In Cloudflare mode, the origin port answers only Cloudflare's ranges, in UFW and in
  `DOCKER-USER`.
- **Privacy**: IP addresses are stored as hashes with a rotating secret; raw playback events are
  kept for 24 months; deleting a user anonymizes their events. Every administrative action is
  audited.

More detail in [Security and privacy](README.md#security-and-privacy).

## Keeping an installation safe

- Run `/opt/<name>/scripts/update.sh` regularly to pull the latest images.
- Keep the host updated (`unattended-upgrades` on Ubuntu/Debian) and SSH restricted to keys.
- Keep the administrator list short; it is set by email in the stack.
- Back up `/opt/<name>/data` and the MinIO data directory. The Swarm secrets cannot be read back:
  keep the summary printed at the first installation somewhere safe.
- In Cloudflare mode, keep SSL/TLS and *Always Use HTTPS* as the installer summary describes.
