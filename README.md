# WebSub Hub

A small, content-agnostic [WebSub](https://www.w3.org/TR/websub/) hub hosted on Azure Functions.
The hub fetches a topic after a publisher ping, compares the response body, and sends the full
updated representation to verified subscriber callbacks. It does not parse feeds or OPML.

That means a topic may be RSS, Atom, OPML, JSON, or another HTTP representation. The hub preserves
the fetched `Content-Type`; for OPML, the subscriber receives the complete OPML document and decides
how to reconcile it.

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/` | WebSub subscribe, unsubscribe, and authenticated publish requests |
| `GET` | `/health` | Health/configuration status; never returns settings or tokens |

For subscribe/unsubscribe, the hub returns `202 Accepted` and performs asynchronous callback intent
verification. Requests must include `hub.verify=async`. A successful verification response body must
exactly match `hub.challenge`.

Publishing is protected by `X-WebSub-Publish-Token`. The deployment requires a token of at least
32 characters. Do not put its value in source, command output, or a committed parameter file.

## Deploy with azd

Prerequisites: .NET 10 SDK, Azure CLI, Azure Developer CLI (`azd`), and an Azure subscription where
you may create resource groups, managed identities, and role assignments.

```bash
azd auth login
azd init -e websub-prod
azd env set WEBSUB_PUBLISH_TOKEN "$(openssl rand -hex 32)"
azd up
azd env get-values | grep '^HUB_URL='
```

`azd up` provisions the infrastructure and deploys the Function App. It uses Flex Consumption
(FC1), a user-assigned managed identity, Azure Storage, Log Analytics, and Application Insights.
No custom domain is configured; `HUB_URL` is the Function App's default
`https://<app-name>.azurewebsites.net/` URL.

The hub accepts these topics by default:

- `https://lqdev.me/`
- `https://www.lqdev.me/`
- `https://luisquintanilla.me/`
- `https://www.luisquintanilla.me/`

The prefix list and open-mode switch are deployment parameters, not code constants. For example:

```bash
azd env set WEBSUB_ALLOWED_TOPIC_PREFIXES "https://lqdev.me/,https://www.lqdev.me/"
azd provision
```

Keep open mode disabled for this personal hub. Enabling it allows any public HTTP(S) topic, subject
to the hub's public-address checks:

```bash
azd env set WEBSUB_OPEN_MODE true
azd provision
```

Changing application code uses `azd deploy`; changing infrastructure parameters uses `azd provision`.
The `.azure/` directory contains local azd environment state and is ignored by git. Do not commit it.

### GitHub Actions deployment

The workflow in `.github/workflows/azure-dev.yml` uses OIDC; it does not store Azure client secrets.
After creating the GitHub repository, run `azd pipeline config --provider github` once and follow
its prompts to configure the federated identity and repository variables. Set
`WEBSUB_PUBLISH_TOKEN` as a GitHub Actions **secret** and the prefix list as a repository **variable**
only if overriding its default. The workflow never prints the token.

## Local development

1. Copy `src/WebSubHub/local.settings.json.example` to `src/WebSubHub/local.settings.json`.
   The copied settings file is ignored. Do not put production values in it.
2. Start Azurite (for example, `npx azurite --silent`).
3. Generate a temporary local publish token and export it in the shell that launches Functions:
   ```bash
   export WEBSUB_PUBLISH_TOKEN="$(openssl rand -hex 32)"
   ```
4. Start the Functions host from the project directory:
   ```bash
   cd src/WebSubHub && func start
   ```
5. In another terminal, run `dotnet fsi scripts/echo-callback.fsx`.

The local settings example permits loopback URLs only for local testing. The deployed Bicep setting
forces `WEBSUB_ALLOW_LOCAL_URLS=false`; don't enable it in Azure.

## Local smoke test

With the hub, Azurite, and callback script running:

```bash
export LOCAL_HUB_SECRET="$(openssl rand -hex 32)"
curl -i -X POST http://localhost:7071/ \
  -H 'Content-Type: application/x-www-form-urlencoded' \
  --data-urlencode 'hub.mode=subscribe' \
  --data-urlencode 'hub.verify=async' \
  --data-urlencode 'hub.topic=http://localhost:8099/topic.opml' \
  --data-urlencode 'hub.callback=http://localhost:8099/callback' \
  --data-urlencode "hub.secret=$LOCAL_HUB_SECRET"
```

The echo callback logs the verification request and acknowledges the challenge. To publish, use the
temporary token from the same shell in which it was exported:

```bash
curl -i -X POST http://localhost:7071/ \
  -H 'Content-Type: application/x-www-form-urlencoded' \
  -H "X-WebSub-Publish-Token: $WEBSUB_PUBLISH_TOKEN" \
  --data-urlencode 'hub.mode=publish' \
  --data-urlencode 'hub.url=http://localhost:8099/topic.opml'
```

The second request should deliver the OPML body to `/callback` with `Content-Type: text/x-opml`,
`Link` discovery headers, and `X-Hub-Signature`. The callback script does not print signature values
or request bodies.

For deployed smoke checks, run:

```bash
scripts/smoke.sh "$(azd env get-value HUB_URL)"
```

The smoke script checks health, default-deny topic policy, and unauthenticated publish rejection.
It does not print secrets.

## Configuration

| Setting | Default | Description |
|---|---|---|
| `WEBSUB_HUB_URL` | Function App URL from Bicep | Hub URL advertised in delivery `Link` headers |
| `WEBSUB_ALLOWED_TOPIC_PREFIXES` | The four lqdev.me URL prefixes above | Comma-separated allowlist |
| `WEBSUB_OPEN_MODE` | `false` | Allow all public HTTP(S) topic URLs when enabled |
| `WEBSUB_PUBLISH_TOKEN` | Required | Secret header value for publish requests |
| `WEBSUB_ALLOW_LOCAL_URLS` | `false` | Local-only setting to permit loopback callbacks/topics |
| `WEBSUB_DEFAULT_LEASE_SECONDS` | `864000` | Default subscription lease (10 days) |
| `WEBSUB_MIN_LEASE_SECONDS` | `3600` | Minimum accepted lease (1 hour) |
| `WEBSUB_MAX_LEASE_SECONDS` | `2592000` | Maximum accepted lease (30 days) |
| `WEBSUB_MAX_TOPIC_BYTES` | `5242880` | Maximum topic payload (5 MiB) |

## Security and cost notes

- Subscription callbacks undergo DNS/IP checks both at verification and delivery; private, loopback,
  link-local, and reserved addresses are rejected. Topic redirects are followed only after the
  redirect target passes the same check, with a five-redirect limit and no HTTPS-to-HTTP downgrade.
- Topic allowlisting is on by default. Open mode is an explicit deployment parameter.
- Publish notifications require a random shared token. Subscriber delivery signatures use HMAC-SHA256.
- Azure Storage data access uses a managed identity and role assignments; account shared-key
  authentication is disabled.
- `local.settings.json`, `.azure/`, and `infra/local.parameters.json` are gitignored. The checked-in
  example contains no credentials.
- Flex Consumption is usage-based and has a monthly free grant in supported regions. Storage and
  monitoring may still incur charges; confirm current prices for the selected region before deploying.

## Architecture

```text
Publisher ── POST hub.mode=publish + token ──► HubEndpoint
                                                 │
                                                 ▼
                                           Fetch queue
                                                 │
                                       GET topic + hash
                                                 │
                                  changed ───────┴─────── unchanged
                                    │                         │
                                    ▼                         └─ stop
                              Blob payload
                                    │
                           Delivery queue(s)
                                    │
                          verified callbacks
```

Storage tables hold subscriptions and each topic's current hash/ETag. Topic bodies are stored in
Blob Storage so queue messages remain small and each fan-out delivery gets the exact representation
that triggered it. Hourly lease cleanup removes expired subscriptions.
