namespace WebSubHub.Functions

open System
open System.Net.Http
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Azure.Functions.Worker
open Microsoft.Extensions.Logging
open WebSubHub

type FetchQueue
    (
        config: HubConfig,
        tables: WebSubHub.Storage.TableStore,
        queues: WebSubHub.Storage.QueueStore,
        blobs: WebSubHub.Storage.BlobStore,
        client: HttpClient,
        logger: ILogger<FetchQueue>
    ) =

    [<Function("FetchQueue")>]
    member _.Run([<QueueTrigger("websub-fetch", Connection = "AzureWebJobsStorage")>] message: string) : Task =
        task {
            let fetchMessage = JsonSerializer.Deserialize<FetchMessage>(message)

            if isNull (box fetchMessage) then
                invalidOp "Queue message did not contain a topic URL."

            let topic = Uri(fetchMessage.Topic)

            if not (TopicPolicy.isAllowed config topic) then
                logger.LogWarning("Refusing to fetch topic no longer allowed by policy: {Topic}", topic)
            else
                let! previous = tables.GetTopic(topic.AbsoluteUri)
                let! fetched =
                    Fetcher.fetch
                        client
                        config.MaxTopicBytes
                        config.AllowLocalUrls
                        topic
                        (previous |> Option.bind (fun state -> state.ETag))
                        CancellationToken.None

                match fetched with
                | None ->
                    match previous with
                    | Some state ->
                        do!
                            tables.UpsertTopic(
                                { state with LastFetched = DateTimeOffset.UtcNow }
                            )
                    | None -> ()
                | Some content ->
                    let hash =
                        SHA256.HashData(content.Body)
                        |> Convert.ToHexString
                        |> fun value -> value.ToLowerInvariant()

                    do! blobs.Put(hash, content.Body)

                    let state =
                        { Topic = topic.AbsoluteUri
                          ContentHash = hash
                          ContentType = content.ContentType
                          ETag = content.ETag
                          LastFetched = DateTimeOffset.UtcNow }

                    do! tables.UpsertTopic(state)

                    let changed =
                        previous
                        |> Option.forall (fun old -> old.ContentHash <> hash || old.ContentType <> content.ContentType)

                    if changed then
                        let! subscriptions = tables.ListSubscriptions(topic.AbsoluteUri)

                        for subscription in subscriptions do
                            do!
                                queues.Enqueue(
                                    "websub-deliver",
                                    { Topic = topic.AbsoluteUri
                                      Callback = subscription.Callback
                                      ContentHash = hash
                                      ContentType = content.ContentType
                                      Attempt = 1 }
                                )

                        logger.LogInformation(
                            "Topic changed; queued {SubscriberCount} deliveries for {Topic}",
                            subscriptions.Length,
                            topic
                        )
        }
