namespace WebSubHub

open System

type HubMode =
    | Subscribe
    | Unsubscribe
    | Publish

type SubscriptionRequest =
    { Mode: HubMode
      Callback: Uri option
      Topic: Uri option
      LeaseSeconds: int option
      Secret: string option }

type Subscription =
    { Topic: string
      Callback: string
      Secret: string option
      ExpiresAt: DateTimeOffset
      CreatedAt: DateTimeOffset }

type TopicState =
    { Topic: string
      ContentHash: string
      ContentType: string
      ETag: string option
      LastFetched: DateTimeOffset }

type FetchedTopic =
    { Topic: Uri
      Body: byte array
      ContentType: string
      ETag: string option }

type VerifyMessage =
    { Mode: string
      Callback: string
      Topic: string
      LeaseSeconds: int
      Secret: string option }

type FetchMessage = { Topic: string }

type DeliverMessage =
    { Topic: string
      Callback: string
      ContentHash: string
      ContentType: string
      Attempt: int }
