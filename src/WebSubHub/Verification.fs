namespace WebSubHub

open System
open System.Collections.Generic
open System.Net.Http
open System.Net.Http.Headers
open System.Threading
open System.Threading.Tasks

module Verification =
    let makeVerificationUri (callback: Uri) (message: VerifyMessage) (challenge: string) =
        let parameters =
            [ "hub.mode", message.Mode
              "hub.topic", message.Topic
              "hub.challenge", challenge ]
            @ (if message.Mode = "subscribe" then
                   [ "hub.lease_seconds", string message.LeaseSeconds ]
               else
                   [])

        let query =
            parameters
            |> List.map (fun (key, value) ->
                $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}")
            |> String.concat "&"

        let builder = UriBuilder(callback)
        builder.Query <-
            if String.IsNullOrEmpty(builder.Query) then query
            else builder.Query.TrimStart('?') + "&" + query

        builder.Uri

    let verify (client: HttpClient) (allowLocal: bool) (message: VerifyMessage) : Task<bool> =
        task {
            let challenge = Guid.NewGuid().ToString("N")
            let callback = Uri(message.Callback)

            let! safe = UriPolicy.isSafeAsync allowLocal callback CancellationToken.None

            if not safe then
                return false
            else
                let verificationUri = makeVerificationUri callback message challenge
                use! response = client.GetAsync(verificationUri)

                if not response.IsSuccessStatusCode then
                    return false
                else
                    let! body = response.Content.ReadAsStringAsync()
                    return String.Equals(body, challenge, StringComparison.Ordinal)
        }

    let notifyDenied (client: HttpClient) (allowLocal: bool) (message: VerifyMessage) =
        task {
            let callback = Uri(message.Callback)
            let! safe = UriPolicy.isSafeAsync allowLocal callback CancellationToken.None

            if not safe then
                return false
            else
                use content =
                    new FormUrlEncodedContent(
                        [ KeyValuePair("hub.mode", "denied")
                          KeyValuePair("hub.topic", message.Topic)
                          KeyValuePair("hub.reason", "intent verification failed") ]
                    )

                use! response = client.PostAsync(callback, content)
                return response.IsSuccessStatusCode
        }
