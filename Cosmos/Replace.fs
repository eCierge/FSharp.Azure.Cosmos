﻿[<AutoOpen>]
module FSharp.Azure.Cosmos.Replace

open Microsoft.Azure.Cosmos

[<Struct>]
type ReplaceOperation<'a> = {
    Item : 'a
    Id : string
    PartitionKey : PartitionKey voption
    RequestOptions : ItemRequestOptions
}

[<Struct>]
type ReplaceConcurrentlyOperation<'a, 'e> = {
    Id : string
    PartitionKey : PartitionKey voption
    RequestOptions : ItemRequestOptions
    Update : 'a -> Async<Result<'a, 'e>>
}

open System

type ReplaceBuilder<'a> (enableContentResponseOnWrite : bool) =
    member _.Yield _ =
        {
            Item = Unchecked.defaultof<_>
            Id = String.Empty
            PartitionKey = ValueNone
            RequestOptions = ItemRequestOptions (EnableContentResponseOnWrite = enableContentResponseOnWrite)
        }
        : ReplaceOperation<'a>

    /// Sets the item being to replace existing with
    [<CustomOperation "item">]
    member _.Item (state : ReplaceOperation<_>, item) = { state with Item = item }

    /// Sets the item being to replace existing with
    [<CustomOperation "id">]
    member _.Id (state : ReplaceOperation<_>, id) = { state with Id = id }

    /// Sets the partition key
    [<CustomOperation "partitionKey">]
    member _.PartitionKey (state : ReplaceOperation<_>, partitionKey : PartitionKey) = {
        state with
            PartitionKey = ValueSome partitionKey
    }

    /// Sets the partition key
    [<CustomOperation "partitionKey">]
    member _.PartitionKey (state : ReplaceOperation<_>, partitionKey : string) = {
        state with
            PartitionKey = ValueSome (PartitionKey partitionKey)
    }

    /// Sets the request options
    [<CustomOperation "requestOptions">]
    member _.RequestOptions (state : ReplaceOperation<_>, options : ItemRequestOptions) = {
        state with
            RequestOptions = options
    }

    /// Sets the eTag to <see href="IfMatchEtag">IfMatchEtag</see>
    [<CustomOperation "eTag">]
    member _.ETag (state : ReplaceOperation<_>, eTag : string) =
        state.RequestOptions.IfMatchEtag <- eTag
        state

type ReplaceConcurrentlyBuilder<'a, 'e> (enableContentResponseOnWrite : bool) =
    member _.Yield _ =
        {
            Id = String.Empty
            PartitionKey = ValueNone
            RequestOptions = ItemRequestOptions (EnableContentResponseOnWrite = enableContentResponseOnWrite)
            Update =
                fun _ ->
                    raise
                    <| MissingMethodException ("Update function is not set for concurrent replace operation")
        }
        : ReplaceConcurrentlyOperation<'a, 'e>

    /// Sets the item being to replace existing with
    [<CustomOperation "id">]
    member _.Id (state : ReplaceConcurrentlyOperation<_, _>, id) = { state with Id = id }

    /// Sets the partition key
    [<CustomOperation "partitionKey">]
    member _.PartitionKey (state : ReplaceConcurrentlyOperation<_, _>, partitionKey : PartitionKey) = {
        state with
            PartitionKey = ValueSome partitionKey
    }

    /// Sets the partition key
    [<CustomOperation "partitionKey">]
    member _.PartitionKey (state : ReplaceConcurrentlyOperation<_, _>, partitionKey : string) = {
        state with
            PartitionKey = ValueSome (PartitionKey partitionKey)
    }

    /// Sets the request options
    [<CustomOperation "requestOptions">]
    member _.RequestOptions (state : ReplaceConcurrentlyOperation<_, _>, options : ItemRequestOptions) =
        options.EnableContentResponseOnWrite <- enableContentResponseOnWrite
        { state with RequestOptions = options }

    /// Sets the partition key
    [<CustomOperation "update">]
    member _.Update (state : ReplaceConcurrentlyOperation<_, _>, update : 'a -> Async<Result<'a, 't>>) = {
        state with
            Update = update
    }

let replace<'a> = ReplaceBuilder<'a> (false)
let replaceAndRead<'a> = ReplaceBuilder<'a> (true)

let replaceConcurrenly<'a, 'e> = ReplaceConcurrentlyBuilder<'a, 'e> (false)
let replaceConcurrenlyAndRead<'a, 'e> = ReplaceConcurrentlyBuilder<'a, 'e> (true)

// https://docs.microsoft.com/en-us/rest/api/cosmos-db/http-status-codes-for-cosmosdb

type ReplaceResult<'t> =
    | Ok of 't // 200
    | BadRequest of ResponseBody : string // 400
    | NotFound of ResponseBody : string // 404
    | ModifiedBefore of ResponseBody : string //412 - need re-do
    | EntityTooLarge of ResponseBody : string // 413
    | TooManyRequests of ResponseBody : string * RetryAfter : TimeSpan voption // 429

type ReplaceConcurrentResult<'t, 'e> =
    | Ok of 't // 200
    | BadRequest of ResponseBody : string // 400
    | NotFound of ResponseBody : string // 404
    | ModifiedBefore of ResponseBody : string //412 - need re-do
    | EntityTooLarge of ResponseBody : string // 413
    | TooManyRequests of ResponseBody : string * RetryAfter : TimeSpan voption // 429
    | CustomError of Error : 'e

open System.Net

