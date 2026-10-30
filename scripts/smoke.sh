#!/usr/bin/env bash
set -euo pipefail

hub_url="${1:?Usage: scripts/smoke.sh <hub-url>}"

curl --fail --silent --show-error "${hub_url%/}/health" | grep -q '"status":"ok"'

status="$(
  curl --silent --output /dev/null --write-out '%{http_code}' \
    --request POST "${hub_url%/}/" \
    --header 'Content-Type: application/x-www-form-urlencoded' \
    --data-urlencode 'hub.mode=subscribe' \
    --data-urlencode 'hub.verify=async' \
    --data-urlencode 'hub.topic=https://example.invalid/feed.xml' \
    --data-urlencode 'hub.callback=https://example.invalid/callback'
)"
if [[ "$status" != "403" ]]; then
  echo "Expected disallowed topic to return 403; received $status." >&2
  exit 1
fi

status="$(
  curl --silent --output /dev/null --write-out '%{http_code}' \
    --request POST "${hub_url%/}/" \
    --header 'Content-Type: application/x-www-form-urlencoded' \
    --data-urlencode 'hub.mode=publish' \
    --data-urlencode 'hub.url=https://lqdev.me/.well-known/following.opml'
)"
if [[ "$status" != "401" ]]; then
  echo "Expected publish without token to return 401; received $status." >&2
  exit 1
fi

echo "WebSub hub smoke checks passed."
