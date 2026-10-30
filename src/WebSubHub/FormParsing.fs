namespace WebSubHub

open System
open System.Collections.Generic

module FormParsing =
    let parse (body: string) : Dictionary<string, string list> =
        let result = Dictionary<string, ResizeArray<string>>(StringComparer.Ordinal)

        for pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries) do
            let parts = pair.Split('=', 2)

            let decode (value: string) =
                value.Replace("+", " ")
                |> Uri.UnescapeDataString

            let key = decode parts[0]
            let value = if parts.Length > 1 then decode parts[1] else ""

            match result.TryGetValue key with
            | true, values -> values.Add value
            | _ ->
                let values = ResizeArray<string>()
                values.Add value
                result.Add(key, values)

        let output = Dictionary<string, string list>(StringComparer.Ordinal)

        for entry in result do
            output.Add(entry.Key, List.ofSeq entry.Value)

        output

    let values (form: IReadOnlyDictionary<string, string list>) (key: string) =
        match form.TryGetValue key with
        | true, found -> found
        | _ -> []

    let first (form: IReadOnlyDictionary<string, string list>) (key: string) =
        values form key |> List.tryHead
