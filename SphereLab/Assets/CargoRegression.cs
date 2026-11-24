using System;

using UnityEngine;

public static class CargoRegression
{
    static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
    [Serializable] sealed class Record{public FlightPlanner.Decision data;}
    public static void ReplayLethality()
    {
        var history=new System.Collections.Generic.List<FlightPlanner.Decision>();
        foreach(string line in System.IO.File.ReadLines(Environment.GetEnvironmentVariable("SPHERE_REPLAY_LOG"))){
            if(line.StartsWith("{\"kind\":\"round-start\""))history.Clear();
            if(!line.StartsWith("{\"kind\":\"plan\""))continue;
            var plan=JsonUtility.FromJson<Record>(line).data;
            history.Add(plan);
            if(plan.route.Count==0)continue;
            var first=plan.choices[plan.route[0]];
            if(first.cratePush && plan.crate.Get(first.pushedCrate).asleep &&
                plan.route.Exists(i=>plan.choices[i].crush && plan.choices[i].impactCrate==first.pushedCrate))break;
        }
        Check(history.Count>0,"No recorded plans");
        var target=history[history.Count-1];
        var chosen=target.choices[target.route[0]];
        Check(chosen.cratePush && target.crate.Get(chosen.pushedCrate).asleep &&
            target.route.Exists(i=>target.choices[i].crush && target.choices[i].impactCrate==chosen.pushedCrate),"Missing recorded winning push");
        using var cargo=new CargoPhysics();
        var graph=new FlightPlanner().Graph;
        var visual=GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var shape=new SphereEnvelope(graph.surface,FlightPlanner.Radius,visual);
        FlightPlanner.Decision Run(bool lethal,bool realtime){
            var trial=new FlightPlanner(graph,FlightBody.Default){Cargo=cargo,Envelope=shape};
            FlightPlanner.Decision result=null;
            for(int n=0;n<history.Count;n++){
                var old=history[n];
                var edge=old.choices[old.route[0]];
                int anchor=graph.NearestVisible(edge.from);
                if(n==0)shape.Update(edge.from,old.observed.eye,old.crate,0,true);
                // Reconstruct the probe's unbudgeted prefix in both arms.
                // Intervene only at the frozen decision under comparison.
                trial.PredictLethalImpact=old!=target || lethal;
                result=trial.Plan(old.id,old.time,edge.from,anchor,old.observed,edge.startVelocity,
                    old.escapeEnabled,old.alternateEnabled,old.alternateRate,old.crate,old==target && realtime);
                if(old!=target){
                    // This stationary-player fixture uses 60 Hz. Preserve body
                    // deformation over the recorded leg, including its lookahead.
                    const float dt=1f/60;
                    for(int frame=1;frame<=15;frame++){
                        float t=frame*dt;
                        var eye=Vector3.Lerp(old.observed.eye,history[n+1].observed.eye,t/FlightPlanner.Step);
                        shape.Update(FlightPlanner.FlightPoint(edge,t),eye,old.crate,dt,
                            upcoming:FlightPlanner.FlightPoint(edge,t+.12f));
                    }
                }
            }
            Check(result.route.Count>0,"Planner returned no first action");
            return result;
        }
        void Pair(string kind,int index,bool sham,bool realtime){
            bool reverse=index%2==1;
            var first=Run(sham || !reverse,realtime);
            var second=Run(sham || reverse,realtime);
            var on=reverse?second:first;var off=reverse?first:second;
            var a=on.choices[on.route[0]];var b=off.choices[off.route[0]];
            float separation=0;
            for(int tick=1;tick<=16;tick++){
                float t=tick*FlightPlanner.Step/16;
                separation=Mathf.Max(separation,Vector3.Distance(FlightPlanner.FlightPoint(a,t),FlightPlanner.FlightPoint(b,t)));
            }
            bool impact(FlightPlanner.Decision d)=>d.route.Exists(i=>d.choices[i].crush);
            bool sawImpact(FlightPlanner.Decision d)=>d.choices.Exists(e=>e.impactCrate>=0);
            Check(sham || !impact(off),"Nonlethal arm rewarded a crate impact");
            Debug.Log($"LethalityPair {kind} {index} decision={target.id} push={a.cratePush}/{b.cratePush} win={impact(on)}/{impact(off)} sampledImpact={sawImpact(on)}/{sawImpact(off)} expanded={on.expanded}/{off.expanded} pathDelta={separation:F4}");
        }
        Pair("full",0,false,false);
        Pair("warmup",0,false,true);
        for(int i=0;i<24;i++){
            Pair("5ms",i,false,true);
            if(i%2==0)Pair("sham",i/2,true,true);
        }
        Debug.Log("Paired lethality replay complete");
    }
    public static void ReplayInteractions()
    {
        using var physics=new CargoPhysics();
        var planner=new FlightPlanner{Cargo=physics};
        var shape=new SphereEnvelope(planner.Graph.surface,FlightPlanner.Radius,GameObject.CreatePrimitive(PrimitiveType.Sphere));
        planner.Envelope=shape;
        bool liveFixture=false,choseImpact=false,choseDisplacement=false,actualImpact=false,changedChoice=false,committedPush=false;
        foreach(var line in System.IO.File.ReadLines(Environment.GetEnvironmentVariable("SPHERE_REPLAY_LOG"))){
            if(line.Contains("\"kind\":\"round-start\"") && line.Contains("\"detail\":\"crate probe\""))liveFixture=true;
            if(line.Contains("\"kind\":\"outcome\"") && line.Contains("\"detail\":\"Crate impact\""))actualImpact=true;
            if(!line.StartsWith("{\"kind\":\"plan\""))continue;
            var old=JsonUtility.FromJson<Record>(line).data;
            if((!liveFixture && (old.id<540 || old.id>551)) || old.route.Count==0)continue;
            var first=old.choices[old.route[0]];
            committedPush|=first.cratePush && old.crate.Get(first.pushedCrate).asleep &&
                old.route.Exists(i=>old.choices[i].crush) && old.choices.Exists(e=>e.parent<0 && !e.cratePush && e.firstShot<0);
            int anchor=planner.Graph.NearestVisible(first.from);
            FlightPlanner.Decision Replay(CargoPhysics.State boxes,FlightPlanner.Model player){
                var trial=new FlightPlanner(planner.Graph,FlightBody.Default){Cargo=physics,Envelope=shape};
                shape.Update(first.from,player.eye,boxes,0,true);
                return trial.Plan(old.id,old.time,first.from,anchor,player,first.startVelocity,old.escapeEnabled,old.alternateEnabled,old.alternateRate,boxes);
            }
            var with=Replay(old.crate,old.observed);
            bool wins=with.route.Exists(i=>with.choices[i].crush);
            int push=with.route.FindIndex(i=>with.choices[i].cratePush);
            if(!wins && push<0)continue;
            var without=Replay(default,old.observed);
            Check(!without.route.Exists(i=>without.choices[i].cratePush || without.choices[i].crush),"Interaction occurred without a crate");
            bool alternative=with.choices.Exists(e=>e.parent<0 && !e.cratePush && e.firstShot<0);
            choseImpact|=wins;
            if(push>=0 && wins && old.crate.Get(with.choices[with.route[push]].pushedCrate).asleep && alternative){
                choseDisplacement=true;
                var away=old.observed;away.eye=new Vector3(0,1.51f,-14);
                var control=Replay(old.crate,away);
                bool pushes=control.route.Exists(i=>control.choices[i].cratePush);
                changedChoice|=control.route.Count>0 && !pushes;
                Debug.Log($"Player outside landing area: {control.reason}, selected route pushed {pushes}");
            }
            Debug.Log($"Interaction replay {old.id}: chosen push leg {push}, winning impact {wins}, safe non-push available {alternative}, without cargo {without.reason}");
        }
        Check(choseImpact && actualImpact,"Expected both a forecast impact and a recorded live impact");
        if(liveFixture)Check(committedPush && choseDisplacement && changedChoice,"Expected a committed live push, a replayed winning interaction and a different choice outside the landing area");
    }
    [Serializable] sealed class ShotRecord{
        public int decision;public float time;public Vector3 sphere;
        public CargoPhysics.State crate;public FlightPlanner.Model player;
    }
    public static void ReplayDecisionFixes()
    {
        using var cargo=new CargoPhysics();
        var planner=new FlightPlanner{Cargo=cargo};
        var envelope=new SphereEnvelope(planner.Graph.surface,FlightPlanner.Radius,GameObject.CreatePrimitive(PrimitiveType.Sphere));
        planner.Envelope=envelope;
        int misses=0,attacks=0;bool outerHit=false;
        FlightPlanner.Decision previous=null;
        foreach(var line in System.IO.File.ReadLines(Environment.GetEnvironmentVariable("SPHERE_REPLAY_LOG"))){
            if(line.StartsWith("{\"kind\":\"plan\"")){
                previous=JsonUtility.FromJson<Record>(line).data;
                continue;
            }
            if(!line.StartsWith("{\"kind\":\"rail\""))continue;
            var shot=JsonUtility.FromJson<ShotRecord>(line);
            envelope.Update(shot.sphere,shot.player.eye,shot.crate,0,true);
            if(shot.decision==311){
                outerHit=envelope.Current.Intercepts(shot.player.eye,shot.player.aim,shot.crate) &&
                    envelope.Rail(shot.player.eye,shot.player.aim,shot.crate,out _) &&
                    !SphereSight.Rail(shot.player.eye,shot.player.aim,shot.sphere,out _,FlightPlanner.Radius,shot.crate);
            }
            if(shot.decision<600 || previous.reason!="Bait: reposition")continue;
            var edge=previous.choices[previous.route[0]];
            float t=Mathf.Clamp(shot.time-previous.time,0,FlightPlanner.Step);
            float before=Mathf.Max(0,t-.001f);
            Vector3 velocity=t>before?(FlightPlanner.FlightPoint(edge,t)-FlightPlanner.FlightPoint(edge,before))/(t-before):edge.startVelocity;
            var next=planner.Plan(shot.decision,shot.time,shot.sphere,planner.Graph.NearestVisible(shot.sphere),
                shot.player,velocity,true,true,previous.alternateRate,shot.crate,true);
            Check(next.search.source=="GOAP" && next.route.Count>0,"Missing searched response to a recorded miss");
            var move=next.choices[next.route[0]];
            misses++;if((move.to-shot.player.eye).sqrMagnitude<(move.from-shot.player.eye).sqrMagnitude)attacks++;
        }
        Check(outerHit && misses==5 && attacks>0,"Recorded decision checks failed: "+outerHit+", "+attacks+"/"+misses);
        Debug.Log($"Recorded decisions: forecast the outer-body hit at 311, selected attacks for {attacks}/{misses} reposition misses.");
    }
    public static void ProfileRecordedPlans()
    {
        
        using var cargo=new CargoPhysics();
        var planner=new FlightPlanner{Cargo=cargo};
        planner.Envelope=new SphereEnvelope(planner.Graph.surface,FlightPlanner.Radius,GameObject.CreatePrimitive(PrimitiveType.Sphere));
        var times=new System.Collections.Generic.List<double>();
        int selectedFull=0,recordedFull=0;
        var depths=new int[FlightPlanner.Depth+1];
        var recordedDepths=new int[depths.Length];
        // Optional movement replay chains the selected legs inside each recorded window.
        // Player and cargo observations continue to come from the human session.
        string windows=Environment.GetEnvironmentVariable("SPHERE_MOVEMENT_WINDOWS");
        var movementWindows=new System.Collections.Generic.Dictionary<int,int>();
        if(!string.IsNullOrEmpty(windows))foreach(var span in windows.Split(',')){
            var ends=span.Split('-');movementWindows.Add(int.Parse(ends[0]),int.Parse(ends[1]));
        }
        bool auditCrates=Environment.GetEnvironmentVariable("SPHERE_AUDIT_CRATE_APPROACHES")=="1";
        int windowEnd=-1;
        Vector3 replayPosition=default,replayVelocity=default;
        foreach(string line in System.IO.File.ReadLines(Environment.GetEnvironmentVariable("SPHERE_REPLAY_LOG"))){
            if(line.StartsWith("{\"kind\":\"round-start\"")){
                planner=new FlightPlanner(planner.Graph,FlightBody.Default){Cargo=cargo,Envelope=planner.Envelope};
                continue;
            }
            if(!line.StartsWith("{\"kind\":\"plan\""))continue;
            var prior=JsonUtility.FromJson<Record>(line).data;
            if(prior.route.Count==0)continue;
            var first=prior.choices[prior.route[0]];
            int anchor=prior.search?.startNode??planner.Graph.NearestVisible(first.from);
            if(prior.search!=null && prior.search.startBody.width>0)planner.Envelope.Apply(prior.search.startBody);
            else planner.Envelope.Update(first.from,prior.observed.eye,prior.crate,0,true);
            bool begins=movementWindows.TryGetValue(prior.id,out int last);
            if(begins){windowEnd=last;replayPosition=first.from;replayVelocity=first.startVelocity;}
            bool follow=prior.id<=windowEnd;
            if(follow && !begins){
                anchor=planner.Graph.NearestVisible(replayPosition);
                planner.Envelope.Update(replayPosition,prior.observed.eye,prior.crate,0,true);
            }
            if(auditCrates && prior.crate.present && prior.crate.Get(prior.crate.Nearest(first.from)).Box.SqrDistance(first.from)<16){
                var offline=new FlightPlanner(planner.Graph,FlightBody.Default){Cargo=cargo,Envelope=planner.Envelope};
                var deep=offline.Plan(prior.id,prior.time,first.from,anchor,prior.observed,first.startVelocity,
                    prior.escapeEnabled,prior.alternateEnabled,prior.alternateRate,prior.crate,false);
                Debug.Log($"CrateApproach {prior.id} attempts={deep.search.attempted} depth={deep.search.maxDepth} pushes={deep.choices.FindAll(e=>e.cratePush).Count} safePushes={deep.choices.FindAll(e=>e.cratePush && e.firstShot<0 && string.IsNullOrEmpty(e.contactVeto)).Count} rootPushes={deep.choices.FindAll(e=>e.cratePush && e.parent<0).Count} routePush={deep.route.Exists(i=>deep.choices[i].cratePush)} firstPush={deep.route.Count>0 && deep.choices[deep.route[0]].cratePush} impacts={deep.search.physicalImpacts} selection={deep.reason}");
            }
            var clock=System.Diagnostics.Stopwatch.StartNew();
            var result=planner.Plan(prior.id,prior.time,follow?replayPosition:first.from,anchor,prior.observed,follow?replayVelocity:first.startVelocity,prior.escapeEnabled,prior.alternateEnabled,prior.alternateRate,prior.crate,true);
            if(follow && result.route.Count>0){
                var move=result.choices[result.route[0]];
                Debug.Log($"MovementReplay {prior.id} reason={result.reason} from={move.from:F3} to={move.to:F3} firstShot={move.firstShot:F3} source={result.search.source} move={FlightPlanner.DescribeInput(move.input)} steps={result.route.Count} tested={result.search.attempted} railRejected={result.search.railRejected} selection={result.search.selection} eye={prior.observed.eye:F3} ready={prior.observed.ready:F2} visible={result.forecast.Exists(t=>t.visible)} firstPush={move.cratePush} forecastMissAt={result.forecast.Find(t=>t.forecastMiss).t:F3} missChance={result.forecast.Find(t=>t.forecastMiss).missChance:F2}");
                replayPosition=move.to;replayVelocity=move.endVelocity;
            }else if(follow){
                replayVelocity=Vector3.zero;
                Debug.Log($"MovementReplay {prior.id} reason={result.reason} from={replayPosition:F3} to={replayPosition:F3} source={result.search.source} tested={result.search.attempted} invalid={result.search.invalid} stop={result.search.stop}");
            }
            if(result.search.attempted>0 && prior.search!=null && prior.search.attempted>0){
                if(result.route.Count>=FlightPlanner.Depth && result.search.source=="GOAP")selectedFull++;
                if(prior.route.Count>=FlightPlanner.Depth && prior.search.source=="GOAP")recordedFull++;
                depths[result.search.maxDepth]++;
                recordedDepths[prior.search.maxDepth]++;
            }
            JsonUtility.ToJson(result);
            times.Add(clock.Elapsed.TotalMilliseconds);
            for(int frame=0;frame<15;frame++)planner.RefreshGuides();
        }
        Debug.Log($"Paired search depths 0-{FlightPlanner.Depth}: recorded [{string.Join(",",recordedDepths)}], replay [{string.Join(",",depths)}]");
        Debug.Log($"Selected full GOAP routes in paired searches: recorded {recordedFull}, replay {selectedFull}");
        times.Sort();
        Debug.Log($"Recorded decisions {times.Count}, median {times[times.Count/2]:F1} ms, p95 {times[(int)(times.Count*.95)]:F1} ms, max {times[times.Count-1]:F1} ms");
    }

