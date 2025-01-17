﻿module internal DG.XrmDefinitelyTyped.CrmBaseHelper

open System
open System.Threading.Tasks

open Utility
open Microsoft.Xrm.Sdk
open Microsoft.Xrm.Sdk.Client
open Microsoft.Xrm.Sdk.Messages
open Microsoft.Xrm.Sdk.Query
open Microsoft.Xrm.Sdk.Metadata
open Microsoft.Crm.Sdk.Messages
open Microsoft.Xrm.Sdk.WebServiceClient

// Execute request
let getResponse<'T when 'T :> OrganizationResponse> (proxy:IOrganizationService) request =
  if(proxy :? OrganizationServiceProxy) then 
    let orgProxy = proxy :?> OrganizationServiceProxy
    orgProxy.Timeout <- TimeSpan(1,0,0)
  (proxy.Execute(request)) :?> 'T

// Retrieve version
let retrieveVersion proxy =
  let req = RetrieveVersionRequest()
  let resp = getResponse<RetrieveVersionResponse> proxy req
  parseVersion resp.Version

// Retrieve data
let internal retrieveMultiple (proxy:IOrganizationService) logicalName (query:QueryExpression) = 
  query.PageInfo <- PagingInfo()

  let rec retrieveMultiple' 
    (proxy:IOrganizationService) (query:QueryExpression) page cookie =
    seq {
        query.PageInfo.PageNumber <- page
        query.PageInfo.PagingCookie <- cookie
        let resp = proxy.RetrieveMultiple(query)
        yield! resp.Entities

        match resp.MoreRecords with
        | true -> yield! retrieveMultiple' proxy query (page + 1) resp.PagingCookie
        | false -> ()
    }
  retrieveMultiple' proxy query 1 null

// Perform requests as bulk
let performAsBulk proxy requests handleResponse =
  let request = ExecuteMultipleRequest()
  request.Requests <- OrganizationRequestCollection()
  request.Requests.AddRange(requests)
  request.Settings <- ExecuteMultipleSettings()
  request.Settings.ContinueOnError <- false
  request.Settings.ReturnResponses <- true

  let bulkResp = getResponse<ExecuteMultipleResponse> proxy request
  bulkResp.Responses
  |> Seq.map (fun resp -> 
    if isNull resp.Fault then handleResponse resp
    else failwithf "Error while retrieving entity metadata: %s" resp.Fault.Message)
  |> Seq.toArray

// Get all entities
let internal getEntities 
  proxy (logicalName:string) (cols:string list) =

  let q = QueryExpression(logicalName)
  if cols.Length = 0 then q.ColumnSet <- ColumnSet(true)
  else q.ColumnSet <- ColumnSet(Array.ofList cols)

  retrieveMultiple proxy logicalName q

// Get all entities with a filter
let internal getEntitiesFilter 
  (proxy:IOrganizationService) (logicalName:string)
  (cols:string list) (filter:Map<string,obj>) =
    
  let f = FilterExpression()
  filter |> Map.iter(fun k v -> f.AddCondition(k, ConditionOperator.Equal, v))

  let q = QueryExpression(logicalName)
  if cols.Length = 0 then q.ColumnSet <- ColumnSet(true)
  else q.ColumnSet <- ColumnSet(Array.ofList cols)
  q.Criteria <- f
    
  retrieveMultiple proxy logicalName q

// Retrieve entity metadata for all entities
let getAllEntityMetadataLight proxy =
  let request = RetrieveAllEntitiesRequest()
  request.EntityFilters <- Microsoft.Xrm.Sdk.Metadata.EntityFilters.Entity
  let resp = getResponse<RetrieveAllEntitiesResponse> proxy request
  resp.EntityMetadata

// Retrieve all metadata for all entities
let getAllEntityMetadata (proxy:IOrganizationService) =
  let request = RetrieveAllEntitiesRequest()
  request.EntityFilters <- Microsoft.Xrm.Sdk.Metadata.EntityFilters.All
  request.RetrieveAsIfPublished <- false

  let resp = getResponse<RetrieveAllEntitiesResponse> proxy request
  resp.EntityMetadata

// Make retrieve request
let getEntityMetadataRequest lname = 
  let request = RetrieveEntityRequest()
  request.LogicalName <- lname
  request.EntityFilters <- Microsoft.Xrm.Sdk.Metadata.EntityFilters.All
  request.RetrieveAsIfPublished <- false
  request

// Retrieve single entity metadata
let getEntityMetadata proxy lname =
  let resp = getEntityMetadataRequest lname |> getResponse<RetrieveEntityResponse> proxy
  resp.EntityMetadata
    

