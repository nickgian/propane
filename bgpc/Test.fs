﻿module Test
open IR
open CGraph
open System
open Common.Debug
open Common.Error

let maxTests = 100000

(********************************************* 
 *  Config helpers
 *********************************************)

let isPeer x m = 
    match m with 
    | IR.Peer y -> x = y || y = "*"
    | IR.State(_,y) -> x = y || y = "*"
    | _ -> false

let prefersPeer config x (a,b) =
    try 
        let deviceConfig = Map.find x config 
        let ((_,lp1), _) = List.find (fun ((m,_), _) -> isPeer a m) deviceConfig.Filters
        let ((_,lp2), _) = List.find (fun ((m,_), _) -> isPeer b m) deviceConfig.Filters
        lp1 < lp2
    with _ -> false

let receiveFrom config x y = 
    let deviceConf = Map.find x config 
    List.exists (fun ((m,_), _) -> isPeer y m) deviceConf.Filters

let originates (config: IR.T) x =
    let deviceConfig = Map.find x config
    deviceConfig.Originates

(********************************************* 
 *  Regular expression queries
 *********************************************)

type FailReason = 
    | InconsistentPrefs
    | NoPathForRouters

type Test = 
    {Name: string;
     Explanation: string;
     Topo: Topology.T;
     Rf: Regex.REBuilder -> Regex.T list;
     Receive: (string*string) list option;
     Originate: string list option;
     Prefs: (string*string*string) list option
     Fail: FailReason option}
 
let tDiamond = Examples.topoDiamond () 
let tDatacenterSmall = Examples.topoDatacenterSmall ()
let tDatacenterMedium = Examples.topoDatacenterMedium ()
let tDatacenterLarge = Examples.topoDatacenterLarge ()
let tBrokenTriangle = Examples.topoBrokenTriangle ()
let tBigDipper = Examples.topoBigDipper () 
let tBadGadget = Examples.topoBadGadget ()
let tSeesaw = Examples.topoSeesaw ()
let tStretchingManWAN = Examples.topoStretchingManWAN ()

let rDiamond1 (reb: Regex.REBuilder) = 
    let pref1 = reb.Concat (List.map reb.Loc ["A"; "X"; "N"; "Y"; "B"])
    [reb.Build pref1]

let rDiamond2 (reb: Regex.REBuilder) = 
    let pref1 = reb.Concat (List.map reb.Loc ["A"; "X"; "N"; "Y"; "B"])
    let pref2 = reb.Concat [reb.Loc "A"; reb.Star reb.Inside; reb.Loc "N"; reb.Loc "Z"; reb.Loc "B"]
    [reb.Build pref1; reb.Build pref2]

let rDatacenterSmall1 (reb: Regex.REBuilder) = 
    let pref1 = reb.Internal()
    [reb.Build pref1]

let rDatacenterSmall2 (reb: Regex.REBuilder) = 
    let pref1 = reb.Inter [reb.Waypoint("M"); reb.EndsAt("A")]
    [reb.Build pref1]

let rDatacenterSmall3 (reb: Regex.REBuilder) =
    let pref1 = reb.Inter [reb.Waypoint "M"; reb.EndsAt "A"]
    let pref2 = reb.Inter [reb.Internal(); reb.EndsAt "A"]
    [reb.Build pref1; reb.Build pref2]

let rDatacenterSmall4 (reb: Regex.REBuilder) =
    let pref1 = reb.EndsAt("A")
    [reb.Build pref1]

let rDatacenterSmall5 (reb: Regex.REBuilder) =
    let pref1 = reb.Inter [reb.Waypoint("M"); reb.EndsAt("A")]
    [reb.Build pref1]

let rDatacenterMedium1 (reb: Regex.REBuilder) =
    let pref1 = reb.Internal()
    [reb.Build pref1]

let rDatacenterMedium2 (reb: Regex.REBuilder) =
    let pref1 = reb.Inter [reb.Waypoint("X"); reb.EndsAt("F")]
    [reb.Build pref1]

let rDatacenterMedium3 (reb: Regex.REBuilder) =
    let vf = reb.ValleyFree([["A";"B";"E";"F"]; ["C";"D";"G";"H"]; ["X";"Y"]])
    let pref1 = reb.Inter [reb.Waypoint("X"); reb.EndsAt("F"); vf]
    [reb.Build pref1]

