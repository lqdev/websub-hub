open System
open System.IO
open System.Net
open System.Text

let listener = new HttpListener()
listener.Prefixes.Add("http://localhost:8099/")
listener.Start()

let topicPath = Path.Combine(__SOURCE_DIRECTORY__, "topic.opml")

printfn "Local OPML topic and callback listening on http://localhost:8099/"
printfn "Press Ctrl+C to stop."

while true do
    let context = listener.GetContext()
    let request = context.Request
    let response = context.Response

    if request.HttpMethod = "GET" && request.Url.AbsolutePath = "/callback" then
        let challenge = request.QueryString["hub.challenge"]
        response.StatusCode <- if isNull challenge then 400 else 200
        response.ContentType <- "text/plain; charset=utf-8"

        if not (isNull challenge) then
            let bytes = Encoding.UTF8.GetBytes(challenge)
            response.OutputStream.Write(bytes, 0, bytes.Length)

        response.Close()
        printfn "Verification GET acknowledged."
    elif request.HttpMethod = "GET" && request.Url.AbsolutePath = "/topic.opml" then
        let bytes = File.ReadAllBytes(topicPath)
        response.StatusCode <- 200
        response.ContentType <- "text/x-opml; charset=utf-8"
        response.ContentLength64 <- int64 bytes.Length
        response.OutputStream.Write(bytes, 0, bytes.Length)
        response.Close()
        printfn "Served local OPML topic (%d bytes)." bytes.Length
    elif request.HttpMethod = "POST" && request.Url.AbsolutePath = "/callback" then
        use memory = new MemoryStream()
        request.InputStream.CopyTo(memory)
        response.StatusCode <- 204
        response.Close()

        printfn "Delivery received: content-type=%s bytes=%d signature-present=%b"
            request.ContentType
            memory.Length
            (request.Headers["X-Hub-Signature"] |> isNull |> not)
    else
        response.StatusCode <- 404
        response.Close()
