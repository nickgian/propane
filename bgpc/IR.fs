﻿module IR

open CGraph
open Common
open Common.Debug
open Common.Error
open System.Collections.Generic

exception UncontrollableEnterException of string
exception UncontrollablePeerPreferenceException of string

type CounterExample = 
    | UnusedPreferences of Map<int, Regex.T>
    | NoPathForRouters of Set<string>
    | InconsistentPrefs of CgState * CgState
    | UncontrollableEnter of string
    | UncontrollablePeerPreference of string

type Match = 
    | Peer of string 
    | State of string * string
    | PathRE of Regex.T
    | NoMatch

    override this.ToString () = 
        match this with 
        | Peer s -> "Peer=" + s
        | State(is,s) -> "Community=" + is + ", Peer=" + s
        | PathRE r -> "Regex=" + string r
        | NoMatch -> "--"

type Action = 
    | SetComm of string
    | SetMed of int
    | PrependPath of int

    override this.ToString() = 
        match this with 
        | SetComm(is) -> "Community<-" + is
        | SetMed i -> "MED<-" + string i
        | PrependPath i -> "Prepend " + string i

type LocalPref = int
type Peer = string
type Import = Match * LocalPref
type Export = Peer * Action list

/// Result from compiling a single prefix

type Filter = 
    | Deny
    | Allow of Import * (Export list)

type DeviceConfig =
    {Originates: bool;
     Filters: Filter list}

type PredConfig = Predicate.T * Map<string, DeviceConfig>

type PrefixResult =
    {K: int option;
     BuildTime: int64;
     MinimizeTime: int64;
     OrderingTime: int64;
     ConfigTime: int64;
     CompressSizeInit: int;
     CompressSizeFinal: int;
     Config: PredConfig}

type CompileResult = Result<PrefixResult, CounterExample>

/// Result from compiling the entire policy

type DeviceAggregates = (Prefix.T list * seq<string>) list
type DeviceTags = ((string * Prefix.T list) * seq<string>) list
type DeviceMaxRoutes = (uint32 * seq<string>) list

type DeviceControl = 
    {Aggregates: DeviceAggregates;
     Tags: DeviceTags;
     MaxRoutes: DeviceMaxRoutes}

type RouterConfig = 
    {Actions: (Predicate.T * DeviceConfig) list;
     Control: DeviceControl}

type T = Map<string, RouterConfig>

let joinConfigs (aggs, comms, maxroutes) (results: PrefixResult list) : T =
    let mutable result = Map.empty
    for v in results do
        let (prefix, config) = v.Config 
        for kv in config do
            let dc = kv.Value
            let router = kv.Key
            let value = (prefix, dc)
            match Map.tryFind router result with
            | None -> result <- Map.add router [value] result
            | Some x -> result <- Map.add router (value :: x) result
    Map.map (fun router vs ->
        let a = Common.Map.getOrDefault router [] aggs
        let b = Common.Map.getOrDefault router [] comms
        let c = Common.Map.getOrDefault router [] maxroutes
        {Actions=List.rev vs; Control={Aggregates=a; Tags=b; MaxRoutes=c}}) result