let rDatacenterMedium4 (reb: Regex.REBuilder) =
    let vf = reb.ValleyFree([["A";"B";"E";"F"]; ["C";"D";"G";"H"]; ["X";"Y"]])
    let start = reb.StartsAtAny(["A"; "B"])
    let pref1 = reb.Inter [start; reb.Waypoint("X"); reb.EndsAt("F"); vf]
    let pref2 = reb.Inter [reb.EndsAt("F"); vf]
    [reb.Build pref1; reb.Build pref2]

let rDatacenterLarge1 (reb: Regex.REBuilder) =
    let pref1 = reb.Inter [reb.Waypoint("M"); reb.EndsAt("A")]
    [reb.Build pref1]

let rDatacenterLarge2 (reb: Regex.REBuilder) =
    let pref1 = reb.Inter [reb.Waypoint("M"); reb.EndsAt("A")]
    let pref2 = reb.EndsAt("A")
    [reb.Build pref1; reb.Build pref2]

let rDatacenterLarge3 (reb: Regex.REBuilder) =
    let pref1 = reb.Inter [reb.Waypoint("M"); reb.EndsAt("A")]
    let pref2 = reb.Inter [reb.Waypoint("N"); reb.EndsAt("A")]
    let pref3 = reb.EndsAt("A")
    [reb.Build pref1; reb.Build pref2; reb.Build pref3]

let rBrokenTriangle1 (reb: Regex.REBuilder) =
    let pref1 = reb.Union [reb.Path(["C"; "A"; "E"; "D"]); reb.Path(["A"; "B"; "D"])]
    [reb.Build pref1]

let rBigDipper1 (reb: Regex.REBuilder) =
    let op1 = reb.Path(["C"; "A"; "E"; "D"])
    let op2 = reb.Path(["A"; "E"; "D"])
    let op3 = reb.Path(["A"; "D"])
    let pref1 = reb.Union [op1; op2; op3]
    [reb.Build pref1]

let rBadGadget1 (reb: Regex.REBuilder) =
    let op1 = reb.Path(["A"; "C"; "D"])
    let op2 = reb.Path(["B"; "A"; "D"])
    let op3 = reb.Path(["C"; "B"; "D"])
    let pref1 = reb.Union [op1; op2; op3]
    let op4 = reb.Path(["A"; "D"]) 
    let op5 = reb.Path(["B"; "D"])
    let op6 = reb.Path(["C"; "D"])
    let pref2 = reb.Union [op4; op5; op6]
    [reb.Build pref1; reb.Build pref2]

let rBadGadget2 (reb: Regex.REBuilder) =
    let op1 = reb.Path(["A"; "C"; "D"])
    let op2 = reb.Path(["B"; "A"; "D"])
    let op3 = reb.Path(["C"; "B"; "D"])
    let op4 = reb.Path(["A"; "D"]) 
    let op5 = reb.Path(["B"; "D"])
    let op6 = reb.Path(["C"; "D"])
    let pref1 = reb.Union [op1; op2; op3; op4; op5; op6]
    [reb.Build pref1]

let rSeesaw1 (reb: Regex.REBuilder) = 
    let op1 = reb.Path(["A"; "X"; "N"; "M"])
    let op2 = reb.Path(["B"; "X"; "N"; "M"])
    let op3 = reb.Path(["A"; "X"; "O"; "M"])
    let op4 = reb.Path(["X"; "O"; "M"])
    let pref1 = reb.Union [op1; op2; op3; op4]
    let pref2 = reb.Path(["X"; "N"; "M"])
    [reb.Build pref1; reb.Build pref2]

let rStretchingManWAN1 (reb: Regex.REBuilder) = 
    let pref1 = reb.Concat [reb.Star reb.Outside; reb.Loc "A"; reb.Star reb.Inside; reb.Loc "Y"]
    let pref2 = reb.Concat [reb.Star reb.Outside; reb.Loc "B"; reb.Star reb.Inside; reb.Outside]
    [reb.Build pref1; reb.Build pref2]

let rStretchingManWAN2 (reb: Regex.REBuilder) = 
    let pref1 = reb.Concat [reb.Star reb.Outside; reb.Loc "A"; reb.Star reb.Inside; reb.Loc "Y"; reb.Star reb.Outside; reb.Loc "ASChina"]
    [reb.Build pref1]

