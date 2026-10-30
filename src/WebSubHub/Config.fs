namespace WebSubHub

open System

type HubConfig =
    { HubUrl: Uri
      AllowedPrefixes: string list
      OpenMode: bool
      PublishToken: string option
      AllowLocalUrls: bool
      DefaultLease: int
      MinLease: int
      MaxLease: int
      MaxTopicBytes: int64 }

module Config =
    let private env (name: string) =
        Environment.GetEnvironmentVariable(name) |> Option.ofObj

    let private envInt (name: string) (fallback: int) =
        env name
        |> Option.bind (fun value ->
            match Int32.TryParse(value) with
            | true, parsed -> Some parsed
            | _ -> None)
        |> Option.defaultValue fallback

    let load () : HubConfig =
        let hubUrl =
            env "WEBSUB_HUB_URL"
            |> Option.defaultValue "http://localhost:7071/"
            |> Uri

        let prefixes =
            env "WEBSUB_ALLOWED_TOPIC_PREFIXES"
            |> Option.defaultValue ""
            |> fun value ->
                value.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
                |> Array.toList

        let openMode =
            env "WEBSUB_OPEN_MODE"
            |> Option.map (fun value -> value.Equals("true", StringComparison.OrdinalIgnoreCase))
            |> Option.defaultValue false

        let publishToken =
            env "WEBSUB_PUBLISH_TOKEN"
            |> Option.filter (String.IsNullOrWhiteSpace >> not)

        let allowLocalUrls =
            env "WEBSUB_ALLOW_LOCAL_URLS"
            |> Option.map (fun value -> value.Equals("true", StringComparison.OrdinalIgnoreCase))
            |> Option.defaultValue false

        { HubUrl = hubUrl
          AllowedPrefixes = prefixes
          OpenMode = openMode
          PublishToken = publishToken
          AllowLocalUrls = allowLocalUrls
          DefaultLease = envInt "WEBSUB_DEFAULT_LEASE_SECONDS" 864000
          MinLease = envInt "WEBSUB_MIN_LEASE_SECONDS" 3600
          MaxLease = envInt "WEBSUB_MAX_LEASE_SECONDS" 2592000
          MaxTopicBytes = int64 (envInt "WEBSUB_MAX_TOPIC_BYTES" (5 * 1024 * 1024)) }

    let clampLease (cfg: HubConfig) (requested: int option) =
        requested
        |> Option.defaultValue cfg.DefaultLease
        |> max cfg.MinLease
        |> min cfg.MaxLease
