namespace WebSubHub

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Threading.Tasks

type DeliveryResult =
    | Delivered
    | Gone
    | Retry
    | PermanentFailure of HttpStatusCode

module Delivery =
    let private sendSafe
        (client: HttpClient)
        (hubUrl: Uri)
        (subscription: Subscription)
        (topic: string)
        (contentType: string)
        (body: byte array)
        : Task<DeliveryResult> =
        task {
            use request = new HttpRequestMessage(HttpMethod.Post, subscription.Callback)
            let content = new ByteArrayContent(body)
            request.Content <- content

            let parsedContentType =
                match MediaTypeHeaderValue.TryParse(contentType) with
                | true, value -> value
                | _ -> MediaTypeHeaderValue("application/octet-stream")

            content.Headers.ContentType <- parsedContentType
            request.Headers.UserAgent.ParseAdd("websub-hub/1.0")
            request.Headers.TryAddWithoutValidation(
                "Link",
                $"<{hubUrl.AbsoluteUri}>; rel=\"hub\", <{topic}>; rel=\"self\""
            )
            |> ignore

            subscription.Secret
            |> Option.iter (fun secret ->
                request.Headers.TryAddWithoutValidation("X-Hub-Signature", Signer.signSha256 secret body)
                |> ignore)

            use! response = client.SendAsync(request)

            if response.IsSuccessStatusCode then
                return Delivered
            elif response.StatusCode = HttpStatusCode.Gone then
                return Gone
            elif
                int response.StatusCode >= 500
                || response.StatusCode = HttpStatusCode.RequestTimeout
                || int response.StatusCode = 429
            then
                return Retry
            else
                return PermanentFailure response.StatusCode
        }

    let send
        (client: HttpClient)
        (hubUrl: Uri)
        (allowLocal: bool)
        (subscription: Subscription)
        (topic: string)
        (contentType: string)
        (body: byte array)
        : Task<DeliveryResult> =
        task {
            let! safe =
                UriPolicy.isSafeAsync allowLocal (Uri(subscription.Callback)) Threading.CancellationToken.None

            if not safe then
                return PermanentFailure HttpStatusCode.Forbidden
            else
                return! sendSafe client hubUrl subscription topic contentType body
        }