let tests = [

    {Name= "Diamond1";
     Explanation="A simple path";
     Topo= tDiamond;
     Rf= rDiamond1; 
     Receive= Some [("Y", "B"); ("N","Y"); ("X","N"); ("A","X")];
     Originate = Some ["B"];
     Prefs = Some [];
     Fail = None};

    {Name= "Diamond2";
     Explanation="Impossible Backup (should fail)";
     Topo= tDiamond;
     Rf= rDiamond2; 
     Receive= None;
     Originate = None;
     Prefs = None; 
     Fail = Some InconsistentPrefs};

    {Name= "DCsmall1";
     Explanation="Shortest paths routing";
     Topo= tDatacenterSmall;
     Rf= rDatacenterSmall1; 
     Receive= Some [];
     Originate = Some ["A"; "B"; "C"; "D"];
     Prefs = Some [];
     Fail = None};
   
    {Name= "DCsmall2";
     Explanation="Waypoint through spine no backup (should fail)";
     Topo= tDatacenterSmall;
     Rf= rDatacenterSmall2; 
     Receive= None;
     Originate = None;
     Prefs = None; 
     Fail = Some NoPathForRouters};

    {Name= "DCsmall3";
     Explanation="Waypoint through spine with backup";
     Topo= tDatacenterSmall;
     Rf= rDatacenterSmall3; 
     Receive= Some [("X", "A"); ("M", "X"); ("N", "X"); ("B", "X"); ("Y", "M"); ("Y", "N"); ("C", "Y"); ("D", "Y")];
     Originate = Some ["A"];
     Prefs = Some [("Y", "M", "N")]; 
     Fail = None};

    {Name= "DCsmall4";
     Explanation="End at single location";
     Topo= tDatacenterSmall;
     Rf= rDatacenterSmall4; 
     Receive= Some [("X", "A"); ("M","X"); ("N", "X"); ("Y", "M"); ("Y", "N"); ("C", "Y"); ("D", "Y")];
     Originate = Some ["A"];
     Prefs = Some []; 
     Fail = None };

    {Name= "DCsmall5";
     Explanation="Waypoint through spine to single location (should fail)";
     Topo= tDatacenterSmall;
     Rf= rDatacenterSmall5; 
     Receive= None;
     Originate = None;
     Prefs = None; 
     Fail = Some NoPathForRouters};

    {Name= "DCmedium1";
     Explanation="Shortest paths routing";
     Topo= tDatacenterMedium;
     Rf= rDatacenterMedium1; 
     Receive= Some [];
     Originate = Some [];
     Prefs = Some [];
     Fail = None};

    {Name= "DCmedium2";
     Explanation="Waypoint through spine (should fail)";
     Topo= tDatacenterMedium;
     Rf= rDatacenterMedium2; 
     Receive= None;
     Originate = None;
     Prefs = None;
     Fail = Some InconsistentPrefs}; 

    {Name= "DCmedium3";
     Explanation="Waypoint through spine, valley free (should fail)";
     Topo= tDatacenterMedium;
     Rf= rDatacenterMedium3; 
     Receive= None;
     Originate = None;
     Prefs = None;
     Fail = Some InconsistentPrefs};

    {Name= "DCmedium4";
     Explanation="Waypoint through spine, valley free with simple backup";
     Topo= tDatacenterMedium;
     Rf= rDatacenterMedium4; 
     Receive= Some [("G", "F"); ("H", "F"); ("E","G"); ("E","H"); ("X", "G"); 
                    ("X","H"); ("Y","G"); ("Y", "H"); ("C","X"); ("C","Y"); 
                    ("D","X"); ("D","Y"); ("A","C"); ("A","D"); ("B","C"); ("B", "D"); 
                    ("H","X"); ("H","Y"); ("G","X"); ("G","Y")]; (* Strange, but safe *)
     Originate = Some ["F"];
     Prefs = Some [("C", "X", "Y"); ("D", "X", "Y"); ("G", "F", "X"); ("G", "F", "Y"); ("H", "F", "X"); ("H", "F", "Y")];
     Fail = None};

    {Name= "DClarge1";
     Explanation="Waypoint through spine (should fail)";
     Topo= tDatacenterLarge;
     Rf= rDatacenterLarge1; 
     Receive= None;
     Originate = None;
     Prefs = None;
     Fail = Some NoPathForRouters};

    {Name= "DClarge2";
     Explanation="Waypoint through spine with backup (should fail due to valleys)";
     Topo= tDatacenterLarge;
     Rf= rDatacenterLarge2; 
     Receive= None;
     Originate = None;
     Prefs = None;
     Fail = Some InconsistentPrefs};

    {Name= "DClarge3";
     Explanation="Waypoint through spines with preference and backup (should fail due to valleys)";
     Topo= tDatacenterLarge;
     Rf= rDatacenterLarge3; 
     Receive= None;
     Originate = None;
     Prefs = None;
     Fail = Some InconsistentPrefs};

    {Name= "BrokenTriangle1";
     Explanation="Inconsistent path suffixes (should fail)";
     Topo= tBrokenTriangle;
     Rf= rBrokenTriangle1; 
     Receive= None;
     Originate = None;
     Prefs = None;
     Fail = Some NoPathForRouters};

    {Name= "BigDipper1";
     Explanation="Must choose the correct preference";
     Topo= tBigDipper;
     Rf= rBigDipper1; 
     Receive= Some [("E", "D"); ("A", "E"); ("C", "A")];
     Originate = Some ["D"];
     Prefs = Some [("A","E","D")];
     Fail = None};

    {Name= "BadGadget";
     Explanation="Total ordering prevents instability (should fail)";
     Topo= tBadGadget;
     Rf= rBadGadget1; 
     Receive= None;
     Originate = None;
     Prefs = None;
     Fail = Some InconsistentPrefs};

    {Name= "OkGadget";
     Explanation="Must find correct total ordering";
     Topo= tBadGadget;
     Rf= rBadGadget2; 
     Receive= Some [("A", "D"); ("B", "D"); ("C", "D")];
     Originate = Some ["D"];
     Prefs = Some [("A", "D", "C"); ("B", "D", "A"); ("C", "D", "B")];
     Fail = None};

    {Name= "Seesaw1";
     Explanation="Must get all best preferences (should fail)";
     Topo= tSeesaw;
     Rf= rSeesaw1; 
     Receive= None;
     Originate = None;
     Prefs = None;
     Fail = Some InconsistentPrefs};

    (* Begin inter-domain tests *)

    {Name= "StretchingMan1";
     Explanation="Prefer one AS over another";
     Topo= tStretchingManWAN;
     Rf= rStretchingManWAN1; 
     Receive= Some [("C", "D"); ("A", "C"); ("B", "C")];
     Originate = Some [];
     Prefs = Some [("D", "Y", "Z")];
     Fail = None};

    {Name= "StretchingMan2";
     Explanation="Using peer not listed in the topology";
     Topo= tStretchingManWAN;
     Rf= rStretchingManWAN2; 
     Receive= Some [];
     Originate = Some [];
     Prefs = Some [];
     Fail = None};
]