let format (config: T) = 
    let sb = System.Text.StringBuilder ()
    for kv in config do
        let routerName = kv.Key 
        let routerConfig = kv.Value
        sb.Append("\nRouter ") |> ignore
        sb.Append(routerName) |> ignore
        for (prefix, peers) in routerConfig.Control.Aggregates do
            for peer in peers do
                sb.Append (sprintf "\n  Aggregate(%s, %s)" (string prefix) peer) |> ignore
        for ((c, prefix), peers) in routerConfig.Control.Tags do 
            for peer in peers do 
                sb.Append (sprintf "\n  Tag(%s, %s, %s)" c (string prefix) peer) |> ignore
        for (i, peers)  in routerConfig.Control.MaxRoutes do 
            for peer in peers do 
                sb.Append (sprintf "\n  MaxRoutes(%d)" i) |> ignore
        for (pred, deviceConf) in routerConfig.Actions do
            let predStr = string pred
            for f in deviceConf.Filters do
                match f with 
                | Deny ->
                    sb.Append("\n  Deny: ") |> ignore
                    sb.Append("[Pred=" + predStr + "]") |> ignore
                | Allow ((m, lp), es) ->
                    match m with 
                    | NoMatch -> 
                        sb.Append ("\n  Originate: [Pred=" + predStr + "]") |> ignore
                    | _ ->
                        sb.Append("\n  Allow: ") |> ignore
                        sb.Append("[Pred=" + predStr + ", " + string m + "]") |> ignore
                        if (not deviceConf.Originates) && lp <> 100 then
                            sb.Append(" (LP=" + lp.ToString() + ")") |> ignore
                        for (peer, acts) in es do
                            sb.Append("\n    Export: [Peer<-" + peer) |> ignore
                            if acts <> [] then
                                let str = Common.List.joinBy "," (List.map string acts)
                                sb.Append(", " + str) |> ignore
                            sb.Append("]") |> ignore
        sb.Append("\n") |> ignore
    sb.ToString()

let formatPrefix pconfig = 
    format (joinConfigs (Map.empty, Map.empty, Map.empty) [pconfig])

(* Ensure well-formedness for controlling 
   traffic entering the network. MED and prepending allow
   certain patterns of control to immediate neighbors only *)

type IncomingPattern = 
    | Anything
    | Nothing of string
    | Specific of Regex.T

type IncomingInfo = 
    {Peers: seq<CgState>;
     Info: Map<CgState, IncomingPattern>}

type IncomingExportMap = Map<CgState, Action list>

let collectForPeer cg acc peer = 
    let reachable = 
        Reachable.src cg peer Down
        |> Set.filter (fun x -> x <> peer && Topology.isTopoNode x.Node)
    let hasRepeatedOut = Set.exists (CGraph.isRepeatedOut cg) reachable
    let hasOther = (reachable.Count > 1) || (not hasRepeatedOut && reachable.Count > 0)
    match hasRepeatedOut, hasOther with
    | false, false -> Map.add peer (Nothing peer.Node.Loc) acc
    | true, false -> Map.add peer Anything acc
    | _, true ->
        let cexample = CGraph.ToRegex.constructRegex (copyGraph cg) peer
        Map.add peer (Specific cexample) acc 

let collectIncomingInfo (cg: CGraph.T) : IncomingInfo =
    let isExportPeer v = 
        Topology.isOutside v.Node && 
        Seq.exists (fun u -> Topology.isInside u.Node) (neighborsIn cg v)
    let exportPeers = Seq.filter isExportPeer cg.Graph.Vertices
    let info = Seq.fold (collectForPeer cg) Map.empty exportPeers
    {Peers = exportPeers; Info = info}

let getUnique peers =
    Set.ofSeq (Seq.map (fun p -> p.Node.Loc) peers)

let addExports (settings: Args.T) info peers actions exportMap =
    let mutable exportMap = exportMap
    let mutable actions = actions
    for p in peers do
        match Map.find p info.Info with 
        | Anything -> ()
        | Nothing x ->
            if settings.UseNoExport then 
                actions <- (SetComm "no-export") :: actions
            else raise (UncontrollableEnterException ("enable no-export to limit incoming traffic to peer: " + x))
        | Specific re -> 
            raise (UncontrollableEnterException (sprintf "(%s) incoming traffic cannot conform to: %s" p.Node.Loc (string re)))
        exportMap <- Map.add p actions exportMap
    exportMap

