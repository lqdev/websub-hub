namespace WebSubHub

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Threading
open System.Threading.Tasks

module Fetcher =
    let private readLimited (stream: Stream) (limit: int64) (ct: CancellationToken) =
        task {
            use output = new MemoryStream()
            let buffer = Array.zeroCreate<byte> 32768
            let mutable total = 0L
            let mutable read = 1

            while read > 0 do
                let! count = stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                read <- count
                total <- total + int64 count

                if total > limit then
                    raise (InvalidDataException($"Topic payload exceeds configured size limit ({limit} bytes)."))

                if count > 0 then
                    do! output.WriteAsync(buffer.AsMemory(0, count), ct)

            return output.ToArray()
        }

    let fetch
        (client: HttpClient)
        (maxBytes: int64)
        (allowLocal: bool)
        (topic: Uri)
        (etag: string option)
        (ct: CancellationToken)
        : Task<FetchedTopic option> =
        task {
            let rec send (current: Uri) (validator: string option) (redirects: int) =
                task {
                    let! safe = UriPolicy.isSafeAsync allowLocal current ct

                    if not safe then
                        raise (InvalidOperationException($"Refusing to fetch non-public topic URL: {current}"))

                    use request = new HttpRequestMessage(HttpMethod.Get, current)
                    request.Headers.UserAgent.ParseAdd("websub-hub/1.0")
                    request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("*/*"))

                    validator
                    |> Option.iter (fun value ->
                        match EntityTagHeaderValue.TryParse(value) with
                        | true, parsed -> request.Headers.IfNoneMatch.Add(parsed)
                        | _ -> ())

                    let! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    let statusCode = int response.StatusCode
                    let isRedirect = statusCode = 301 || statusCode = 302 || statusCode = 303 || statusCode = 307 || statusCode = 308

                    if isRedirect then
                        if redirects >= 5 || isNull response.Headers.Location then
                            response.Dispose()
                            return raise (HttpRequestException("Topic redirect limit reached or redirect target missing."))
                        else
                            let next = Uri(current, response.Headers.Location)
                            response.Dispose()

                            if current.Scheme = Uri.UriSchemeHttps && next.Scheme <> Uri.UriSchemeHttps then
                                return raise (InvalidOperationException("Refusing HTTPS-to-HTTP topic redirect."))
                            else
                                return! send next None (redirects + 1)
                    else
                        return response
                }

            use! response = send topic etag 0

            if response.StatusCode = HttpStatusCode.NotModified then
                return None
            else
                response.EnsureSuccessStatusCode() |> ignore

                let contentLength = response.Content.Headers.ContentLength

                if contentLength.HasValue && contentLength.Value > maxBytes then
                    raise (InvalidDataException($"Topic payload exceeds configured size limit ({maxBytes} bytes)."))

                use! stream = response.Content.ReadAsStreamAsync(ct)
                let! body = readLimited stream maxBytes ct

                let contentType =
                    match response.Content.Headers.ContentType with
                    | null -> "application/octet-stream"
                    | value -> value.ToString()

                let responseEtag =
                    if isNull response.Headers.ETag then None
                    else Some(response.Headers.ETag.ToString())

                return
                    Some
                        { Topic = topic
                          Body = body
                          ContentType = contentType
                          ETag = responseEtag }
        }
