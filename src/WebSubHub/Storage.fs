namespace WebSubHub

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open System.Collections.Generic
open Azure
open Azure.Data.Tables
open Azure.Storage.Blobs
open Azure.Storage.Queues
open Azure.Storage.Queues.Models

module Storage =
    let private readAllAsync<'T> (items: IAsyncEnumerable<'T>) =
        task {
            let values = ResizeArray<'T>()
            let enumerator = items.GetAsyncEnumerator()
            let mutable hasNext = true

            while hasNext do
                let! hasValue = enumerator.MoveNextAsync().AsTask()

                if hasValue then
                    values.Add enumerator.Current
                else
                    hasNext <- false

            do! enumerator.DisposeAsync().AsTask()
            return List.ofSeq values
        }

    let hash (value: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes(value))
        |> Convert.ToHexString
        |> fun hex -> hex.ToLowerInvariant()

    let private tryString (entity: TableEntity) (key: string) =
        match entity.TryGetValue key with
        | true, (:? string as value) -> Some value
        | _ -> None

    let private tryDate (entity: TableEntity) (key: string) =
        match entity.TryGetValue key with
        | true, (:? DateTimeOffset as value) -> Some value
        | true, (:? DateTime as value) -> Some(DateTimeOffset(value, TimeSpan.Zero))
        | _ -> None

    let private subscriptionFromEntity (entity: TableEntity) =
        match tryString entity "Topic", tryString entity "Callback", tryDate entity "ExpiresAt", tryDate entity "CreatedAt" with
        | Some topic, Some callback, Some expiresAt, Some createdAt ->
            Some
                { Topic = topic
                  Callback = callback
                  Secret = tryString entity "Secret"
                  ExpiresAt = expiresAt
                  CreatedAt = createdAt }
        | _ -> None

    type TableStore(service: TableServiceClient) =
        let subscriptions = service.GetTableClient("subscriptions")
        let topics = service.GetTableClient("topics")

        member _.EnsureTables() =
            task {
                let! _ = subscriptions.CreateIfNotExistsAsync()
                let! _ = topics.CreateIfNotExistsAsync()
                return ()
            }

        member _.UpsertSubscription(subscription: Subscription) =
            task {
                let entity = TableEntity(hash subscription.Topic, hash subscription.Callback)
                entity["Topic"] <- subscription.Topic
                entity["Callback"] <- subscription.Callback
                entity["ExpiresAt"] <- subscription.ExpiresAt
                entity["CreatedAt"] <- subscription.CreatedAt

                match subscription.Secret with
                | Some secret -> entity["Secret"] <- secret
                | None -> ()

                let! _ = subscriptions.UpsertEntityAsync(entity, TableUpdateMode.Replace)
                return ()
            }

        member _.DeleteSubscription(topic: string, callback: string) =
            task {
                let! existing = subscriptions.GetEntityIfExistsAsync<TableEntity>(hash topic, hash callback)

                if existing.HasValue then
                    let! _ = subscriptions.DeleteEntityAsync(hash topic, hash callback, ETag.All)
                    ()

                return ()
            }

        member _.ListSubscriptions(topic: string) =
            task {
                let filter = $"PartitionKey eq '{hash topic}'"
                let! entities = subscriptions.QueryAsync<TableEntity>(filter = filter) |> readAllAsync

                let now = DateTimeOffset.UtcNow

                return
                    entities
                    |> Seq.choose subscriptionFromEntity
                    |> Seq.filter (fun subscription -> subscription.ExpiresAt > now)
                    |> List.ofSeq
            }

        member _.ListExpired(now: DateTimeOffset) =
            task {
                let! entities = subscriptions.QueryAsync<TableEntity>() |> readAllAsync

                return
                    entities
                    |> Seq.choose subscriptionFromEntity
                    |> Seq.filter (fun subscription -> subscription.ExpiresAt <= now)
                    |> List.ofSeq
            }

        member _.GetTopic(topic: string) =
            task {
                let! result = topics.GetEntityIfExistsAsync<TableEntity>("topic", hash topic)

                if not result.HasValue then
                    return None
                else
                    let entity = result.Value

                    match tryString entity "ContentHash", tryString entity "ContentType", tryDate entity "LastFetched" with
                    | Some contentHash, Some contentType, Some lastFetched ->
                        return
                            Some
                                { Topic = topic
                                  ContentHash = contentHash
                                  ContentType = contentType
                                  ETag = tryString entity "ETag"
                                  LastFetched = lastFetched }
                    | _ ->
                        return None
            }

        member _.UpsertTopic(topic: TopicState) =
            task {
                let entity = TableEntity("topic", hash topic.Topic)
                entity["Topic"] <- topic.Topic
                entity["ContentHash"] <- topic.ContentHash
                entity["ContentType"] <- topic.ContentType
                entity["LastFetched"] <- topic.LastFetched

                match topic.ETag with
                | Some etag -> entity["ETag"] <- etag
                | None -> ()

                let! _ = topics.UpsertEntityAsync(entity, TableUpdateMode.Replace)
                return ()
            }

    type QueueStore(service: QueueServiceClient) =
        member _.EnsureQueues() =
            task {
                for name in [ "websub-verify"; "websub-fetch"; "websub-deliver" ] do
                    let queue = service.GetQueueClient(name)
                    let! _ = queue.CreateIfNotExistsAsync()
                    ()
            }

        member _.Enqueue<'T>(queueName: string, message: 'T, ?delay: TimeSpan) =
            task {
                let queue = service.GetQueueClient(queueName)
                let! _ = queue.CreateIfNotExistsAsync()
                let text =
                    JsonSerializer.Serialize(message)
                    |> Encoding.UTF8.GetBytes
                    |> Convert.ToBase64String

                let visibility =
                    delay
                    |> Option.map Nullable
                    |> Option.defaultValue (Nullable())

                let! _ = queue.SendMessageAsync(text, visibility, Nullable())
                return ()
            }

    type BlobStore(service: BlobServiceClient) =
        let container = service.GetBlobContainerClient("websub-payloads")

        member _.EnsureContainer() =
            task {
                let! _ = container.CreateIfNotExistsAsync()
                return ()
            }

        member _.Put(contentHash: string, body: byte array) =
            task {
                let blob = container.GetBlobClient(contentHash)
                let! _ = blob.UploadAsync(BinaryData(body), true)
                return ()
            }

        member _.Get(contentHash: string) =
            task {
                let blob = container.GetBlobClient(contentHash)
                let! result = blob.DownloadContentAsync()
                return result.Value.Content.ToArray()
            }

        member _.Delete(contentHash: string) =
            task {
                let blob = container.GetBlobClient(contentHash)
                let! _ = blob.DeleteIfExistsAsync()
                return ()
            }

        member _.DeleteOlderThan(cutoff: DateTimeOffset) =
            task {
                let! items = container.GetBlobsAsync() |> readAllAsync

                for item in items do
                    match item.Properties.LastModified with
                    | lastModified when lastModified.HasValue && lastModified.Value < cutoff ->
                        let blob = container.GetBlobClient(item.Name)
                        let! _ = blob.DeleteIfExistsAsync()
                        ()
                    | _ -> ()
            }