    public static void ReplayWaits(){
        using var sim=new CargoPhysics();var planner=new FlightPlanner(){Cargo=sim};
        int tested=0,moved=0,safe=0;
        foreach(string line in System.IO.File.ReadLines(Environment.GetEnvironmentVariable("SPHERE_REPLAY_LOG"))){
            if(!line.Contains("\"kind\":\"plan\""))continue;
            var old=JsonUtility.FromJson<Record>(line).data;
            if(!(old.id>=79 && old.id<=93 || old.id>=246 && old.id<=256))continue;
            var e=old.choices[old.route[0]];
            if((e.from-e.to).magnitude>.025f)continue;
            var d=planner.Plan(old.id,old.time,e.from,planner.Graph.NearestVisible(e.from),old.observed,e.startVelocity,
                old.escapeEnabled,old.alternateEnabled,old.alternateRate,old.crate);
            tested++;var first=d.choices[d.route[0]];
            if((first.from-first.to).magnitude>.025f){moved++;if(d.reason=="Survives horizon" || d.reason=="Predicted safe contact")safe++;}
        }
        Debug.Log($"Recorded wait replay: {tested} held decisions, {moved} revised moves, {safe} with full survival or verified contact.");
    }
    static void CheckLayout(CargoPhysics sim){
        var initial=CargoPhysics.Layout();
        Vector3 remote=new(0,1.51f,-15);
        for(int id=0;id<initial.Count;id++){
            var start=initial.Get(id).center;var dir=CargoPhysics.DropDirections[id];
            Vector3 victim=remote;bool crushed=false;
            for(int pass=0;pass<2;pass++){
                var world=initial;bool crossed=false;
                for(int i=0;i<180;i++){
                    var from=start+dir*(-1.6f+i*.11f);
                    var motion=sim.Advance(ref world,victim,victim,from,from+.11f*dir,.48f,1f/60);
                    if(pass==1 && motion.crushed){Check(motion.crushId==id,"Wrong crate attribution");crushed=true;break;}
                    if(pass==0 && world.Get(id).center.y<2.48f){
                        victim=world.Get(id).center;victim.y=1.51f;crossed=true;break;
                    }
                }
                if(pass==0)Check(crossed,"No fall at "+CargoPhysics.SiteNames[id]);
            }
            Check(crushed,"No player impact at "+CargoPhysics.SiteNames[id]);
            Check(initial.Get(id).center==start,"Branch mutated source snapshot");
            Debug.Log("Drop test passed: "+CargoPhysics.SiteNames[id]);
        }
        var pair=CargoPhysics.At(new(0,.7f,-10));pair.asleep=true;
        pair.extra=new[]{new CargoPhysics.Item{center=new(1.55f,.7f,-10),asleep=true}};
        for(int i=0;i<24;i++){
            Vector3 from=new(-1.25f+i*.08f,.7f,-10);
            sim.Advance(ref pair,remote,remote,from,from+Vector3.right*.08f,.48f,1f/60);
        }
        Check(pair.extra[0].center.x>1.8f,"Neighboring crates failed to collide");
        var shield=CargoPhysics.At(new(10,2,-10));
        shield.extra=new[]{new CargoPhysics.Item{center=new(0,2,-10)}};
        Check(!SphereSight.Rail(new(0,2,-13),Vector3.forward,new(0,2,-7),out _,.48f,shield),"Secondary crate did not block rail");
        Debug.Log("Verified multiple crates collide, block rail and preserve branch-local state.");
    }
    public static void Run()
    {

        using var sim=new CargoPhysics();
        Vector3 remote=new(-16,1.51f,12);
        var state=CargoPhysics.At(new Vector3(2,.701f,-12));
        var move=sim.Advance(ref state,new Vector3(.8f,1.51f,-12),new Vector3(1.6f,1.51f,-12),remote,remote,.48f,.15f);
        Check(move.playerPush && state.center.x>2.05f && !move.crushed,"Player side push failed");
        var initial=CargoPhysics.At(CargoPhysics.Spawn);
        state=initial;bool pushed=false;float hitX=0;
        for(int i=0;i<120;i++){
            var from=new Vector3(14-i*.14f,3.76f,6);
            var tick=sim.Advance(ref state,remote,remote,from,from+Vector3.left*.14f,.48f,1f/60);
            pushed|=tick.spherePush;
            if(state.center.y<2.5f){hitX=state.center.x;break;}
        }
        Check(pushed && hitX!=0,"Sphere could not send crate over ledge");
        Debug.Log($"Free drop crossed head height at x={hitX:F2}");
        Vector3 victim=new(hitX,1.51f,6);
        state=initial;bool killed=false;
        for(int i=0;i<120;i++){
            var from=new Vector3(14-i*.14f,3.76f,6);
            var tick=sim.Advance(ref state,victim,victim,from,from+Vector3.left*.14f,.48f,1f/60);
            if(tick.crushed){killed=true;break;}
        }
        Check(killed,"Falling crate failed to end a player beneath its path");
        // Same falling body: test a nearby player outside its footprint.
        state=CargoPhysics.At(new Vector3(0,4,-12));
        Vector3 bystander=new(1.5f,1.51f,-12);
        for(int i=0;i<90;i++){
            var tick=sim.Advance(ref state,bystander,bystander,remote,remote,.48f,1f/60);
            Check(!tick.crushed,"Near miss counted as a crush");
        }
        var shield=CargoPhysics.At(new Vector3(0,2,-9));
        Vector3 eye=new(0,2,-12),target=new(0,2,-6);
        Check(!SphereSight.Rail(eye,Vector3.forward,target,out _,.48f,shield),"Rail passed through cargo");
        Check(!SphereSight.Exposed(eye,target,out _,.48f,shield),"Crate failed to conceal the full sphere");
        shield.center+=Vector3.right*2;
        Check(SphereSight.Rail(eye,Vector3.forward,target,out _,.48f,shield),"Moved crate still blocks its old position");
        Debug.Log("Crate mechanics passed: player push, sphere push, falling impact.");
        CheckLayout(sim);
        var planner=new FlightPlanner(){Cargo=sim};
        foreach(float x in new[]{7.8f,hitX,hitX+1,hitX-1,9f})
        foreach(float start in new[]{13.15f,14f})
        foreach(Vector3 aim in new[]{Vector3.right,Vector3.forward}){
            Vector3 sphere=planner.Graph.Spawn(new(start,3+FlightPlanner.Radius+SurfaceWorld.Skin,6));
            var model=new FlightPlanner.Model{eye=new(x,1.51f,6),aim=aim};
            var plan=planner.Plan(1,0,sphere,planner.Graph.NearestVisible(sphere),model,Vector3.zero,true,true,2.6f,initial);
            if(plan.reason!="Predicted crate impact")continue;
            Debug.Log($"Search found crate impact: player x={x:F3}, sphere x={start:F3}, aim={aim}, first arc {plan.choices[plan.route[0]].to}");return;
        }
        Debug.Log("No selected lethal push in surveyed starting states.");
    }
}