let configureIncomingTraffic cg : IncomingExportMap =
    let settings = Args.getSettings()
    let info = collectIncomingInfo cg
    // Temporary 
    if not settings.CheckEnter then 
        let mutable exportMap = Map.empty 
        for p in info.Peers do 
            exportMap <- Map.add p [] exportMap
        exportMap
    else
    let byPreference =
        info.Peers 
        |> Seq.map (fun p -> (Set.minElement (Reachable.srcAccepting cg p Down), p))
        |> Seq.groupBy fst
        |> Seq.map (fun (x,y) -> (x, Seq.map snd y))
        |> Seq.sortBy fst
    let mutable exportMap = Map.empty
    let mutable i = 0
    let mutable prev = None
    for (_, peers) in byPreference do
        match prev with
        | None -> 
            exportMap <- addExports settings info peers [] exportMap
            prev <- Some peers
        | Some ps ->
            let unqNow = getUnique peers
            let unqPrev = getUnique ps
            let pre = Set.minElement unqPrev
            let now = Set.minElement unqNow
            let canAvoidAggregation = (Set.count unqPrev = 1) && (Set.count unqNow = 1) && (now = pre)
            if canAvoidAggregation && (settings.UseMed || settings.UsePrepending) then
                let mutable actions = []
                if settings.UsePrepending && i > 0 then actions <- (PrependPath (3*i)) :: actions
                if settings.UseMed && i > 0 then actions <- (SetMed (80+i)) :: actions
                exportMap <- addExports settings info peers actions exportMap
            else
                (* TODO: we need to use aggregates here since last time there were many *)
                raise (UncontrollablePeerPreferenceException now)
                ()
            prev <- Some peers
        i <- i + 1
    exportMap


(* Order config by preference and then router name. 
   This makes it easier to minimize the config *)
let inline comparePrefThenLoc (x,i1) (y,i2) = 
    let cmp = compare i1 i2
    if cmp = 0 then
        compare x.Node.Loc y.Node.Loc
    else cmp


(* Generate a configuration from the product graph *)

type OutPeerMatch = 
    | PeerMatch of CgState
    | RegexMatch of Regex.T

let inline isPeerMatch x = 
    match x with 
    | PeerMatch _ -> true
    | RegexMatch _ -> false

let inline getPeerMatch x = 
    match x with 
    | PeerMatch y -> y
    | RegexMatch _ -> failwith "unreachable"

let inline getRegexMatch x = 
    match x with 
    | PeerMatch _ -> failwith "unreachable"
    | RegexMatch y -> y

let getOutPeerType cg (x:CgState) = 
    if Topology.isOutside x.Node then
        let nin = neighborsIn cg x
        if Seq.length nin = 2 &&
           Seq.exists ((=) cg.Start) nin && 
           Seq.exists (fun v -> 
                isRepeatedOut cg v && 
                let nin = neighborsIn cg v 
                Seq.length nin = 2 && 
                Seq.exists ((=) cg.Start) nin) nin then
            PeerMatch x
        else RegexMatch (CGraph.ToRegex.constructRegex (CGraph.copyReverseGraph cg) x)
    else PeerMatch x

let getMatches allTopoPeers incomingMatches =
    let peers, regexes = 
        List.ofSeq incomingMatches 
        |> List.partition isPeerMatch
    let peers = List.map getPeerMatch peers |> Set.ofList
    let peerLocs = Set.map (fun v -> v.Node.Loc) peers
    let mutable matches = 
        List.map (fun r -> Match.PathRE (getRegexMatch r)) regexes
    let states = Set.map (fun v -> v.State) peers
    let eqStates = Set.count states = 1
    if peerLocs = allTopoPeers && eqStates then 
        matches <- Match.State(string (Set.minElement states),"*") :: matches
    else
        for v in peers do 
            let m = Match.State(string v.State, v.Node.Loc)
            matches <- m :: matches
    matches

let inline getExport specialCase inExports x v = 
    if Topology.isOutside v.Node 
    then 
        let ret = Map.find v inExports
        if ret <> [] then 
            specialCase := true
        ret
    else [SetComm(string x.State)]

