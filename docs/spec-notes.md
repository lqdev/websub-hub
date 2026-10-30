# WebSub and non-feed topics

The hub's content-agnostic behavior follows the WebSub Recommendation:

- The abstract describes communication between publishers of **any kind of Web content** and their
  subscribers.
- Section 4 discovery requires subscribers to check HTTP `Link` headers first. An OPML document
  does not need an embedded HTML `<link>` element to advertise `rel="hub"` and `rel="self"`.
- Section 7 requires a delivered payload's `Content-Type` to correspond to the topic's type and
  requires the full topic representation. The optional diff optimization is described for Atom and
  RSS, not required for other formats.

The hub therefore fetches topic URLs, hashes the response body, and forwards the full bytes together
with the fetched `Content-Type`. It neither parses nor rewrites OPML or feed data.

## OPML includes as separate topics

An OPML subscriber can treat each include outline as a separate list resource:

```xml
<outline type="include"
         title="Blogroll"
         text="Blogroll"
         url="https://lqdev.me/collections/blogroll/index.opml" />
```

The subscriber discovers `rel="hub"` and `rel="self"` on that URL and subscribes independently.
This keeps ownership and update provenance with the publisher of each included list. A subscriber
that recursively resolves lists should set a depth limit, detect cycles, and retain the source topic
for every resulting feed. This hub transports topic representations; it does not resolve includes.

## Verification, leases, and signatures

- Subscribe/unsubscribe intent is verified with an asynchronous GET request containing
  `hub.mode`, `hub.topic`, `hub.challenge`, and, for subscribe, `hub.lease_seconds`.
- The callback must return a successful HTTP status and the exact challenge as the response body.
- The hub clamps subscription durations to configured minimum and maximum values.
- If `hub.secret` is supplied, deliveries include `X-Hub-Signature: sha256=<hex HMAC-SHA256>`.
- A callback returning `410 Gone` is unsubscribed. Transient errors are retried up to five times.

See the [WebSub Recommendation](https://www.w3.org/TR/websub/) for the complete protocol.
