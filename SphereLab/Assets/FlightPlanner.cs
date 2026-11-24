using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class FlightPlanner
{
    public const float Step = .25f, Radius = .48f, Speed = 16f;
    public const int Depth = 6, Budget = 64;
    const int Solid = 1 << 8;
    public static bool Clear(Vector3 a, Vector3 b, float r)
    {
        Vector3 d = b - a;
        return d.sqrMagnitude < .000001f || !Physics.SphereCast(a, r, d.normalized, out _, d.magnitude, Solid);
    }
    readonly FlightBody body;
    readonly float exposureCost, refugeCost;
    public FlightGraph Graph { get; }
    public List<Vector3> nodes => Graph.nodes;
    public List<List<int>> links => Graph.links;

    public FlightPlanner() : this(new FlightGraph(FlightGrid.Hangar, FlightBody.Default.radius), FlightBody.Default) {}

    public FlightPlanner(FlightGraph graph, FlightBody body, float exposureCost = .55f, float refugeCost = 1f)
    {
        body.Validate();
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        if (graph.radius != body.radius) throw new ArgumentException("Graph clearance must match enemy radius.");
        this.body = body;
        this.exposureCost = exposureCost;
        this.refugeCost = refugeCost;
        contactCheck = new ContactCheck(body);
    }
    [Serializable] public struct Model { public Vector3 eye,aim,velocity; public float ready; }
    [Serializable] public struct Sample { public Vector3 sphere,eye,aim,alternateAim,sightTarget; public float t,missChance,visibleTime; public bool forecastMiss,visible,shot,alternateShot,exposed; public Vector3 crate; public int crateId; }
    [Serializable] public class Edge { public int parent,input,supportId,pushedCrate=-1,impactCrate=-1; public Vector3[] surfacePath, supportCenters; public Vector3 supportStart; public Vector3 from,to,startVelocity,endVelocity; public float coverDistance,exposure, firstShot=-1, estimate; public bool contact,cratePush,crush; public string contactVeto; }
    [Serializable] public class Decision
    {
        public SearchTrace search;
        public CargoPhysics.State crate;
        public int id,expanded; public float time; public Model observed;
        public bool escapeEnabled,alternateEnabled;
        public float alternateRate; public int alternateOnlyHits,escapeCandidates,contactsChecked,contactsVetoed;
        public string lastContactVeto;
        public string escapeResult="Unchecked"; public List<Sample> continuation=new();
        public string reason; public List<Edge> choices=new(); public List<int> route=new(); public List<Sample> forecast=new();
    }
    [Serializable] public class SearchTrace
    {
        public string source,selection,stop="Skipped";
        public int bestEdge=-1,maxDepth,attempted,invalid,railRejected,vetoed,physicalImpacts;
        public float elapsedMs,budgetMs;
        public int startNode;
        public SphereEnvelope.Pose startBody;
        public List<Expansion> expansions=new();
    }
    [Serializable] public struct Expansion
    {
        public int fromEdge,depth,firstChild,childCount,leadingEdge,untested;
    }
    class State
    {
        public SphereEnvelope.Pose shape;
        public CargoPhysics.State crate;
        public Model model; public Vector3 pos,velocity,alternateAim;
        public Edge motion;
        public int nextTarget; public int firstEdge; public int node,depth,edge=-1; public State parent;
        public float cost,estimate,visibleTime; public bool contact,assumedMiss; public List<Sample> samples;
    }
    float[] routeDistance,coverDistances;
    IEnumerator<int> routeRefresh;
    Vector3 mapGoal,mapEye,mapFuture;
    public void RefreshGuides()
    {
        if(routeRefresh==null)return;
        var timer=System.Diagnostics.Stopwatch.StartNew();
        while(timer.Elapsed.TotalMilliseconds<1.25){
            if(routeRefresh.MoveNext())continue;
            routeRefresh.Dispose();routeRefresh=null;break;
        }
    }
    Vector3 currentEye,nearFutureEye;
    bool Concealed(Vector3 p) =>
        !SphereSight.Exposed(currentEye,p,out _,body.radius) && !SphereSight.Exposed(nearFutureEye,p,out _,body.radius);

    float DistanceToCover(Vector3 p,int anchor)
    {
        if(Concealed(p))return 0;
        float distance=coverDistances[anchor]+Vector3.Distance(p,nodes[anchor]);
        foreach(int next in links[anchor]){
            float candidate=coverDistances[next]+Vector3.Distance(p,nodes[next]);
            if(candidate<distance && Clear(p,nodes[next],body.radius))distance=candidate;
        }
        return Mathf.Min(80,distance);
    }
    // Static graph distance around cover guides the finite-horizon search.
    IEnumerable<int> MeasureRoutes(Vector3 goal,Vector3 eye,Vector3 future)
    {
        var routes=new float[nodes.Count];
        var queue=new SortedSet<(float distance,int node)>();
        float closest=float.PositiveInfinity;int nearest=0;
        for(int i=0;i<nodes.Count;i++){
            float d=Vector3.Distance(nodes[i],goal);
            if(d<closest){closest=d;nearest=i;}
            routes[i]=float.PositiveInfinity;
            if(d<=6 && Clear(nodes[i],goal,body.radius)){routes[i]=d;queue.Add((d,i));}
            if((i&31)==31)yield return 0;
        }
        if(queue.Count==0){routes[nearest]=closest;queue.Add((closest,nearest));}
        foreach(int slice in Propagate(routes,queue))yield return slice;
        var covers=new float[nodes.Count];
        for(int i=0;i<nodes.Count;i++){
            bool hidden=!SphereSight.Exposed(eye,nodes[i],out _,body.radius) &&
                (eye==future || !SphereSight.Exposed(future,nodes[i],out _,body.radius));
            covers[i]=hidden?0:float.PositiveInfinity;
            if(hidden)queue.Add((0,i));
            if((i&15)==15)yield return 0;
        }
        foreach(int slice in Propagate(covers,queue))yield return slice;
        routeDistance=routes;coverDistances=covers;
    }
    IEnumerable<int> Propagate(float[] distances,SortedSet<(float distance,int node)> queue)
    {
        int expanded=0;
        while(queue.Count>0){
            var top=queue.Min;queue.Remove(top);
            foreach(int j in links[top.node]){
                float d=top.distance+Vector3.Distance(nodes[j],nodes[top.node]);
                if(d>=distances[j])continue;
                queue.Remove((distances[j],j));distances[j]=d;queue.Add((d,j));
            }
            if((++expanded&31)==0)yield return 0;
        }
    }
    readonly Vector3[] forecastEye=new Vector3[(Depth+2)*4];
    public static Vector3 FlightPoint(Edge e,float t)
    {
        t=Mathf.Clamp(t,0,Step);
        if(e.surfacePath!=null && e.surfacePath.Length>0){
            float phase=t/Step*e.surfacePath.Length;
            int i=Mathf.Min(Mathf.FloorToInt(phase),e.surfacePath.Length-1);
            return Vector3.Lerp(i==0?e.from:e.surfacePath[i-1],e.surfacePath[i],phase-i);
        }
        return e.from+e.startVelocity*t+(e.endVelocity-e.startVelocity)*(t*t/(2*Step));
    }

    int LocalAnchor(int origin,Vector3 pos)
    {
        int best=origin;float distance=(nodes[origin]-pos).sqrMagnitude;
        foreach(int j in links[origin]){
            float d=(nodes[j]-pos).sqrMagnitude;
            if(d<distance && Clear(pos,nodes[j],body.radius)){distance=d;best=j;}
        }
        return best;
    }

    // Angular tracking error in degrees. Crossing the sightline adds lag;
    // purely radial travel changes apparent size but does not require a flick.
    public static float MissChance(float angularRadius,float aimGap,float angularSpeed,float visibleTime=0)
    {
        float scatter=1.8f+.08f*angularSpeed+.60f*aimGap;
        float size=angularRadius/scatter;
        return Mathf.Exp(-.5f*size*size-Mathf.Max(0,visibleTime-.125f)/.38f);
    }

    public static bool DirectCharge(Vector3 travel,Vector3 toPlayer)
    {
        // Within 20 degrees of the player leaves little sideways tracking work.
        float closing=Vector3.Dot(travel,toPlayer);
        return closing>0 && closing*closing>=.883f*travel.sqrMagnitude*toPlayer.sqrMagnitude;
    }

    static float TrackTime(float prior,bool visible,float dt)=>
        Mathf.Clamp(prior+(visible?dt:-2*dt),0,2);

    float lastSightTime=-1,observedVisibleTime;
    public void ObserveSight(float time,Model player,Vector3 center,float radius,CargoPhysics.State crates)
    {
        float elapsed=lastSightTime<0?0:time-lastSightTime;
        if(lastSightTime>=0 && elapsed<.05f)return;
        bool visible=Vector3.Dot(player.aim,(center-player.eye).normalized)>.45f &&
            SphereSight.Exposed(player.eye,center,out _,radius,crates);
        observedVisibleTime=TrackTime(observedVisibleTime,visible,Mathf.Min(elapsed,Step));
        lastSightTime=time;
    }

    public static Vector3 Track(Vector3 aim,Vector3 delta,Vector3 target,bool exposed,float rate,float dt)
    {
        if(!exposed || Vector3.Dot(aim,delta.normalized)<=.45f)return aim;
        return Vector3.RotateTowards(aim,target.normalized,rate*dt,0);
    }

    public CargoPhysics Cargo { get; set; }
    public SphereEnvelope Envelope { get; set; }
    // Counterfactual replay only. The physical impact and live damage rules
    // stay the same; this determines whether search treats a crush as a win.
    internal bool PredictLethalImpact { get; set; }=true;
    readonly ContactCheck contactCheck;
    readonly List<Edge> contactRoute=new();
    // Headings are relative to the player's bearing on the current support.
    // The same movement inputs are available with an empty arena.
    static readonly float[] steeringOffsets={0,45,-45,90,-90,180};
    const int SteeringBase=-10000;
    public static string DescribeInput(int input)
    {
        if(input<=SteeringBase)return $"steer {steeringOffsets[SteeringBase-input]}°";
        if(input<=-3)return $"move to crate {(-3-input)/6+1}, face {(-3-input)%6+1}";
        if(input==-2)return "brake";
        return input==-1?"steer to target":$"move to guide {input}";
    }

    State Evaluate(State s,Vector3 dest,int node,Decision log)
    {
        var e=new Edge{parent=s.edge,input=node,from=s.pos,startVelocity=s.velocity};
        const int intervals=16;
        const float dt=Step/4;
        Vector3 position=s.pos,velocity=s.velocity;
        var points=new Vector3[intervals];
        bool ride=s.crate.present && (node<=-3 ||
            (Graph.surface.Distance(s.pos,out _)>body.radius+.065f &&
             s.crate.Get(s.crate.Nearest(s.pos)).Box.SqrDistance(s.pos)<Mathf.Pow(body.radius+.075f,2)));
        CargoPhysics.State moving=s.crate;
        var crateTicks=ride?new CargoPhysics.State[4]:null;
        var playerTicks=ride?new Vector3[4]:null;
        var crushTicks=ride?new bool[4]:null;
        Vector3 walkedEye=s.model.eye;
        e.supportId=node<=-3?(-3-node)/6:moving.Nearest(s.pos);
        if(ride){e.supportCenters=new Vector3[intervals];e.supportStart=moving.Get(e.supportId).center;}
        for(int i=0;i<intervals;i++){
            if(!Graph.surface.Walk(position,dest,ref velocity,body.speed,body.acceleration,Step/intervals,node==-2,out var next,ride?moving:default,node>=0))return null;
            if(ride){
                int sample=s.depth*4+i/4;
                Vector3 eyeDelta=(forecastEye[sample]-(sample==0?currentEye:forecastEye[sample-1]))/4;
                Vector3 oldCenter=moving.Get(e.supportId).center;
                // Tangential crawling does not apply the horizontal shove used by ground contact.
                var move=Cargo.Advance(ref moving,walkedEye,walkedEye+eyeDelta,position,position,body.radius,Step/intervals);
                if(Graph.surface.Distance(next,out _)>body.radius+.05f)next+=moving.Get(e.supportId).center-oldCenter;
                if(!Graph.surface.Attach(next,.10f,out next,moving))return null;
                if(move.crushed)e.impactCrate=move.crushId;
                walkedEye=move.player;crushTicks[i/4]|=move.crushed;
                crateTicks[i/4]=moving;playerTicks[i/4]=walkedEye;e.supportCenters[i]=moving.Get(e.supportId).center;
            }
            points[i]=next;position=next;
        }
        e.surfacePath=points;e.to=position;e.endVelocity=velocity;
        // Stationary actions use the compact analytic representation.
        if(s.velocity.sqrMagnitude<.0000001f && (e.to-s.pos).sqrMagnitude<.0000001f && velocity.sqrMagnitude<.0000001f)e.surfacePath=null;
        var n=new State{parent=s,motion=e,node=LocalAnchor(s.node,e.to),pos=e.to,velocity=e.endVelocity,
            depth=s.depth+1,crate=s.crate,model=s.model,alternateAim=s.alternateAim,cost=s.cost+Step,samples=new(4),shape=s.shape,assumedMiss=s.assumedMiss,visibleTime=s.visibleTime};
        Vector3 previous=s.pos;
        for(int i=1;i<=4;i++){
            Vector3 p=FlightPoint(e,i*dt);
            ref Model m=ref n.model;
            int tick=s.depth*4+i-1;
            Vector3 eye=forecastEye[tick];
            bool crushed=false;
            if(ride){n.crate=crateTicks[i-1];eye=playerTicks[i-1];crushed=crushTicks[i-1];}
            else if(n.crate.present){
                Vector3 priorEye=tick==0?currentEye:forecastEye[tick-1];
                var movement=Cargo.Advance(ref n.crate,m.eye,m.eye+eye-priorEye,previous,p,body.radius,dt);
                // A pinned box makes this particular flight arc infeasible.
                if((movement.sphere-p).sqrMagnitude>.0036f)return null;
                if(movement.spherePush){e.cratePush=true;e.pushedCrate=movement.spherePushId;}
                if(movement.crushed)e.impactCrate=movement.crushId;eye=movement.player;crushed=movement.crushed;
            }
            Vector3 travel=p-previous;
            Vector3 priorDelta=previous-m.eye;
            m.eye=eye;previous=p;
            if(Envelope!=null)n.shape=Envelope.Predict(n.shape,p,m.eye,n.crate,dt,upcoming:FlightPoint(e,i*dt+.12f));
            Vector3 center=Envelope==null?p:n.shape.center;
            float sightRadius=Envelope==null?body.radius:Mathf.Max(n.shape.width,n.shape.height);
            Vector3 delta=center-m.eye;
            bool exposed=SphereSight.Exposed(m.eye,center,out var sightTarget,sightRadius,n.crate);
            bool visible=exposed && Vector3.Dot(m.aim,delta.normalized)>.45f;
            Vector3 priorAim=m.aim,priorAlternate=n.alternateAim;
            m.aim=Track(m.aim,delta,sightTarget-m.eye,exposed,2.6f,dt);
            n.alternateAim=log.alternateEnabled
                ?Track(n.alternateAim,delta,sightTarget-m.eye,exposed,log.alternateRate,dt):m.aim;
            n.visibleTime=TrackTime(n.visibleTime,visible,dt);
            bool charging=DirectCharge(travel,m.eye-Vector3.up*.45f-center);
            float t=s.depth*Step+i*dt;
            m.ready-=dt;
            // Test the held-shot alternative when the player has tracked us or
            // we're charging them. Only an observed reload is reliable here.
            if(n.assumedMiss && (charging || n.visibleTime>=.4f))
                m.ready=Mathf.Min(m.ready,log.observed.ready-t);
            bool shot=m.ready<=0 && (Envelope!=null?n.shape.Intercepts(m.eye,m.aim,n.crate):SphereSight.Rail(m.eye,m.aim,p,out _,body.radius,n.crate));
            bool other=log.alternateEnabled && m.ready<=0 && (Envelope!=null?n.shape.Intercepts(m.eye,n.alternateAim,n.crate):SphereSight.Rail(m.eye,n.alternateAim,p,out _,body.radius,n.crate));
            bool forecastMiss=false;
            float missChance=0;
            if(visible && !charging && m.ready<=0 && !n.assumedMiss && log.time>=heldFireUntil && e.firstShot<0){
                float aimGap=Vector3.Angle(priorAim,delta);
                if(log.alternateEnabled)aimGap=Mathf.Min(aimGap,Vector3.Angle(priorAlternate,delta));
                // Use the visible body's projected area, including its squashed pose.
                float radius=body.radius;
                if(Envelope!=null){
                    float cosine=Vector3.Dot(n.shape.up,delta.normalized);
                    float width=n.shape.width,height=n.shape.height;
                    float secondAxis=Mathf.Sqrt(Mathf.Lerp(height*height,width*width,cosine*cosine));
                    radius=Mathf.Sqrt(width*secondAxis);
                }
                float angularRadius=Mathf.Atan2(radius,delta.magnitude)*Mathf.Rad2Deg;
                // Rotating or squeezing on a corner is not lateral travel.
                float angularSpeed=Vector3.Angle(priorDelta,p-eye)/dt;
                missChance=MissChance(angularRadius,aimGap,angularSpeed,n.visibleTime);
                // A player can see the target well before committing to a shot.
                // Require a plausible trigger window for both tracking models.
                bool trigger=Vector3.Angle(m.aim,delta)<=angularRadius+6 &&
                    (!log.alternateEnabled || Vector3.Angle(n.alternateAim,delta)<=angularRadius+6);
                if(trigger && aimGap<=angularRadius+20 && missChance>=.70f){
                    n.assumedMiss=forecastMiss=true;
                    shot=other=false;
                    m.ready=RailRecharge;
                    n.cost+=(1-missChance)*Depth*Step;
                }
            }
            if(other && !shot && e.firstShot<0)log.alternateOnlyHits++;
            n.samples.Add(new Sample{sphere=p,crateId=e.impactCrate>=0?e.impactCrate:e.supportId,crate=n.crate.present?n.crate.Get(e.impactCrate>=0?e.impactCrate:e.supportId).center:default,eye=m.eye,aim=m.aim,sightTarget=sightTarget,t=t,visibleTime=n.visibleTime,forecastMiss=forecastMiss,missChance=missChance,visible=visible,alternateAim=n.alternateAim,alternateShot=other,shot=shot,exposed=exposed});
            if(exposed)e.exposure+=dt;
            if((shot || other) && e.firstShot<0)e.firstShot=t;
            if(e.firstShot<0 && crushed && PredictLethalImpact){n.contact=e.crush=true;break;}
            if(e.firstShot<0 && (Envelope!=null?n.shape.Touches(m.eye):body.Touches(p,m.eye))){n.contact=e.contact=true;break;}
        }
        if(e.contact){
            contactRoute.Clear();
            for(var state=n;state.parent!=null;state=state.parent)contactRoute.Add(state.motion);
            contactRoute.Reverse();
            log.contactsChecked++;
            e.contactVeto=contactCheck.Verify(contactRoute,n.samples[n.samples.Count-1].t);
            if(e.contactVeto!=null){
                log.contactsVetoed++;log.lastContactVeto=e.contactVeto;
                n.contact=e.contact=false;
            }
        }
        e.coverDistance=DistanceToCover(e.to,n.node);
        // Between equally safe routes, prefer carrying speed through a turn
        // over stopping at a guide and accelerating again on the next leg.
        if(heldPosition && (n.pos-s.pos).sqrMagnitude<.08f*.08f)n.cost+=Step;
        n.cost+=.18f*Mathf.Max(0,s.velocity.magnitude-e.endVelocity.magnitude)/body.speed;
        // Price the time spent without a reachable refuge while the rail is ready.
        float armed=Mathf.Clamp(n.model.ready<0?-n.model.ready:0,0,Step);
        n.cost+=exposureCost*e.exposure+refugeCost*armed*e.coverDistance/(body.speed*Step);
        float distance=routeDistance[n.node]+Vector3.Distance(n.pos,nodes[n.node]);
        foreach(int j in links[n.node])
            distance=Mathf.Min(distance,routeDistance[j]+Vector3.Distance(n.pos,nodes[j]));
        if(float.IsInfinity(distance))distance=Vector3.Distance(n.pos,n.model.eye);
        n.estimate=n.cost+Mathf.Max(0,distance-body.ContactDistance)/body.speed;
        e.estimate=n.estimate; n.edge=log.choices.Count;log.choices.Add(e); n.firstEdge=s.depth==0?n.edge:s.firstEdge;
        return n;
    }
    // Two extra arcs: brake and hold, or steer to a neighboring guide and brake in cover.
    bool TryEscape(State leaf,Decision decision,out List<Sample> samples,out string mode)
    {
        var tail=new Decision{alternateEnabled=decision.alternateEnabled,alternateRate=decision.alternateRate};
        samples=null;mode="No tested escape";
        var targets=new List<int>{-2};targets.AddRange(links[leaf.node]);
        foreach(int index in targets){
            var next=Evaluate(leaf,index<0?leaf.pos:nodes[index],index,tail);
            if(next==null || next.motion.contactVeto!=null || tail.choices[next.edge].firstShot>=0)continue;
            var end=next.contact?next:Evaluate(next,next.pos,-2,tail);
            if(end==null || end.motion.contactVeto!=null || tail.choices[end.edge].firstShot>=0)continue;
            if(!end.contact && (end.samples[end.samples.Count-1].exposed || !Concealed(end.pos)))continue;
            samples=new List<Sample>(next.samples);
            if(end!=next)samples.AddRange(end.samples);
            mode=end.contact?"Contact in extension":index<0?"Brake and hold":"Retreat to cover";
            return true;
        }
        return false;
    }
    public const float RailRecharge = 1f;
    float expectedShotTime=float.PositiveInfinity,heldFireUntil;
    Vector3 previousStart;
    bool hasPreviousStart,heldPosition;

    public Decision Plan(int id,float time,Vector3 start,int anchor,Model model,Vector3 velocity,bool escape=true,bool alternate=true,float measuredRate=2.6f,CargoPhysics.State crate=default,bool realtime=false)
    {
        var log=new Decision{crate=crate,id=id,time=time,observed=model,escapeEnabled=escape,alternateEnabled=alternate,alternateRate=Mathf.Clamp(measuredRate,.5f,12f)};
        contactCheck.Begin(model,alternate?log.alternateRate:2.6f,Cargo,crate,Envelope);
        var walker=model;
        for(int i=0;i<forecastEye.Length;i++){
            walker.eye=PlayerMotion.Slide(walker.eye,walker.velocity*(Step/4)); PlayerMotion.Vertical(ref walker,Step/4);
            forecastEye[i]=walker.eye;
        }
        currentEye=model.eye;nearFutureEye=forecastEye[7];
        var goal=forecastEye[Depth*4-1]-Vector3.up*.45f;
        if(!realtime){
            foreach(int slice in MeasureRoutes(goal,currentEye,nearFutureEye)){}
            mapGoal=goal;mapEye=currentEye;mapFuture=nearFutureEye;
        }else if(routeDistance==null){
            // Use straight-line guidance until the first terrain-aware map is ready.
            routeDistance=new float[nodes.Count];
            coverDistances=new float[nodes.Count];
            for(int i=0;i<nodes.Count;i++)routeDistance[i]=Vector3.Distance(nodes[i],goal);
            mapGoal=goal;mapEye=currentEye;mapFuture=nearFutureEye;
            routeRefresh=MeasureRoutes(goal,currentEye,nearFutureEye).GetEnumerator();
        }else if(routeRefresh==null && ((goal-mapGoal).sqrMagnitude>1 || (currentEye-mapEye).sqrMagnitude>.25f || (nearFutureEye-mapFuture).sqrMagnitude>1)){
            mapGoal=goal;mapEye=currentEye;mapFuture=nearFutureEye;
            routeRefresh=MeasureRoutes(goal,currentEye,nearFutureEye).GetEnumerator();
        }
        // If the expected shot never came, plan against a held rail for the
        // reload window we incorrectly expected. Actual fire opens it immediately.
        if(model.ready>0)heldFireUntil=0;
        else if(time>=expectedShotTime)heldFireUntil=time+RailRecharge;
        expectedShotTime=float.PositiveInfinity;
        heldPosition=hasPreviousStart && (start-previousStart).sqrMagnitude<.08f*.08f;
        previousStart=start;hasPreviousStart=true;
        var pose=Envelope?.Current??default;
        ObserveSight(time,model,Envelope==null?start:pose.center,Envelope==null?body.radius:Mathf.Max(pose.width,pose.height),crate);
        var root=new State{visibleTime=observedVisibleTime,pos=start,node=anchor,crate=crate,model=model,alternateAim=model.aim,velocity=velocity,shape=Envelope?.Current??default};
        State best=null, doomed=null, unverified=null;
        var trace=log.search=new SearchTrace{budgetMs=realtime?5:0,startNode=anchor,startBody=root.shape};
        var searchClock=System.Diagnostics.Stopwatch.StartNew();
        var finalists=new List<State>();
        var search=new GoalPlanner<State>(n=>n.depth,n=>n.firstEdge,n=>n.estimate,n=>n.cost,n=>n.contact);
        bool TimeLeft()=>!realtime || (search.Best==null && doomed==null && unverified==null) || searchClock.Elapsed.TotalMilliseconds<5;
        bool Expand(State s,Action<State> offer)
        {
            log.expanded++;
            var targets=new List<int>{-2};
            // Nearby face samples can represent virtually identical headings at seams.
            // Keep the farthest guide in each heading so the search can explore more turns.
            foreach(int candidate in links[s.node]){
                Vector3 delta=nodes[candidate]-s.pos;
                int same=-1;
                for(int i=1;i<targets.Count;i++){
                    Vector3 existing=nodes[targets[i]]-s.pos;
                    if(Vector3.Dot(existing.normalized,delta.normalized)>.88f){same=i;break;}
                }
                if(same<0)targets.Add(candidate);
                else if(delta.sqrMagnitude>(nodes[targets[same]]-s.pos).sqrMagnitude)targets[same]=candidate;
            }
            Vector3 touch=s.model.eye-Vector3.up*.45f;
            if(Clear(s.pos,touch,body.radius))targets.Add(-1);
            // Aim through visible bends toward a more distant guide point.
            int guide=s.node;
            for(int hop=0;hop<4;hop++){
                int next=guide;
                foreach(int j in links[guide])
                    if(routeDistance[j]<routeDistance[next] && Clear(s.pos,nodes[j],body.radius))next=j;
                if(next==guide)break;
                guide=next;
            }
            if(guide!=s.node && !targets.Contains(guide))targets.Add(guide);
            int refuge=s.node;
            for(int hop=0;hop<4;hop++){
                int next=refuge;
                foreach(int j in links[refuge])
                    if(coverDistances[j]<coverDistances[next] && Clear(s.pos,nodes[j],body.radius))next=j;
                if(next==refuge)break;
                refuge=next;
            }
            if(!targets.Contains(refuge))targets.Add(refuge);
            if(realtime && guide!=s.node){targets.Remove(guide);targets.Insert(0,guide);}
            for(int idCrate=0;idCrate<s.crate.Count;idCrate++)if((s.pos-s.crate.Get(idCrate).center).sqrMagnitude<16)
                {
                    for(int face=0;face<6;face++)targets.Add(-3-idCrate*6-face);
                }
            Graph.surface.Distance(s.pos,out var support,s.crate);
            var forward=Vector3.ProjectOnPlane(touch-s.pos,support).normalized;
            for(int i=0;i<steeringOffsets.Length;i++)targets.Add(SteeringBase-i);
            if(heldPosition && s.depth==0){
                // Try lateral exits while the decision budget is fresh. Braking
                // remains available when every moving route predicts a hit.
                targets.Remove(-2);targets.Add(-2);
                int side=SteeringBase-3;
                targets.Remove(side);targets.Insert(0,side);
                targets.Remove(side-1);targets.Insert(1,side-1);
            }
            var step=new Expansion{fromEdge=s.edge,depth=s.depth,firstChild=log.choices.Count,untested=targets.Count-s.nextTarget};
            while(s.nextTarget<targets.Count)
            {
                if(!TimeLeft())break;
                int target=targets[s.nextTarget++];
                step.untested--;trace.attempted++;
                Vector3 destination;
                int action=target;
                if(target<=SteeringBase){
                    var heading=Quaternion.AngleAxis(steeringOffsets[SteeringBase-target],support)*forward;
                    destination=s.pos+heading*(Step*body.speed*.55f);
                    action=-1;
                }else if(target<=-3){
                    int side=(-3-target)%6;Vector3 normal=Vector3.zero;normal[side/2]=(side%2==0)?1:-1;
                    destination=s.crate.Get((-3-target)/6).center+normal*(CargoPhysics.Half+body.radius+SurfaceWorld.Skin);
                }else destination=target==-2?s.pos:target==-1?touch:nodes[target];
                var n=Evaluate(s,destination,action,log);
                if(n==null){trace.invalid++;continue;}
                var edge=log.choices[n.edge];
                edge.input=target;
                trace.maxDepth=Mathf.Max(trace.maxDepth,n.depth);
                if(edge.impactCrate>=0)trace.physicalImpacts++;
                if(edge.firstShot>=0)
                {
                    trace.railRejected++;
                    var previous=doomed==null?null:log.choices[doomed.edge];
                    if(previous==null || edge.firstShot>previous.firstShot ||
                       (edge.firstShot==previous.firstShot && (edge.coverDistance<previous.coverDistance ||
                       (edge.coverDistance==previous.coverDistance && n.estimate<doomed.estimate))))doomed=n;
                    continue;
                }
                if(edge.contactVeto!=null){
                    trace.vetoed++;
                    if(unverified==null || n.estimate<unverified.estimate)unverified=n;
                    continue;
                }
                offer(n);
                if(!n.contact && n.depth==Depth && escape)finalists.Add(n);
                // Test a continuation before spending the frame on sibling moves.
                if(realtime && (n.contact || search.Best.depth<Depth))break;
            }
            step.childCount=log.choices.Count-step.firstChild;
            step.leadingEdge=search.Best?.edge??-1;
            trace.expansions.Add(step);
            return s.nextTarget==targets.Count;
        }
        search.Search(root,Expand,TimeLeft,Depth,Budget,realtime);
        trace.elapsedMs=(float)searchClock.Elapsed.TotalMilliseconds;
        trace.stop=search.StopReason;
        if(trace.expansions.Count>0 && trace.expansions[trace.expansions.Count-1].untested>0 && trace.stop=="Frontier exhausted")
            trace.stop="Time limit";
        best=search.Best;
        trace.bestEdge=best?.edge??-1;
        trace.source="GOAP";
        trace.selection=best==null?"No accepted successor":best.contact?"Lowest-cost win in the explored expansion":"Deepest safe route; lowest estimate at equal depth";
        if(escape && best!=null && !best.contact && finalists.Count>0 && (!realtime || searchClock.Elapsed.TotalMilliseconds<4)){
            finalists.Sort((a,b)=>a.estimate.CompareTo(b.estimate));
            var shortlist=new List<State>();var firstMoves=new HashSet<int>();
            foreach(var leaf in finalists){
                if(firstMoves.Add(leaf.firstEdge))shortlist.Add(leaf);
                if(shortlist.Count==4)break;
            }
            foreach(var leaf in finalists){
                if(shortlist.Count==4)break;
                if(!shortlist.Contains(leaf))shortlist.Add(leaf);
            }
            shortlist.Sort((a,b)=>a.estimate.CompareTo(b.estimate));
            log.escapeResult="No tested escape";
            foreach(var leaf in shortlist){
                if(realtime && searchClock.Elapsed.TotalMilliseconds>=5)break;
                log.escapeCandidates++;
                if(!TryEscape(leaf,log,out var samples,out var mode))continue;
                best=leaf;log.continuation=samples;log.escapeResult=mode;break;
            }
        }
        if(log.continuation.Count>0)trace.selection="Full route with a tested escape";
        // A partial prefix has no full-horizon safety certificate. Prefer the
        // longest tested survival time if all continuation samples predict death.
        if((best==null || (!best.contact && best.depth<Depth)) && doomed!=null &&
           (best==null || log.choices[doomed.edge].firstShot>best.depth*Step)){best=doomed;trace.selection="Longest tested time before a predicted hit";}
        if(unverified!=null && (best==null || best.motion.firstShot>=0)){best=unverified;trace.selection="Fallback to an unverified contact";}
        log.reason=best!=null && best.motion.contactVeto!=null?"Unverified contact":best==null?"No candidate":log.choices[best.edge].firstShot>=0?"Delay predicted hit":
            best.motion.crush?"Predicted crate impact":best.contact?"Predicted safe contact":best.depth==Depth?"Survives horizon":"Partial forecast";
        if(best==null)trace.source="Fallback";
        for(var n=best;n!=null && n.parent!=null;n=n.parent)log.route.Insert(0,n.edge);
        var chain=new Stack<State>();for(var n=best;n!=null && n.parent!=null;n=n.parent)chain.Push(n);
        foreach(var n in chain)log.forecast.AddRange(n.samples);
        // Preserve full timing for the committed route. The recorder needs only
        // quarter-arc samples to draw discarded alternatives.
        var chosen=new HashSet<int>(log.route);
        for(int i=0;i<log.choices.Count;i++){
            var edge=log.choices[i];if(chosen.Contains(i) || edge.surfacePath==null)continue;
            var path=edge.surfacePath;edge.surfacePath=new[]{path[3],path[7],path[11],path[15]};
            edge.supportCenters=null;
        }
        var miss=log.forecast.Find(sample=>sample.forecastMiss);
        if(miss.forecastMiss)expectedShotTime=time+miss.t;
        return log;
    }
}