let getExports allTopoPeers x inExports (outgoing: seq<CgState>) unqMatchPeer = 
    let toInside, toOutside = 
        List.ofSeq outgoing 
        |> List.partition (fun v -> Topology.isInside v.Node)
    if List.isEmpty toOutside then 
        [("*", [SetComm (string x.State)])]
    else
        let insideExport = [("in", [SetComm (string x.State)])]
        let specialCase = ref false
        let exports = List.ofSeq toOutside |> List.map (fun v -> (v.Node.Loc, getExport specialCase inExports x v)) 
        if not !specialCase then
            let sendToLocs = Seq.map (fun v -> v.Node.Loc) outgoing |> Set.ofSeq
            let sendToLocs = 
                match unqMatchPeer with
                | Some x -> Set.add x sendToLocs
                | None -> sendToLocs
            if allTopoPeers = sendToLocs then 
                [("*", [SetComm(string x.State)])]
            else exports @ insideExport
        else  exports @ insideExport

let updateExports (es: Export list) f : Export list = 
    List.choose f es

let removeRedundantTag exports m = 
    match m with 
    | Match.Peer _ | Match.PathRE _ | Match.NoMatch -> exports 
    | Match.State(is,_) -> 
        updateExports exports (fun (peer, acts) -> 
            let acts' = List.filter (fun a -> 
                match a with 
                | SetComm c -> c <> is
                | _ -> true) acts
            Some (peer, acts') )

let removeCommMatch cg eCounts v m = 
    let inline unq e = Map.find e eCounts = 1
    match m with 
    | Match.State (c,peers) -> 
        if peers = "*" then 
            let ins = CGraph.neighborsIn cg v |> Seq.map CGraph.loc
            if Seq.forall (fun i -> unq (i, v.Node.Loc)) ins then Match.Peer(peers) else m 
        else if unq (peers, v.Node.Loc) then Match.Peer(peers)
        else m
    | _ -> m

let edgeCounts (cg: CGraph.T) =
    cg.Graph.Edges
    |> Seq.fold (fun acc e -> 
            let key = (e.Source.Node.Loc, e.Target.Node.Loc)
            Common.Map.adjust key 0 ((+) 1) acc ) Map.empty

let genConfig (cg: CGraph.T) (pred: Predicate.T) (ord: Consistency.Ordering) (inExports: IncomingExportMap) : PredConfig * int * int =
    let settings = Args.getSettings ()
    let ain = Topology.alphabet cg.Topo |> fst |> Set.map (fun v -> v.Loc)
    let eCounts = edgeCounts cg
    let szRaw = ref 0 
    let szSmart = ref 0
    let mutable config = Map.empty
    for router in ain do
        let mutable filters = []
        let mutable originates = false
        match Map.tryFind router ord with 
        | None -> ()
        | Some prefs ->
            let mutable rules = []
            let mutable lp = 101
            for cgstate in prefs do
                lp <- lp - 1
                let allTopoPeers = 
                    cg.Topo.OutEdges cgstate.Node
                    |> Seq.map (fun e -> e.Target.Loc)
                    |> Set.ofSeq
                let nsIn = neighborsIn cg cgstate
                let nsOut = neighbors cg cgstate
                let receiveFrom = Seq.filter CGraph.isRealNode nsIn
                let sendTo = Seq.filter CGraph.isRealNode nsOut
                let peerTypes = Seq.map (getOutPeerType cg) receiveFrom
                let origin = Seq.exists ((=) cg.Start) nsIn
                let matches =  if origin then [Match.NoMatch] else getMatches allTopoPeers peerTypes
                let unqMatchPeer = 
                    match matches with 
                    | [Match.Peer x] -> Some x
                    | [Match.State(_,x)] -> Some x 
                    | _ -> None
                let exports = getExports allTopoPeers cgstate inExports sendTo unqMatchPeer                
                for m in matches do 
                    let exports = removeRedundantTag exports m
                    let m = removeCommMatch cg eCounts cgstate m
                    filters <- Allow ((m,lp), exports) :: filters
                originates <- origin || originates
                szRaw := !szRaw + (Seq.length nsIn) * (Seq.length nsOut)
                szSmart := !szSmart + (List.length exports)
        szSmart := !szSmart + (List.length filters)
        filters <- List.rev (Deny :: filters)
        let deviceConf = {Originates=originates; Filters=filters}
        config <- Map.add router deviceConf config
    (pred, config), !szRaw, !szSmart

let getLocsThatCantGetPath idx cg (reb: Regex.REBuilder) dfas = 
    let startingLocs = Array.fold (fun acc dfa -> Set.union (reb.StartingLocs dfa) acc) Set.empty dfas
    let originators = 
        CGraph.neighbors cg cg.Start
        |> Seq.map (fun v -> v.Node.Loc)
        |> Set.ofSeq
    let canOriginate = 
        cg.Topo.Vertices
        |> Seq.filter (fun v -> Topology.isInside v && Topology.canOriginateTraffic v)
        |> Seq.map (fun v -> v.Loc)
        |> Set.ofSeq
    let locsThatNeedPath = Set.difference (Set.intersect startingLocs canOriginate) originators
    let locsThatGetPath = CGraph.acceptingLocations cg
    logInfo1(idx, sprintf "Locations that need path: %s" (locsThatNeedPath.ToString()))
    logInfo1(idx, sprintf "Locations that get path: %s" (locsThatGetPath.ToString()))
    Set.difference locsThatNeedPath locsThatGetPath

let getUnusedPrefs cg res = 
    let numberedRegexes = seq {for i in 1.. List.length res do yield i}  |> Set.ofSeq
    let prefs = CGraph.preferences cg
    Set.difference numberedRegexes prefs

// TODO: only do this once total per prefix
let getMinAggregateFailures (cg: CGraph.T) pred (aggInfo: Map<string, DeviceAggregates>) = 
    let originators = CGraph.neighbors cg cg.Start
    let prefixes = Predicate.getPrefixes pred
    let mutable smallest = System.Int32.MaxValue
    for p in prefixes do
        for kv in aggInfo do 
            let aggRouter = kv.Key 
            let aggs = kv.Value 
            let relevantAggs = List.filter (fun (prefix, _) -> Prefix.implies (Prefix.toPredicate prefix) p) aggs
            for (rAgg, _) in relevantAggs do
                let k = CGraph.Failure.disconnectLocs cg originators aggRouter    
                smallest <- min smallest k
    if smallest = System.Int32.MaxValue then  None
    else Some smallest

let compileToIR fullName idx pred (aggInfo: Map<string,DeviceAggregates>) (reb: Regex.REBuilder) res : CompileResult =
    let settings = Args.getSettings ()
    let fullName = fullName + "(" + (string idx) + ")"
    let dfas, dfaTime = Profile.time (List.map (fun r -> reb.MakeDFA (Regex.rev r))) res
    let dfas = Array.ofList dfas
    let cg, pgTime = Profile.time (CGraph.buildFromAutomata (reb.Topo())) dfas
    let buildTime = dfaTime + pgTime
    debug1 (fun () -> CGraph.generatePNG cg fullName )
    let cg, delTime = Profile.time (CGraph.Minimize.delMissingSuffixPaths) cg
    let cg, minTime = Profile.time (CGraph.Minimize.minimize idx) cg
    let minTime = delTime + minTime
    debug1 (fun () -> CGraph.generatePNG cg (fullName + "-min"))
    // check there is a route for each location specified
    let lost = getLocsThatCantGetPath idx cg reb dfas
    if not (Set.isEmpty lost) then Err(NoPathForRouters(lost)) else
    
    // TODO: handle this better
    // let unusedPrefs = getUnusedPrefs cg res
    // if not (Set.isEmpty unusedPrefs)  then
    //    let cexamples = Set.fold (fun acc p -> 
    //        Map.add p (List.item (p-1) res) acc) Map.empty unusedPrefs
    //    Err(UnusedPreferences(cexamples))
    //else
        try
            // check that BGP can ensure incoming traffic compliance
            let inExports = configureIncomingTraffic cg
            // check aggregation failure consistency
            let k = getMinAggregateFailures cg pred aggInfo
            // check  that BGP preferences can be set properly
            let (ordering, orderTime) = Profile.time (Consistency.findOrderingConservative idx cg) fullName
            match ordering with 
            | Ok ord ->
                let (config, szRaw, szSmart), configTime = Profile.time (genConfig cg pred ord) inExports
                let result = 
                    {K=k; 
                      BuildTime=buildTime; 
                      MinimizeTime=minTime; 
                      OrderingTime=orderTime;
                      ConfigTime=configTime; 
                      CompressSizeInit=szRaw;
                      CompressSizeFinal=szSmart;
                      Config=config}
                debug1 (fun () -> System.IO.File.WriteAllText (sprintf "%s.ir" fullName, formatPrefix result) )
                Ok (result)
            | Err((x,y)) -> Err(InconsistentPrefs(x,y))
        with 
            | UncontrollableEnterException s -> Err(UncontrollableEnter s)
            | UncontrollablePeerPreferenceException s -> Err(UncontrollablePeerPreference s)

let compileForSinglePrefix fullName idx (aggInfo: Map<string, DeviceAggregates>) (pred, reb, res) =
    try 
        match compileToIR fullName idx pred aggInfo reb res with 
        | Ok(config) -> config
        | Err(x) ->
            match x with
            | UnusedPreferences m ->
                error (sprintf "Unused preferences %A" m)
            | NoPathForRouters rs ->
                unimplementable (sprintf "Unable to find a path for routers: %s for predicate %s" (string rs) (string pred))
            | InconsistentPrefs(x,y) ->
                let xs = x.ToString()
                let ys = y.ToString() 
                unimplementable (sprintf "Can not choose preference between:\n%s\n%s\nfor predicate %s" xs ys (string pred))
            | UncontrollableEnter x -> 
                unimplementable (sprintf "Can not control inbound traffic from peer: %s for predicate %s" x (string pred))
            | UncontrollablePeerPreference x -> 
                unimplementable (sprintf "Can not control inbound preference from peer: %s without MED or prepending for predicate %s" x (string pred))
    with Topology.InvalidTopologyException -> 
        error (sprintf "Invalid Topology: internal topology must be weakly connected")

let checkAggregateLocs ins _ prefix links = 
    if Set.contains "out" ins then
        error (sprintf "Cannot aggregate on external location: out for prefix: %s" (string prefix))
    match List.tryFind (fst >> Topology.isOutside) links with
    | None -> ()
    | Some x -> 
        error (sprintf "Cannot aggregate on external location: %s for prefix: %s" (fst x).Loc (string prefix))

let checkCommunityTagLocs ins _ (c, prefix) links =
    if Set.contains "out" ins then
        error (sprintf "Cannot tag communities on external location: out for community %s, prefix: %s" c (string prefix))
    match List.tryFind (fst >> Topology.isOutside) links with
    | None -> ()
    | Some x -> 
        error (sprintf "Cannot tag communities on external location: %s for community %s prefix: %s" (fst x).Loc c (string prefix))

let checkMaxRouteLocs ins outs i links =
    let v = List.exists (fun (a,b) -> Topology.isOutside a && Topology.isOutside b) links
    let w = Set.contains "out" ins
    let x = Set.contains "out" outs
    let y = List.exists (fst >> Topology.isOutside) links
    let z = List.exists (snd >> Topology.isOutside) links
    if v || ((w || y) && (x || z)) then 
        error (sprintf "cannot set maxroutes(%d) on links without and edge in the internal topology" i)

let splitByLocation f topo (vs: _ list) = 
    let mutable acc = Map.empty
    for (k, ins, outs) in vs do 
        let links = Topology.findLinks topo (ins,outs)
        f ins outs k links
        let pairs = 
            links
            |> List.map (fun (x,y) -> (x.Loc, y.Loc))
            |> Seq.groupBy fst
            |> Seq.map (fun (x,y) -> (x, [(k, Seq.map snd y)]))
            |> Map.ofSeq
        acc <- Common.Map.merge acc pairs (fun _ (xs,ys) -> xs @ ys)
    acc

let splitConstraints topo (constraints: _ list) =
    let aggs, comms, maxroutes = 
        List.fold (fun ((x,y,z) as acc) c -> 
            match c with
            | Ast.CAggregate (p,ins,outs) -> ((p,ins,outs)::x, y, z)
            | Ast.CCommunity (s,p,ins,outs) -> (x, ((s,p),ins,outs)::y, z)
            | Ast.CMaxRoutes (i,ins,outs) -> (x, y, (i,ins,outs)::z)
            | _ -> acc
        ) ([], [], []) constraints
    let aggInfo = splitByLocation checkAggregateLocs topo aggs
    let commInfo = splitByLocation checkCommunityTagLocs topo comms
    let maxRouteInfo = splitByLocation checkMaxRouteLocs topo maxroutes
    (aggInfo, commInfo, maxRouteInfo)
    
type Stats = 
    {NumPrefixes: int;
     SizeRaw: int;
     SizeCompressed: int;
     TotalTime: int64;
     PrefixTime: int64;
     PerPrefixTimes: int64 array;
     PerPrefixBuildTimes: int64 array;
     PerPrefixMinTimes: int64 array;
     PerPrefixOrderTimes: int64 array;
     PerPrefixGenTimes: int64 array;
     JoinTime: int64;}

let minFails x y = 
    match x, y with 
    | None, _ -> y 
    | _, None -> x 
    | Some i, Some j -> Some (min i j)

let compileAllPrefixes fullName topo (pairs: Ast.PolicyPair list) constraints : T * int option * Stats =
    let info = splitConstraints topo constraints
    let (aggInfo, _, _) = info
    let pairs = Array.ofList pairs
    let timedConfigs, prefixTime =
        Profile.time (Array.Parallel.mapi (fun i x -> 
            Profile.time (compileForSinglePrefix fullName (i+1) aggInfo) x)) pairs
    let nAggFails = Array.map (fun (res,_) -> res.K) timedConfigs
    let k = Array.fold minFails None nAggFails
    let configs, times = Array.unzip timedConfigs
    let joined, joinTime = Profile.time (joinConfigs info) (Array.toList configs)    
    let buildTimes = Array.map (fun c -> c.BuildTime) configs
    let minTimes = Array.map (fun c -> c.MinimizeTime) configs
    let orderTimes = Array.map (fun c -> c.OrderingTime) configs
    let genTimes = Array.map (fun c -> c.ConfigTime) configs
    let szInit = Array.fold (fun acc c -> c.CompressSizeInit + acc) 0 configs
    let szFinal = Array.fold (fun acc c -> c.CompressSizeFinal + acc) 0 configs
    let stats = 
        {NumPrefixes=Array.length configs;
         SizeRaw=szInit;
         SizeCompressed=szFinal;
         TotalTime=prefixTime + joinTime;
         PrefixTime=prefixTime;
         PerPrefixTimes=times
         PerPrefixBuildTimes=buildTimes;
         PerPrefixMinTimes=minTimes;
         PerPrefixOrderTimes=orderTimes;
         PerPrefixGenTimes=genTimes;
         JoinTime=joinTime}
    joined, k, stats