let private toReplaceResult (ex : CosmosException) =
    match ex.StatusCode with
    | HttpStatusCode.BadRequest -> ReplaceResult.BadRequest ex.ResponseBody
    | HttpStatusCode.NotFound -> ReplaceResult.NotFound ex.ResponseBody
    | HttpStatusCode.PreconditionFailed -> ReplaceResult.ModifiedBefore ex.ResponseBody
    | HttpStatusCode.RequestEntityTooLarge -> ReplaceResult.EntityTooLarge ex.ResponseBody
    | HttpStatusCode.TooManyRequests -> ReplaceResult.TooManyRequests (ex.ResponseBody, ex.RetryAfter |> ValueOption.ofNullable)
    | _ -> raise ex

let private toReplaceConcurrentlyErrorResult (ex : CosmosException) =
    match ex.StatusCode with
    | HttpStatusCode.NotFound -> ReplaceConcurrentResult.NotFound ex.ResponseBody
    | HttpStatusCode.BadRequest -> ReplaceConcurrentResult.BadRequest ex.ResponseBody
    | HttpStatusCode.PreconditionFailed -> ReplaceConcurrentResult.ModifiedBefore ex.ResponseBody
    | HttpStatusCode.RequestEntityTooLarge -> ReplaceConcurrentResult.EntityTooLarge ex.ResponseBody
    | HttpStatusCode.TooManyRequests ->
        ReplaceConcurrentResult.TooManyRequests (ex.ResponseBody, ex.RetryAfter |> ValueOption.ofNullable)
    | _ -> raise ex

open System.Threading
open System.Threading.Tasks

let rec executeConcurrentlyAsync<'value, 'error>
    (ct : CancellationToken)
    (container : Container)
    (operation : ReplaceConcurrentlyOperation<'value, 'error>)
    (retryAttempts : int)
    : Task<CosmosResponse<ReplaceConcurrentResult<'value, 'error>>> =
    task {
        try
            let partitionKey =
                match operation.PartitionKey with
                | ValueSome partitionKey -> partitionKey
                | ValueNone -> PartitionKey.None

            let! response = container.ReadItemAsync<'value> (operation.Id, partitionKey, cancellationToken = ct)
            let eTag = response.ETag
            let! itemUpdateResult = operation.Update response.Resource

            match itemUpdateResult with
            | Result.Error e -> return CosmosResponse.fromItemResponse (fun _ -> CustomError e) response
            | Result.Ok item ->
                let updateOptions = new ItemRequestOptions (IfMatchEtag = eTag)

                let! response =
                    container.ReplaceItemAsync<'value> (
                        item,
                        operation.Id,
                        requestOptions = updateOptions,
                        cancellationToken = ct
                    )

                return CosmosResponse.fromItemResponse Ok response
        with
        | HandleException ex when
            ex.StatusCode = HttpStatusCode.PreconditionFailed
            && retryAttempts = 1
            ->
            return CosmosResponse.fromException toReplaceConcurrentlyErrorResult ex
        | HandleException ex when ex.StatusCode = HttpStatusCode.PreconditionFailed ->
            return! executeConcurrentlyAsync ct container operation (retryAttempts - 1)
        | HandleException ex -> return CosmosResponse.fromException toReplaceConcurrentlyErrorResult ex
    }

open System.Runtime.InteropServices

[<Literal>]
let DefaultRetryCount = 10

type Microsoft.Azure.Cosmos.Container with

    member container.PlainExecuteAsync<'a>
        (operation : ReplaceOperation<'a>, [<Optional>] cancellationToken : CancellationToken)
        =
        container.ReplaceItemAsync<'a> (
            operation.Item,
            operation.Id,
            operation.PartitionKey |> ValueOption.toNullable,
            operation.RequestOptions,
            cancellationToken = cancellationToken
        )

    member private container.ExecuteCoreAsync<'a>
        (operation : ReplaceOperation<'a>, [<Optional>] cancellationToken : CancellationToken)
        =
        task {
            try
                let! response = container.PlainExecuteAsync (operation, cancellationToken)
                return CosmosResponse.fromItemResponse ReplaceResult.Ok response
            with HandleException ex ->
                return CosmosResponse.fromException toReplaceResult ex
        }

    member container.ExecuteAsync<'a> (operation : ReplaceOperation<'a>, [<Optional>] cancellationToken : CancellationToken) =
        if String.IsNullOrEmpty operation.RequestOptions.IfMatchEtag then
            invalidArg "eTag" "Safe replace requires ETag"

        container.ExecuteCoreAsync (operation, cancellationToken)

    member container.ExecuteOverwriteAsync<'a> (operation : ReplaceOperation<'a>, [<Optional>] cancellationToken : CancellationToken) =
        container.ExecuteCoreAsync (operation, cancellationToken)

    member container.ExecuteConcurrentlyAsync<'a, 'e>
        (
            operation : ReplaceConcurrentlyOperation<'a, 'e>,
            [<Optional; DefaultParameterValue(DefaultRetryCount)>] maxRetryCount : int,
            [<Optional>] cancellationToken : CancellationToken
        )
        =
        executeConcurrentlyAsync<'a, 'e> cancellationToken container operation maxRetryCount

    member container.ExecuteConcurrentlyAsync<'a, 'e>
        (operation : ReplaceConcurrentlyOperation<'a, 'e>, [<Optional>] cancellationToken : CancellationToken)
        =
        executeConcurrentlyAsync<'a, 'e> cancellationToken container operation DefaultRetryCount
