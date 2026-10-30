namespace WebSubHub.Functions

open System
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Azure.Functions.Worker
open Microsoft.Extensions.Logging
open WebSubHub

type VerifyQueue(config: HubConfig, tables: WebSubHub.Storage.TableStore, client: HttpClient, logger: ILogger<VerifyQueue>) =
    [<Function("VerifyQueue")>]
    member _.Run([<QueueTrigger("websub-verify", Connection = "AzureWebJobsStorage")>] message: string) : Task =
        task {
            let request = JsonSerializer.Deserialize<VerifyMessage>(message)

            if isNull (box request) then
                invalidOp "Queue message did not contain a verification request."

            let! verified = Verification.verify client config.AllowLocalUrls request

            if not verified then
                logger.LogWarning("WebSub intent verification failed for topic {Topic}", request.Topic)
                let! notified = Verification.notifyDenied client config.AllowLocalUrls request

                if not notified then
                    logger.LogWarning("Could not deliver subscription denial for {Topic}", request.Topic)
            elif request.Mode = "subscribe" then
                let now = DateTimeOffset.UtcNow

                do!
                    tables.UpsertSubscription(
                        { Topic = request.Topic
                          Callback = request.Callback
                          Secret = request.Secret
                          ExpiresAt = now.AddSeconds(float request.LeaseSeconds)
                          CreatedAt = now }
                    )

                logger.LogInformation("Stored WebSub subscription for {Topic}", request.Topic)
            elif request.Mode = "unsubscribe" then
                do! tables.DeleteSubscription(request.Topic, request.Callback)
                logger.LogInformation("Removed WebSub subscription for {Topic}", request.Topic)
        }
