open System
open System.Linq
open System.Collections.Generic
open System.Collections.Concurrent
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

open HotChocolate
open HotChocolate.Execution.Configuration
open HotChocolate.Types
open HotChocolate.Types.Descriptors
open HotChocolate.Types.Descriptors.Definitions
open HotChocolate.Data
open HotChocolate.Data.Filters
open HotChocolate.Data.Filters.Expressions

[<Struct>]
type Updated = Updated


type Cat = { Name : string }


type CatFilterConvention() =
  inherit FilterConvention()

  interface ITypeSystemMember

  override _.Configure(describe) =
    describe.AddDefaults() |> ignore


type CatProvider(signal : ChannelReader<Updated>) as me =
  let typesChanged = Event<EventHandler<EventArgs>, EventArgs>()

  let cats = ConcurrentQueue<Cat>()

  let checkForRefresh () : Task = backgroundTask {
    while true do
      let! ready = signal.WaitToReadAsync()
      if ready then
        let! _ = signal.ReadAsync()
        cats.Enqueue({ Name = $"cat%03i{Random.Shared.Next()}" })
        typesChanged.Trigger(me, EventArgs())
  }

  do Task.Run(Func<Task> checkForRefresh) |> ignore

  let makeCatObject () =
    let def = ObjectTypeDefinition("Cat")
    def.Fields.Add(
      ObjectFieldDefinition(
        name = "name",
        ``type`` = TypeReference.Parse("String!"),
        pureResolver = (fun c -> c.Parent<Cat>().Name)
      )
    )
    def

  let makeCatsField context =
    let def =
      ObjectFieldDefinition(
        name = "cats",
        ``type`` = TypeReference.Parse("[Cat]"),
        resolver = fun _ -> ValueTask.FromResult(cats)
      )
    def
      .ToDescriptor(context)
      .UseFiltering<CatFilterConvention>()
      .ToDefinition()

  interface ITypeModule with
    member _.CreateTypesAsync(context, _) =
      backgroundTask {
        let types = ResizeArray<ITypeSystemMember>()

        let filterConvention = CatFilterConvention()
        types.Add(filterConvention)

        let catTypeDefintion = makeCatObject()
        types.Add(ObjectType.CreateUnsafe(catTypeDefintion))

        let catsDefinition = makeCatsField context
        let typeDefinition = ObjectTypeDefinition("Query")
        typeDefinition.Fields.Add(catsDefinition)
        types.Add(ObjectTypeExtension.CreateUnsafe(typeDefinition))

        return types :> IReadOnlyCollection<_>
      }
      |> ValueTask<IReadOnlyCollection<_>>

    [<CLIEvent>]
    member _.TypesChanged = typesChanged.Publish


type Query() =
  member _.Version = 1


type UpdateService(logger : ILoggerFactory, signal : ChannelWriter<Updated>) =
  inherit BackgroundService()

  let log = logger.CreateLogger<UpdateService>()

  override _.ExecuteAsync(stoppingToken) = backgroundTask {
    while not stoppingToken.IsCancellationRequested do
      log.LogDebug("Going to sleep.")
      do! Task.Delay(10000)
      log.LogDebug("Signaling...")
      do! signal.WriteAsync(Updated)
  }


[<EntryPoint>]
let main args =
  let builder = WebApplication.CreateBuilder(args)
  // ⮟⮟⮟ Channel ⮟⮟⮟
  builder.Services
    .AddSingleton<Channel<Updated>>(fun _ ->
      let options =
        BoundedChannelOptions(
          capacity = 1,
          FullMode = BoundedChannelFullMode.DropOldest,
          SingleReader = true,
          SingleWriter = false
        )
      Channel.CreateBounded<Updated>(options)
    )
    .AddSingleton<ChannelReader<Updated>>(fun svc ->
      svc.GetRequiredService<Channel<Updated>>().Reader
    )
    .AddSingleton<ChannelWriter<Updated>>(fun svc ->
      svc.GetRequiredService<Channel<Updated>>().Writer
    )
  |> ignore

  // ⮟⮟⮟ Graph ⮟⮟⮟
  builder.Services
    .AddSingleton<CatProvider>()
    .AddGraphQLServer()
    .AddFiltering<CatFilterConvention>()
    .AddQueryType<Query>()
    .AddTypeModule<CatProvider>()
    .InitializeOnStartup()
  |> ignore

  // ⮟⮟⮟ Hosted services ⮟⮟⮟
  builder.Services.AddHostedService<UpdateService>() |> ignore

  // ⮟⮟⮟ Application ⮟⮟⮟
  let app = builder.Build()
  app.MapGraphQL() |> ignore
  app.Run()

  0 // Exit code
