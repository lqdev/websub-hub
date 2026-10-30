namespace WebSubHub.Functions

open System.Net
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Azure.Functions.Worker
open Microsoft.Azure.Functions.Worker.Http
open WebSubHub

type Health(config: HubConfig) =
    [<Function("Health")>]
    member _.Run([<HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")>] request: HttpRequestData) : Task<HttpResponseData> =
        task {
            let response = request.CreateResponse(HttpStatusCode.OK)
            response.Headers.Add("Content-Type", "application/json; charset=utf-8")

            let body =
                JsonSerializer.Serialize(
                    {| status = "ok"
                       openMode = config.OpenMode
                       allowedTopicPrefixes = config.AllowedPrefixes.Length |}
                )

            do! response.WriteStringAsync(body)
            return response
        }
