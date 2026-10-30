namespace WebSubHub

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Azure.Data.Tables
open Azure.Identity
open Azure.Storage.Blobs
open Azure.Storage.Queues
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting

type StorageInitializer(tables: Storage.TableStore, queues: Storage.QueueStore, blobs: Storage.BlobStore) =
    interface IHostedService with
        member _.StartAsync(_: CancellationToken) =
            task {
                do! tables.EnsureTables()
                do! queues.EnsureQueues()
                do! blobs.EnsureContainer()
            }
            :> Task

        member _.StopAsync(_: CancellationToken) = Task.CompletedTask

module Program =
    let private environmentValue name =
        Environment.GetEnvironmentVariable(name) |> Option.ofObj

    let private serviceClient<'T>
        (connectionString: string option)
        (serviceUriSetting: string)
        (createFromConnection: string -> 'T)
        (createFromUri: Uri -> DefaultAzureCredential -> 'T)
        =
        match connectionString with
        | Some value when not (String.IsNullOrWhiteSpace value) ->
            createFromConnection value
        | _ ->
            let uri =
                environmentValue ("AzureWebJobsStorage__" + serviceUriSetting)
                |> Option.defaultWith (fun () ->
                    invalidOp $"Missing storage setting AzureWebJobsStorage__{serviceUriSetting}.")
                |> Uri

            createFromUri uri (DefaultAzureCredential())

    [<EntryPoint>]
    let main _ =
        let config = Config.load ()
        let storageConnection = environmentValue "AzureWebJobsStorage"

        HostBuilder()
            .ConfigureFunctionsWorkerDefaults()
            .ConfigureServices(fun services ->
                services.AddSingleton<HubConfig>(config) |> ignore

                services.AddSingleton<TableServiceClient>(fun _ ->
                    let credentialOptions =
                        DefaultAzureCredentialOptions(
                            ManagedIdentityClientId =
                                (environmentValue "AzureWebJobsStorage__clientId"
                                 |> Option.defaultValue null)
                        )

                    serviceClient
                        storageConnection
                        "tableServiceUri"
                        TableServiceClient
                        (fun uri _ -> TableServiceClient(uri, DefaultAzureCredential(credentialOptions))))
                |> ignore

                services.AddSingleton<QueueServiceClient>(fun _ ->
                    let credentialOptions =
                        DefaultAzureCredentialOptions(
                            ManagedIdentityClientId =
                                (environmentValue "AzureWebJobsStorage__clientId"
                                 |> Option.defaultValue null)
                        )

                    serviceClient
                        storageConnection
                        "queueServiceUri"
                        QueueServiceClient
                        (fun uri _ -> QueueServiceClient(uri, DefaultAzureCredential(credentialOptions))))
                |> ignore

                services.AddSingleton<BlobServiceClient>(fun _ ->
                    let credentialOptions =
                        DefaultAzureCredentialOptions(
                            ManagedIdentityClientId =
                                (environmentValue "AzureWebJobsStorage__clientId"
                                 |> Option.defaultValue null)
                        )

                    serviceClient
                        storageConnection
                        "blobServiceUri"
                        BlobServiceClient
                        (fun uri _ -> BlobServiceClient(uri, DefaultAzureCredential(credentialOptions))))
                |> ignore

                services.AddSingleton<Storage.TableStore>() |> ignore
                services.AddSingleton<Storage.QueueStore>() |> ignore
                services.AddSingleton<Storage.BlobStore>() |> ignore
                services.AddSingleton<StorageInitializer>() |> ignore
                services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                    Func<IServiceProvider, Microsoft.Extensions.Hosting.IHostedService>(fun provider ->
                        provider.GetRequiredService<StorageInitializer>() :> Microsoft.Extensions.Hosting.IHostedService)
                )
                |> ignore

                services.AddHttpClient("websub", fun client ->
                    client.Timeout <- TimeSpan.FromSeconds(30.0))
                    .ConfigurePrimaryHttpMessageHandler(
                        Func<System.Net.Http.HttpMessageHandler>(fun () ->
                            new System.Net.Http.HttpClientHandler(AllowAutoRedirect = false))
                    )
                |> ignore

                services.AddTransient<System.Net.Http.HttpClient>(fun provider ->
                    provider.GetRequiredService<IHttpClientFactory>().CreateClient("websub"))
                |> ignore)
            .Build()
            .Run()

        0
