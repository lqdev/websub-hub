namespace WebSubHub.Functions

open System
open System.Net
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Azure.Functions.Worker
open Microsoft.Extensions.Logging
open WebSubHub

type DeliverQueue
    (
        config: HubConfig,
        tables: WebSubHub.Storage.TableStore,
        queues: WebSubHub.Storage.QueueStore,
        blobs: WebSubHub.Storage.BlobStore,
        client: System.Net.Http.HttpClient,
        logger: ILogger<DeliverQueue>
    ) =

    [<Function("DeliverQueue")>]
    member _.Run([<QueueTrigger("websub-deliver", Connection = "AzureWebJobsStorage")>] message: string) : Task =
        task {
            let delivery = JsonSerializer.Deserialize<DeliverMessage>(message)

            if isNull (box delivery) then
                invalidOp "Queue message did not contain a delivery request."

            let! subscriptions = tables.ListSubscriptions(delivery.Topic)

            match subscriptions |> List.tryFind (fun sub -> sub.Callback = delivery.Callback) with
            | None -> ()
            | Some subscription ->
                let! body = blobs.Get(delivery.ContentHash)

                let! result =
                    Delivery.send
                        client
                        config.HubUrl
                        config.AllowLocalUrls
                        subscription
                        delivery.Topic
                        delivery.ContentType
                        body

                match result with
                | Delivered ->
                    logger.LogInformation("Delivered {Topic} to subscriber", delivery.Topic)
                | Gone ->
                    do! tables.DeleteSubscription(delivery.Topic, delivery.Callback)
                    logger.LogInformation("Removed gone subscriber for {Topic}", delivery.Topic)
                | PermanentFailure status ->
                    logger.LogWarning("Subscriber returned permanent status {Status} for {Topic}", status, delivery.Topic)
                | Retry when delivery.Attempt < 5 ->
                    let delay = TimeSpan.FromSeconds(Math.Pow(2.0, float delivery.Attempt) * 15.0)

                    do!
                        queues.Enqueue(
                            "websub-deliver",
                            { delivery with Attempt = delivery.Attempt + 1 },
                            delay = delay
                        )
                | Retry ->
                    logger.LogError(
                        "Delivery failed after {Attempts} attempts for {Topic}",
                        delivery.Attempt,
                        delivery.Topic
                    )
        }
