namespace WebSubHub

open System

module TopicPolicy =
    let private normalizedAuthority (uri: Uri) =
        uri.GetLeftPart(UriPartial.Authority).TrimEnd('/').ToLowerInvariant()

    let isAllowed (cfg: HubConfig) (topic: Uri) : bool =
        let isHttp = topic.Scheme = Uri.UriSchemeHttps || topic.Scheme = Uri.UriSchemeHttp

        if not isHttp || not (String.IsNullOrEmpty(topic.UserInfo)) then
            false
        elif cfg.OpenMode then
            true
        else
            cfg.AllowedPrefixes
            |> List.exists (fun prefix ->
                match Uri.TryCreate(prefix, UriKind.Absolute) with
                | true, allowed when not (isNull allowed) ->
                    let allowedPath = allowed.AbsolutePath.TrimEnd('/') + "/"
                    let sameAuthority =
                        String.Equals(
                            normalizedAuthority topic,
                            normalizedAuthority allowed,
                            StringComparison.OrdinalIgnoreCase
                        )

                    let pathMatches =
                        topic.AbsolutePath.StartsWith(allowedPath, StringComparison.Ordinal)
                        || String.Equals(topic.AbsolutePath, allowed.AbsolutePath, StringComparison.Ordinal)

                    sameAuthority && pathMatches
                | _ -> false)
