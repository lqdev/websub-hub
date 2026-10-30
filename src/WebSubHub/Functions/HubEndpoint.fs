namespace WebSubHub.Functions

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Azure.Functions.Worker
open Microsoft.Azure.Functions.Worker.Http
open WebSubHub

type HubEndpoint(config: HubConfig, queueStore: WebSubHub.Storage.QueueStore) =
    let response (request: HttpRequestData) status (body: string) =
        task {
            let result = request.CreateResponse(status)

            if not (String.IsNullOrEmpty body) then
                result.Headers.Add("Content-Type", "text/plain; charset=utf-8")
                do! result.WriteStringAsync(body)

            return result
        }

    let parseUri (value: string option) =
        match value with
        | Some raw ->
            match Uri.TryCreate(raw, UriKind.Absolute) with
            | true, uri when uri.Scheme = Uri.UriSchemeHttps || uri.Scheme = Uri.UriSchemeHttp ->
                if String.IsNullOrEmpty(uri.UserInfo) then Some uri else None
            | _ -> None
        | None -> None

    let headerValue (request: HttpRequestData) name =
        let mutable values = Unchecked.defaultof<IEnumerable<string>>

        if request.Headers.TryGetValues(name, &values) then
            values |> Seq.tryHead
        else
            None

    let fixedTimeEquals (left: string) (right: string) =
        let leftBytes = Encoding.UTF8.GetBytes(left)
        let rightBytes = Encoding.UTF8.GetBytes(right)

        leftBytes.Length = rightBytes.Length
        && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes)

    [<Function("HubEndpoint")>]
    member _.Run([<HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "")>] request: HttpRequestData) : Task<HttpResponseData> =
        task {
            let contentType = headerValue request "Content-Type"
            let formContentType =
                contentType
                |> Option.exists (fun value ->
                    let mediaType = value.Split([| ';' |]) |> Array.head |> fun item -> item.Trim()
                    mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))

            if not formContentType then
                return! response request HttpStatusCode.UnsupportedMediaType "Content-Type must be application/x-www-form-urlencoded"
            else
                let! body =
                    task {
                        use reader = new StreamReader(request.Body, Encoding.UTF8, true, 1024, true)
                        return! reader.ReadToEndAsync()
                    }

                if body.Length > 65536 then
                    return! response request HttpStatusCode.RequestEntityTooLarge "request body too large"
                else
                    let form = FormParsing.parse body

                    match FormParsing.first form "hub.mode" with
                    | Some mode when mode = "subscribe" || mode = "unsubscribe" ->
                        match parseUri (FormParsing.first form "hub.callback"), parseUri (FormParsing.first form "hub.topic") with
                        | Some callback, Some topic ->
                            if not (TopicPolicy.isAllowed config topic) then
                                return! response request HttpStatusCode.Forbidden "hub.reason=topic not allowed"
                            else
                                let! callbackIsSafe =
                                    UriPolicy.isSafeAsync config.AllowLocalUrls callback CancellationToken.None

                                if not callbackIsSafe then
                                    return! response request HttpStatusCode.Forbidden "hub.reason=callback URL must resolve to a public address"
                                elif FormParsing.first form "hub.verify" <> Some "async" then
                                    return! response request HttpStatusCode.BadRequest "hub.verify=async is required"
                                else
                                    let parsedLease =
                                        match FormParsing.first form "hub.lease_seconds" with
                                        | None -> Ok None
                                        | Some value ->
                                            match Int32.TryParse value with
                                            | true, parsed when parsed > 0 -> Ok(Some parsed)
                                            | _ -> Error "hub.lease_seconds must be a positive integer"

                                    let secret = FormParsing.first form "hub.secret"
                                    let secretValid =
                                        match secret with
                                        | Some value ->
                                            let length = Encoding.UTF8.GetByteCount(value)
                                            length > 0 && length <= 200
                                        | None -> true

                                    match parsedLease with
                                    | Error reason ->
                                        return! response request HttpStatusCode.BadRequest reason
                                    | Ok _ when not secretValid ->
                                        return! response request HttpStatusCode.BadRequest "hub.secret must be 1-200 bytes"
                                    | Ok requestedLease ->
                                        let verifyMessage =
                                            { Mode = mode
                                              Callback = callback.AbsoluteUri
                                              Topic = topic.AbsoluteUri
                                              LeaseSeconds = Config.clampLease config requestedLease
                                              Secret = secret }

                                        do! queueStore.Enqueue("websub-verify", verifyMessage)
                                        return! response request HttpStatusCode.Accepted ""
                        | _ ->
                            return! response request HttpStatusCode.BadRequest "hub.callback and hub.topic must be absolute HTTP(S) URLs"
                    | Some "publish" ->
                        match config.PublishToken with
                        | None ->
                            return! response request HttpStatusCode.ServiceUnavailable "publishing is not configured"
                        | Some expectedToken when
                            (headerValue request "X-WebSub-Publish-Token"
                             |> Option.exists (fixedTimeEquals expectedToken)
                             |> not) ->
                            return! response request HttpStatusCode.Unauthorized "invalid publish token"
                        | Some _ ->
                            let topics =
                                FormParsing.values form "hub.url"
                                |> List.choose (fun value -> parseUri (Some value))

                            if topics.IsEmpty then
                                return! response request HttpStatusCode.BadRequest "hub.url is required"
                            elif topics |> List.exists (TopicPolicy.isAllowed config >> not) then
                                return! response request HttpStatusCode.Forbidden "hub.reason=topic not allowed"
                            else
                                for topic in topics do
                                    do! queueStore.Enqueue("websub-fetch", { Topic = topic.AbsoluteUri })

                                return! response request HttpStatusCode.Accepted ""
                    | _ ->
                        return! response request HttpStatusCode.BadRequest "hub.mode must be subscribe, unsubscribe, or publish"
        }