// Retrieve entity metadata with a bulk request
let getEntityMetadataBulk proxy lnames =
  let requests = 
    lnames 
    |> Array.map (getEntityMetadataRequest >> fun x -> x :> OrganizationRequest)

  let handleRespnose (resp: ExecuteMultipleResponseItem) = (resp.Response :?> RetrieveEntityResponse).EntityMetadata
  
  performAsBulk proxy requests handleRespnose


// Make a task of a function
let makeAsyncTask  (f : unit->'a) = 
  async { return! Task<'a>.Factory.StartNew( new Func<'a>(f) ) |> Async.AwaitTask }


// Retrieve all optionset metadata
let getAllOptionSetMetadata proxy =
  let request = RetrieveAllOptionSetsRequest()
  request.RetrieveAsIfPublished <- true

  let resp = getResponse<RetrieveAllOptionSetsResponse> proxy request
  resp.OptionSetMetadata

    
// Find relationship intersect entities
let findRelationEntities allLogicalNames (metadata:EntityMetadata[]) =
  metadata
  |> Array.Parallel.map (fun md ->
    md.ManyToManyRelationships 
    |> Array.filter (fun m2m -> 
      m2m.Entity1LogicalName = md.LogicalName && 
      Set.contains m2m.Entity2LogicalName allLogicalNames &&
      not(Set.contains m2m.IntersectEntityName allLogicalNames))
    |> Array.map (fun m2m -> m2m.IntersectEntityName))
  |> Array.concat
  |> Array.distinct


// Retrieve specific entity metadata along with any intersect
let getSpecificEntitiesAndDependentMetadata proxy logicalNames =
  // TODO: either figure out the best degree of parallelism through code, or add it as a setting
  let getMetadata = getEntityMetadataBulk proxy
  let entities = getMetadata logicalNames

  let set = logicalNames |> Set.ofArray
  let needActivityParty =
    not (set.Contains "activityparty") &&
    entities 
    |> Array.exists (fun m -> 
      m.Attributes 
      |> Array.exists (fun a -> 
        a.AttributeType.GetValueOrDefault() = AttributeTypeCode.PartyList))

  let additionalEntities = 
    findRelationEntities set entities
    |> if needActivityParty then Array.append [|"activityparty"|] else id
    |> getMetadata

  Array.append entities additionalEntities
  |> Array.distinctBy (fun e -> e.LogicalName)
  |> Array.sortBy (fun e -> e.LogicalName)


// Retrieve single entity metadata
let getEntityLogicalNameFromId (proxy:IOrganizationService) metadataId =
  let request = RetrieveEntityRequest()
  request.MetadataId <- metadataId
  request.EntityFilters <- Microsoft.Xrm.Sdk.Metadata.EntityFilters.Entity
  request.RetrieveAsIfPublished <- true

  let resp = getResponse<RetrieveEntityResponse> proxy request
  resp.EntityMetadata.LogicalName


// Retrieves all the logical names of entities in a solution
let retrieveSolutionEntities (proxy:IOrganizationService) solutionName =
  let solutionFilter = [("uniquename", solutionName)] |> Map.ofList
  let solutions = 
    getEntitiesFilter proxy "solution" 
      ["solutionid"; "uniquename"] solutionFilter
    
  solutions
  |> Seq.map (fun sol ->
    let solutionComponentFilter = 
      [ ("solutionid", sol.GetAttributeValue<obj>("solutionid")) 
        ("componenttype", 1 :> obj) // 1 = Entity
      ] |> Map.ofList

    getEntitiesFilter proxy "solutioncomponent" 
      ["solutionid"; "objectid"; "componenttype"] solutionComponentFilter
    |> Seq.map (fun sc -> 
      getEntityLogicalNameFromId proxy (sc.GetAttributeValue<Guid>("objectid"))
    )
  )
  |> Seq.concat

// Proxy helper that makes it easy to get a new proxy instance
let proxyHelper xrmAuth () =
  let ap = xrmAuth.ap ?| AuthenticationProviderType.OnlineFederation
  let domain = xrmAuth.domain ?| ""
  let mfaAppId = xrmAuth.mfaAppId ?| ""
  let mfaReturnUrl = xrmAuth.mfaReturnUrl ?| ""
  let proxyInstance = 
    match mfaReturnUrl with
        | "" ->
            let manager = CrmAuth.getServiceManagement xrmAuth.url
            let authToken = CrmAuth.authenticate manager ap xrmAuth.username xrmAuth.password domain
            CrmAuth.getOrganizationServiceProxy manager authToken
        | _ -> CrmAuth.getOrganizationServiceProxyUsingMFA xrmAuth.username xrmAuth.password xrmAuth.url mfaAppId mfaReturnUrl 
  proxyInstance