let rand = System.Random()

let randomRange () = 
    let lo = uint32 (rand.Next ())
    let hi = uint32 (rand.Next ()) + lo
    (lo,hi)

let randomPrefix () = 
    let (lo,hi) = randomRange ()
    Prefix.toPrefixes [(lo,hi)]

let randomPrefixes n = 
    let num = 1 + (rand.Next() % n)
    let mutable prefixes = []
    for i = 0 to num do 
        prefixes <- (randomPrefix ()) :: prefixes
    prefixes

(* Randomized tests that check that converting a prefix 
   to a range-based representation and back are inverse functions *)
let testPrefixes () =
    printfn "Testing prefix predicates..."
    for i = 1 to maxTests do 
        let (lo,hi) = randomRange ()
        let ps = Prefix.toPrefixes [(lo,hi)]
        let rs = 
            List.map Prefix.toPredicate [ps]
            |> List.fold (fun acc r -> Prefix.disj r acc) []
        if List.length rs <> 1 || List.head rs <> (lo,hi) then
            printfn "[Failed]: expected: %A, but got %A" (lo,hi) (List.head rs)

(* Randomized tests that check that the scope merging cross product 
   construction never introduces new prefixes not specified originally *)
let testScopeMerging () = 
    printfn "Testing prefix compaction..."
    let allPrefixes ccs = 
        ccs
        |> List.map (fun (ps,_) -> Set.ofList ps)
        |> List.fold Set.union Set.empty
    for i = 0 to (maxTests / 10) do
        let scope1 = 
            randomPrefixes 10
            |> List.map (fun ps -> (ps,[Ast.Empty]))
            |> List.append [(Prefix.toPrefixes Prefix.top, [Ast.Empty])]
            |> Ast.makeDisjointPairs ""
        let scope2 = 
            randomPrefixes 10
            |> List.map (fun ps -> (ps,[Ast.Empty]))
            |> List.append [(Prefix.toPrefixes Prefix.top, [Ast.Empty])]
            |> Ast.makeDisjointPairs ""
        let combined = Ast.combineConstraints scope1 scope2 Ast.OInter
        let compact = Ast.makeCompactPairs combined
        let origPrefixes = Set.union (allPrefixes scope1) (allPrefixes scope2)
        let currPrefixes = allPrefixes compact
        if not (Set.isSubset currPrefixes origPrefixes) then 
            printfn "[Failed]: Compacted prefixes are not a subset"
        let (ps,_) = List.head (List.rev compact)
        if Prefix.toPredicate ps <> Prefix.top then 
            printfn "[Failed]: Last test not true"

