namespace WebSubHub.Functions

open System
open System.Threading.Tasks
open Microsoft.Azure.Functions.Worker
open Microsoft.Extensions.Logging
open WebSubHub

type LeaseExpiry(tables: WebSubHub.Storage.TableStore, blobs: WebSubHub.Storage.BlobStore, logger: ILogger<LeaseExpiry>) =
    [<Function("LeaseExpiry")>]
    member _.Run([<TimerTrigger("0 0 * * * *")>] timerInfo: obj) : Task =
        task {
            let! expired = tables.ListExpired(DateTimeOffset.UtcNow)

            for subscription in expired do
                do! tables.DeleteSubscription(subscription.Topic, subscription.Callback)

            do! blobs.DeleteOlderThan(DateTimeOffset.UtcNow.AddDays(-7.0))

            if not expired.IsEmpty then
                logger.LogInformation("Removed {Count} expired WebSub subscriptions", expired.Length)
        }
