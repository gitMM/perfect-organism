using System;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class FlightRegression
{
    static void Check(bool pass,string message){if(!pass)throw new Exception(message);}
    [Serializable] class RecordedEvent
    {
        public string kind,detail; public float time; public FlightPlanner.Decision data;
        public FlightPlanner.Model player; public Vector3 sphere;
    }
    // Run separately against an existing human JSONL session, including v2 recordings.
    public static void ReplayRecordedDeaths()
    {
        var review=new DeathReview();int reviewed=0;
        foreach(string line in System.IO.File.ReadLines(Environment.GetEnvironmentVariable("SPHERE_REPLAY_LOG"))){
            var row=JsonUtility.FromJson<RecordedEvent>(line);
            if(row.kind=="round-start")review.Reset();
            if(row.kind=="plan" && row.data.route.Count>0)
                review.Begin(row.data,row.data.choices[row.data.route[0]].from);
            if(row.kind=="observation" || row.kind=="outcome")review.Record(row.time,row.player,row.sphere);
            if(row.kind!="outcome" || row.detail!="Rail hit" || review.plan==null)continue;
            var frames=review.frames;
            Check(frames.Count>1,"Review is missing aligned observations");
            var last=frames[frames.Count-1];
            if(row.time-review.plan.time<=review.plan.forecast[review.plan.forecast.Count-1].t){
                Check(Mathf.Abs(last.t+review.plan.time-row.time)<.001f && last.eye==row.player.eye && last.aim==row.player.aim,
                    "Replay did not align the recorded rail kill to the actual pose");
            }
            reviewed++;Debug.Log($"Recorded rail kill: decision {review.plan.id}, samples {frames.Count}, first difference {review.firstDifference}, final aim offset {last.aimError:F1} degrees.");
        }
        Check(reviewed>0,"Fixture contains no comparable rail kills");
        Debug.Log($"Reviewed {reviewed} recorded human rail kills.");
    }
    // Counterfactual flight with the recorded player's observations held fixed.
    public static void ReplayCornerApproach()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);Hangar.Make();
        var planner=new FlightPlanner();
        Vector3 pos=default,velocity=default;
        int steps=0,oldCorner=0,newCorner=0,oldExposed=0,newExposed=0;
        foreach(var line in System.IO.File.ReadLines(Environment.GetEnvironmentVariable("SPHERE_REPLAY_LOG"))){
            var row=JsonUtility.FromJson<RecordedEvent>(line);
            if(row.kind!="plan" || row.data.id<50 || row.data.id>86)continue;
            var old=row.data;var recorded=old.choices[old.route[0]];
            if(steps++==0){pos=recorded.from;velocity=recorded.startVelocity;}
            int anchor=planner.Graph.NearestVisible(pos);
            var plan=planner.Plan(old.id,old.time,pos,anchor,old.observed,velocity,old.escapeEnabled,old.alternateEnabled,old.alternateRate);
            var edge=plan.choices[plan.route[0]];
            if(plan.continuation.Count>0){
                var end=plan.continuation[plan.continuation.Count-1];
                Check(!end.exposed || plan.escapeResult=="Contact in extension","Certified an exposed refuge");
            }
            if(recorded.to.x< -15 && recorded.to.z< -12)oldCorner++;
            if(edge.to.x< -15 && edge.to.z< -12)newCorner++;
            if(SphereSight.Exposed(old.observed.eye,recorded.to,out _))oldExposed++;
            if(SphereSight.Exposed(old.observed.eye,edge.to,out _))newExposed++;
            pos=edge.to;velocity=edge.endVelocity;
            if(old.id<=55)Debug.Log($"Approach {old.id}: old {recorded.to} revised {pos}, {plan.reason}, {plan.escapeResult}");
            if(plan.forecast.Count>0 && edge.contact)break;
        }
        Check(steps>0,"No matching approach in replay");
        Debug.Log($"Corner replay over {steps} decisions: old corner {oldCorner}, revised {newCorner}, exposed endpoints old {oldExposed}, revised {newExposed}.");
    }
    public static void ReplayContactAttempts()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);Hangar.Make();
        var planner=new FlightPlanner();var check=new ContactCheck();int tested=0;
        foreach(var line in System.IO.File.ReadLines(Environment.GetEnvironmentVariable("SPHERE_REPLAY_LOG"))){
            if(!line.Contains("\"reason\":\"Predicted safe contact\""))continue;
            var old=JsonUtility.FromJson<RecordedEvent>(line).data;
            var path=old.route.ConvertAll(i=>old.choices[i]);var start=path[0];
            check.Begin(old.observed,old.alternateEnabled?old.alternateRate:2.6f);
            var veto=check.Verify(path,old.forecast[old.forecast.Count-1].t);
            int anchor=planner.Graph.NearestVisible(start.from);
            var revised=planner.Plan(old.id,old.time,start.from,anchor,old.observed,start.startVelocity,old.escapeEnabled,old.alternateEnabled,old.alternateRate);
            Debug.Log($"Contact replay {old.id}: original {veto??"passed"}, revised {revised.reason}, rejected {revised.contactsVetoed}/{revised.contactsChecked}.");
            tested++;
        }
        Check(tested>0,"No recorded approaches to verify");
    }

    static void WalkRoute(Vector3 start, Vector3 end, float dt)
    {
        var model=new FlightPlanner.Model{eye=start};
        Vector3 displacement=end-start;
        displacement.y=0;
        int count=Mathf.CeilToInt(displacement.magnitude/(6*dt));
        for(int i=0;i<count;i++){
            model.eye=PlayerMotion.Slide(model.eye,displacement/count);
            PlayerMotion.Vertical(ref model,dt);
        }
        Check(Vector3.Distance(model.eye,end)<.08f,$"Walk from {start} to {end} finished at {model.eye}, dt {dt}");
    }

    static void CheckUpperCover()
    {
        var graph=new FlightGraph(FlightGrid.Hangar,FlightPlanner.Radius);
        var pending=new System.Collections.Generic.Queue<int>();
        var reachable=new System.Collections.Generic.HashSet<int>();
        int start=graph.NearestVisible(graph.Spawn(new(6,3.75f,6)));
        pending.Enqueue(start);reachable.Add(start);
        while(pending.Count>0){
            foreach(int next in graph.links[pending.Dequeue()])
                if(reachable.Add(next))pending.Enqueue(next);
        }
        foreach(int side in new[]{-1,1}){
            float z=side*6;
            CheckPocket(new(side*19.5f,(side<0?5.4f:7.1f)+.8f,z),new(side*13,4.51f,z),"Service screen");
            CheckPocket(new(side*8,10.48f,z),new(side*8,1.5f,z),"Overhead air handler");
        }
        void CheckPocket(Vector3 pocket,Vector3 eye,string blocker){
            foreach(int id in reachable){
                Vector3 node=graph.nodes[id];
                if((node-pocket).sqrMagnitude>4 || !Physics.Linecast(eye,node,out var hit,1<<8))continue;
                if(hit.collider.name==blocker && !SphereSight.Exposed(eye,node,out _))return;
            }
            throw new Exception($"No reachable full-body cover behind {blocker} near {pocket}");
        }
        Debug.Log("Both wall galleries and both ceiling pockets contain reachable, fully concealed nodes.");
    }

    static void WalkHangar()
    {
        foreach(float dt in new[]{1f/120,1f/30,.0625f}){
            foreach(int side in new[]{-1,1}){
                Vector3 foot=new(side*15,1.5f,-10.4f), landing=new(side*15,side<0?3.5f:4.5f,-2.8f);
                WalkRoute(foot,landing,dt);
                WalkRoute(landing,foot,dt);
                WalkRoute(new(side*6,1.5f,-3),new(side*6,1.5f,3),dt);
            }
            Vector3 dockFoot=new(17.5f,1.5f,15),dockTop=new(11.5f,4.7f,15);
            WalkRoute(dockFoot,dockTop,dt);
            WalkRoute(dockTop,dockFoot,dt);
        }
        Debug.Log("Walked both terrace stairs and the dock in both directions, and through both gantries.");
    }

    public static void ShotBait()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);Hangar.Make();
        var planner=new FlightPlanner();
        var player=new FlightPlanner.Model{eye=new Vector3(-3,1.51f,-15),aim=Quaternion.Euler(0,45,0)*Vector3.forward};
        var start=planner.Graph.Spawn(new Vector3(-2,.5f,-2.8f));
        int anchor=planner.Graph.NearestVisible(start);
        foreach(float ready in new[]{0f,FlightPlanner.RailRecharge}){
            player.ready=ready;
            var plan=planner.Plan(1,0,start,anchor,player,Vector3.zero);
            Check(plan.search.source=="GOAP" && plan.route.Count>0 && plan.search.attempted>1,"Cover or reload decision bypassed search");
            var first=plan.choices[plan.route[0]];
            Check(plan.search.expansions.Exists(e=>e.fromEdge<0 && plan.route[0]>=e.firstChild &&
                plan.route[0]<e.firstChild+e.childCount),"Chosen opening outside searched actions");
            Debug.Log($"Cover case ready={ready} chose {FlightPlanner.DescribeInput(first.input)}, {plan.reason}, {plan.route.Count} legs");
        }
        var starts=new System.Collections.Generic.HashSet<Vector3>();
        var enemies=new System.Collections.Generic.HashSet<Vector3>();
        for(int trial=0;trial<16;trial++){
            Check(planner.Graph.RandomStarts(CargoPhysics.Layout(),out var eye,out var enemy),"Random start unavailable");
            Check(Vector3.Distance(eye,enemy)>8 && Physics.Raycast(eye,Vector3.down,1.6f,1<<8),"Invalid random start");
            starts.Add(eye);enemies.Add(enemy);
        }
        Check(starts.Count>1 && enemies.Count>1,"Round starts did not vary");
    }

    public static void RearPursuit()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);Hangar.Make();
        var planner=new FlightPlanner();
        var shell=new SphereEnvelope(planner.Graph.surface,FlightPlanner.Radius,GameObject.CreatePrimitive(PrimitiveType.Sphere));
        // Decision 293 in the human hangar session: the sphere was on a pillar.
        var player=new FlightPlanner.Model{eye=new Vector3(2.5048f,1.5015f,-9.35915f),
            aim=new Vector3(.504517f,-.139692f,.852026f).normalized};
        Vector3 position=new(-7.555f,7.06774f,-9.99229f),velocity=Vector3.zero;
        bool caught=false;
        shell.Update(position,player.eye,default,0,true);
        for(int tick=0;tick<48 && !caught;tick++){
            var plan=planner.Plan(tick+1,tick*FlightPlanner.Step,position,planner.Graph.NearestVisible(position),player,velocity,true,true,2.6f,default,true);
            Check(plan.search.source=="GOAP","Rear opening bypassed search");
            Check(plan.route.Count>0,"Rear pursuit stopped");
            var first=plan.choices[plan.route[0]];
            for(int i=1;i<=16 && !caught;i++){
                position=FlightPlanner.FlightPoint(first,i*FlightPlanner.Step/16);
                shell.Update(position,player.eye,default,FlightPlanner.Step/16);
                caught=shell.Touches(player.eye);
            }
            velocity=first.endVelocity;
            for(int i=0;i<15;i++)planner.RefreshGuides();
            if(caught)Debug.Log($"Rear pursuit reached player from recorded pillar in {tick+1} legs.");
        }
        Check(caught,$"Rear attack failed to make contact; last position {position}");
        player.aim=(position-player.eye).normalized;
        var facing=planner.Plan(50,12,position,planner.Graph.NearestVisible(position),player,velocity,true,true,2.6f,default,true);
        Check(facing.search.attempted>0,"Facing-player decision bypassed search");
    }

    public static void Run()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
        var wall=GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.layer=8;wall.transform.position=new Vector3(-1,0,5);wall.transform.localScale=new Vector3(2,4,1);
        Physics.SyncTransforms();
        Vector3 eye=Vector3.zero,center=new(-.15f,0,10);
        Check(Physics.Linecast(eye,center,1<<8),"Fixture must hide the center");
        Check(SphereSight.Exposed(eye,center,out var rim),"Visible rim incorrectly marked safe");
        Check(SphereSight.Rail(eye,rim.normalized,center,out _),"Predicted aim point must hit visible rim");
        Check(!SphereSight.Rail(eye,center.normalized,center,out _),"Center shot must stop at cover");
        Check(!SphereSight.Exposed(eye,new Vector3(-1.8f,0,10),out _),"Body behind wall must be concealed");
        var leg=new FlightPlanner.Edge{from=Vector3.zero,startVelocity=Vector3.forward*6,endVelocity=Vector3.right*8};
        Vector3 next=FlightPlanner.FlightPoint(leg,FlightPlanner.Step);
        Check((next-new Vector3(1,0,.75f)).magnitude<.0001f,"Incorrect integrated endpoint");
        var continuation=new FlightPlanner.Edge{from=next,startVelocity=leg.endVelocity,endVelocity=Vector3.up*8};
        const float h=.0001f;
        var incoming=(next-FlightPlanner.FlightPoint(leg,FlightPlanner.Step-h))/h;
        var outgoing=(FlightPlanner.FlightPoint(continuation,h)-next)/h;
        Check((incoming-outgoing).magnitude<.02f,"Velocity must be continuous across decisions");
        var camera=new GameObject("Test camera").transform;
        Vector3 subject=new Vector3(2,1,-3);
        var ending=new EndingView(camera,subject);
        ending.Apply(camera,0);
        Check(camera.position==Vector3.zero && Quaternion.Angle(camera.rotation,Quaternion.identity)<.01f,"Camera must start at the shot pose");
        ending.Apply(camera,.375f);
        Check(camera.forward.x>0 && Vector3.Angle(camera.forward,subject-camera.position)>1,"Camera should be turning midway");
        ending.Apply(camera,.75f);
        Check(Vector3.Angle(camera.forward,subject-camera.position)<.05f,"Final camera must face the sphere");
        var forecast=new FlightPlanner.Decision{id=11,time=2,reason="Survives horizon",observed=new FlightPlanner.Model{eye=eye,aim=Vector3.forward}};
        forecast.choices.Add(new FlightPlanner.Edge{from=center});forecast.route.Add(0);
        forecast.forecast.Add(new FlightPlanner.Sample{eye=Vector3.right,aim=Vector3.forward,alternateAim=Vector3.forward,sphere=center,t=.25f});
        var review=new DeathReview();review.Begin(forecast,center);
        review.Record(2.125f,new FlightPlanner.Model{eye=Vector3.right*.5f,aim=Vector3.right},center);
        review.Begin(new FlightPlanner.Decision{reason="Delay predicted hit"},center);
        review.Record(3,forecast.observed,center);
        Check(review.plan==forecast && review.frames.Count==2 && review.firstDifference==1,"Replay lost its last safe decision or exceeded its horizon");
        Check(review.frames[1].eyeError<.001f && review.frames[1].aimError>89,"Replay compared different times");
        var near=Vector3.forward*10;
        wall.SetActive(false);Physics.SyncTransforms();
        var offsetAim=Quaternion.AngleAxis(20,Vector3.up)*Vector3.forward;
        var slow=FlightPlanner.Track(offsetAim,near,near,true,2.6f,.0625f);
        var fast=FlightPlanner.Track(offsetAim,near,near,true,9,.0625f);
        Check(!SphereSight.Rail(eye,slow,near,out _) && SphereSight.Rail(eye,fast,near,out _),"Alternate response failed to detect a faster turn");
        Check(FlightPlanner.DirectCharge(Vector3.forward,Vector3.forward*10) &&
            !FlightPlanner.DirectCharge(Vector3.right,Vector3.forward*10) &&
            !FlightPlanner.DirectCharge(Vector3.back,Vector3.forward*10),"Direct charge classification");
        Check(FlightPlanner.MissChance(2,12,100,.75f)<.25f &&
            FlightPlanner.MissChance(2,12,100,.0625f)>.7f,"Sustained exposure must outlast the snap-shot opportunity");
        float steady=FlightPlanner.MissChance(4,1,5);
        Check(FlightPlanner.MissChance(4,1,80)>steady && FlightPlanner.MissChance(4,15,5)>steady &&
            FlightPlanner.MissChance(1,1,5)>steady && FlightPlanner.MissChance(18,1,5)<.01f,
            "Shot difficulty lost its angular motion, acquisition or apparent size response");
        Debug.Log("Death replay and alternate aim regressions passed.");
        // Place these cases over the hangar floor so all four player trajectories use live collision.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);Hangar.Make();
        WalkHangar();
        CheckUpperCover();
        var check=new ContactCheck();
        var player=new FlightPlanner.Model{eye=new Vector3(0,1.51f,-14),aim=Vector3.forward};
        var body=player.eye-Vector3.up*.45f;
        var approach=new FlightPlanner.Edge{from=body+Vector3.forward*2,startVelocity=Vector3.back*12,endVelocity=Vector3.back*12};
        var path=new System.Collections.Generic.List<FlightPlanner.Edge>{approach};
        check.Begin(player,3);Check(check.Verify(path,.1875f)=="Rail can intercept","Frontal armed approach accepted");
        player.ready=.5f;check.Begin(player,3);Check(check.Verify(path,.1875f)==null,"Clear reload opportunity rejected");
        player.ready=.0625f;check.Begin(player,3);Check(check.Verify(path,.1875f)!=null,"Ignored cooldown expiring during approach");
        player.ready=0;player.aim=Vector3.back;check.Begin(player,3);
        Check(check.Verify(path,.1875f)==null,"Short rear contact against a ready rail rejected");
        player.ready=1;approach.from=body+Vector3.forward*4;approach.startVelocity=approach.endVelocity=Vector3.back*8;
        path.Add(new FlightPlanner.Edge{from=body+Vector3.forward*2,startVelocity=Vector3.back*8,endVelocity=Vector3.back*8});
        check.Begin(player,3);Check(check.Verify(path,.5f)=="Player can sidestep","Moving target was assumed stationary");
        CheckGraphAndBody();
        SurfaceRegression.Run();
        ShotBait();
        RearPursuit();
        Debug.Log("Contact confidence regressions passed.");
        Debug.Log("Ending camera regression passed.");
        Debug.Log("Flight regressions passed: edge peek, covered center, full concealment, curved flight and velocity continuity.");
    }
    static void CheckGraphAndBody()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
        var grid=new FlightGrid {firstNode=new Vector3(30,2,20),spacing=Vector3.one*2,counts=new Vector3Int(3,2,4)};
        var body=new FlightBody {radius=1,speed=6,acceleration=8};
        ArenaGeometry.Block("Test surface",new Vector3(32,.5f,23),new Vector3(8,1,10),Color.gray);
        Physics.SyncTransforms();
        var graph=new FlightGraph(grid,body.radius);
        Check(graph.nodes.Count>0 && graph.NearestVisible(graph.Spawn(grid.firstNode+Vector3.right*.1f))>=0,"Map grid or off-grid spawn failed");
        Vector3 eye=new(0,2,0),target=new(.7f,2,5);
        Check(!SphereSight.Rail(eye,Vector3.forward,target,out _) && SphereSight.Rail(eye,Vector3.forward,target,out _,body.radius),"Radius did not affect rail");
        Check(Mathf.Abs(body.BrakingOffset(Vector3.forward*5,.25f).magnitude-1.625f)<.0001f,"Multi-step braking distance is wrong");
    }
}