(* Compiles various examples and ensures that they either don't compile,
   or they compile and the resulting configuration is correct *)
let testCompilation() =
    printfn "Testing compilation..."
    printfn "----------------------------------------------------------"
    let settings = Args.getSettings ()
    let longest = List.maxBy (fun t -> t.Name.Length) tests
    let longest = longest.Name.Length
    for test in tests do
        let spaces = String.replicate (longest - test.Name.Length + 3) " "
        let msg = String.Format("{0}{1}{2}", test.Name, spaces, test.Explanation)
        printfn "%s" msg
        logInfo0("\n" + msg)
        let reb = Regex.REBuilder(test.Topo)
        let built = test.Rf reb
        match IR.compileToIR reb built (settings.DebugDir + test.Name) with 
        | Err(x) ->
            if (Option.isSome test.Receive || 
                Option.isSome test.Originate || 
                Option.isSome test.Prefs || 
                Option.isNone test.Fail) then 
                let msg = String.Format("\n[Failed]:\n  Name: {0}\n  Message: Should compile but did not\n  Error: {1}\n", test.Name, x)
                printfn "%s" msg
                logInfo0(msg)
            match test.Fail, x with 
            | Some NoPathForRouters, IR.NoPathForRouters _ -> ()
            | Some InconsistentPrefs, IR.InconsistentPrefs _ -> ()
            | _ ->
                let msg = String.Format("\n[Failed]:\n  Name: {0}\n  Message: Expected Error {1}\n", test.Name, test.Fail)
                printfn "%s" msg
                logInfo0(msg)
        | Ok(config) -> 
            if (Option.isNone test.Receive || 
                Option.isNone test.Originate || 
                Option.isNone test.Prefs || 
                Option.isSome test.Fail) then
                let msg = String.Format("\n[Failed]:\n  Name: {0}\n  Message: Should not compile but did\n", test.Name)
                printfn "%s" msg
                logInfo0(msg)
            else
                (* Check receiving from peers *)
                let rs = Option.get test.Receive
                for (x,y) in rs do 
                    if not (receiveFrom config x y) then
                        let msg = String.Format("\n[Failed]: ({0}) - {1} should receive from {2} but did not\n", test.Name, x, y)
                        printfn "%s" msg
                        logInfo0(msg)
                
                (* Check originating routes *)
                let os = Option.get test.Originate
                for x in os do 
                    if not (originates config x) then 
                        let msg = String.Format("\n  [Failed]: ({0}) - {1} should originate a route but did not", test.Name, x)
                        printfn "%s" msg
                        logInfo0(msg)

                (* Test preferences *)
                let ps = Option.get test.Prefs
                for (x,a,b) in ps do
                    if not (prefersPeer config x (a,b)) then 
                        let msg = String.Format("\n[Failed]: ({0}) - {1} should prefer {2} to {3} but did not", test.Name, x, a, b)
                        printfn "%s" msg
                        logInfo0(msg)


let run () =
    printfn ""
    testPrefixes ()
    testScopeMerging ()
    testCompilation ()