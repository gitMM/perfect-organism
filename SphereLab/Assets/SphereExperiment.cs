using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class SphereExperiment : MonoBehaviour
{
    [SerializeField] ArenaDefinition arena;
    FlightGraph graph;
    CargoPhysics cargoPhysics;
    CargoPhysics.State crate;
    Transform[] crateViews;
    int impactCrate;
    int site=-1;
    bool ledgeTrial=>site>=0;
    bool crateProbe,hadChosenImpact,hadSpherePush;
    readonly FlightBody body=FlightBody.Default;
    FlightPlanner planner;
    FlightPlanner.Model player;
    FlightPlanner.Decision decision;
    readonly List<FlightPlanner.Decision> history=new();
    Camera cam; Transform orb,gun; Renderer subjectRenderer;
    SphereEnvelope envelope;
    FlightPlanner.Edge leg;
    Vector3 flightVelocity,railEnd;
    EndingView endingView;
    readonly DeathReview review=new();
    bool reviewing,reviewPending,escapeEnabled=true,alternateEnabled=true;
    float observedTurnRate=2.6f;
    int reviewFrame;
    [Serializable] class ReviewLog{
        public string kind="death-review"; public int round,decision,firstDifference;
        public float hitTime; public List<DeathReview.Match> frames;
    }
    float endingTime;
    float yaw,pitch,elapsed,legTime,railFlash,traceClock;
    int anchor,sequence,selected,round;
    bool inspect,showGraph,finished,liveOverlay=true;
    float inspectPitch,inspectYaw;
    bool probe,deathProbe; int probeFrames,branch=-1,expansion=-1;
    string result="";
    readonly ExperimentLog log=new();
    Material lines;
    FlightPlanner.Decision Displayed => reviewing ? review.plan : inspect && selected>=0 ? history[selected] : decision;
    [Serializable] class Observation
    {
        public string kind,detail; public int decision,round; public float time;
        public Vector3 sphere,supportNormal,bodyCenter,bodyScale; public float surfaceClearance; public FlightPlanner.Model player; public CargoPhysics.State crate;
    }
    void Awake()
    {
        try { Initialize(); }
        catch (Exception error) { enabled=false; Debug.LogException(error,this); }
    }

    void Initialize()
    {
        var args=Environment.GetCommandLineArgs();
        probe=Array.IndexOf(args,"-stationaryProbe")>=0;
        deathProbe=Array.IndexOf(args,"-deathReviewProbe")>=0;
        crateProbe=Array.IndexOf(args,"-crateProbe")>=0;
        site=crateProbe?0:-1;
        probe|=deathProbe || crateProbe;
        escapeEnabled=Array.IndexOf(args,"-noEscapeCheck")<0;
        alternateEnabled=Array.IndexOf(args,"-noAlternateAim")<0;
        if(arena)arena.BuildGeometry(); else Hangar.Make(Array.IndexOf(args,"-recordedWaitProbe")<0);
        Physics.SyncTransforms();
        if(Array.IndexOf(args,"-replayDecisionFixes")>=0){CargoRegression.ReplayDecisionFixes();Application.Quit();enabled=false;return;}
        bool pairedReplay=Array.IndexOf(args,"-replayLethality")>=0;
        if(pairedReplay || Array.IndexOf(args,"-replayInteractions")>=0){
            try{if(pairedReplay)CargoRegression.ReplayLethality();else CargoRegression.ReplayInteractions();Application.Quit();}
            catch(Exception error){Debug.LogException(error);Application.Quit(1);}
            enabled=false;return;
        }
        if(Array.IndexOf(args,"-profileRecordedPlans")>=0){CargoRegression.ProfileRecordedPlans();Application.Quit();enabled=false;return;}
        if(Array.IndexOf(args,"-recordedWaitProbe")>=0){CargoRegression.ReplayWaits();Application.Quit();enabled=false;return;}
        if(Array.IndexOf(args,"-cargoMechanicsProbe")>=0){CargoRegression.Run();Application.Quit();enabled=false;return;}
        graph=new FlightGraph(arena ? arena.navigation : FlightGrid.Hangar,body.radius);
        if(!arena){
            cargoPhysics=new CargoPhysics();
            crateViews=new Transform[CargoPhysics.Starts.Length];
            for(int i=0;i<crateViews.Length;i++)
                crateViews[i]=ArenaGeometry.Block("Movable cargo "+(i+1),CargoPhysics.Starts[i],Vector3.one*CargoPhysics.Half*2,new Color(.85f,.43f,.12f),false).transform;
        }
        foreach(Camera old in FindObjectsByType<Camera>(FindObjectsSortMode.None))old.enabled=false;
        cam=gameObject.AddComponent<Camera>();cam.nearClipPlane=.04f;cam.fieldOfView=75;
        cam.clearFlags=CameraClearFlags.SolidColor;cam.backgroundColor=new(.052f,.065f,.075f);
        var sphere=GameObject.CreatePrimitive(PrimitiveType.Sphere);
        orb=new GameObject("Subject route").transform;
        sphere.name="Subject";sphere.transform.SetParent(orb,false);
        envelope=new SphereEnvelope(graph.surface,body.radius,sphere);
        subjectRenderer=sphere.GetComponent<Renderer>();
        subjectRenderer.material.color=Color.black;
        subjectRenderer.material.SetFloat("_Glossiness",.72f);
        gun=ArenaGeometry.Block("Rail emitter",Vector3.zero,new(.13f,.14f,.68f),new(.16f,.22f,.24f),false).transform;
        gun.SetParent(cam.transform,false);gun.localPosition=new(.27f,-.22f,.52f);
        lines=new Material(Resources.Load<Material>("RecorderLines"));lines.SetInt("_ZWrite",0);

        ResetRound();
    }
    void Observe(string kind,string detail="")
    {
        float clearance=graph.surface.Distance(orb.position,out var normal,crate);
        log.Write(JsonUtility.ToJson(new Observation{kind=kind,detail=detail,round=round,decision=sequence,time=elapsed,sphere=orb.position,bodyCenter=envelope.Center,bodyScale=subjectRenderer.transform.localScale,supportNormal=normal,surfaceClearance=clearance,player=player,crate=crate}));
    }
    void ResetRound()
    {
        planner=new FlightPlanner(graph,body){Cargo=cargoPhysics,Envelope=envelope};
        if(crateViews!=null){crate=CargoPhysics.Layout();ShowCrates(crate);}
        hadChosenImpact=hadSpherePush=false;
        if(round==0)log.Open(arena ? arena.name : "Hangar",body);
        review.Reset();reviewing=reviewPending=false;observedTurnRate=2.6f;
        round++;endingTime=0;flightVelocity=Vector3.zero;finished=false;result="";yaw=0;pitch=0;railFlash=0;
        yaw=arena ? arena.playerYaw : 0;
        player=new FlightPlanner.Model{eye=arena ? arena.playerEye : new Vector3(0,1.51f,-14),aim=Quaternion.Euler(0,yaw,0)*Vector3.forward};
        Vector3 spawn=arena ? arena.enemySpawn : new Vector3(6,3.75f,6);
        if(!probe && !ledgeTrial && graph.RandomStarts(crate,out var eye,out var enemy)){
            player.eye=eye;spawn=enemy;
            yaw=Mathf.Atan2(enemy.x-eye.x,enemy.z-eye.z)*Mathf.Rad2Deg;
            player.aim=Quaternion.Euler(0,yaw,0)*Vector3.forward;
        }
        if(ledgeTrial && crate.present){
            var cargo=crate.Get(site);var push=CargoPhysics.DropDirections[site];
            player.eye=cargo.center+(site==0?2.7f:4f)*push;player.eye.y=1.51f;
            player.aim=-push;yaw=Quaternion.LookRotation(player.aim).eulerAngles.y;
            spawn=cargo.center-push*1.35f;spawn.y=cargo.center.y-CargoPhysics.Half+body.radius+SurfaceWorld.Skin;
        }
        if(deathProbe){
            spawn=player.eye+player.aim*5-Vector3.up*.26f;
            yaw=180;player.aim=Vector3.back;
        }
        spawn=graph.Spawn(spawn);
        anchor=graph.NearestVisible(spawn);
        if(anchor<0 || Physics.CheckSphere(spawn,body.radius,1<<8))
            throw new InvalidOperationException("Enemy spawn must fit the body and be connected to a navigation node.");
        orb.position=spawn;history.Clear();decision=null;
        envelope.Update(spawn,player.eye,crate,0,true);
        gun.gameObject.SetActive(true);SetView(false);Observe("round-start",crateProbe?"crate probe":ledgeTrial?CargoPhysics.SiteNames[site]:deathProbe?"death review probe":probe?"stationary probe":"human");Replan();
    }
    void ShowCrates(CargoPhysics.State state){
        for(int i=0;i<state.Count;i++)crateViews[i].position=state.Get(i).center;
    }
    void SetView(bool debug)
    {
        reviewing=false;
        inspect=debug;selected=history.Count-1;branch=expansion=-1;
        Cursor.lockState=CursorLockMode.Locked;Cursor.visible=false;
        subjectRenderer.enabled=true;
        ShowCrates(crate);
        if(debug){
            inspectPitch=cam.transform.eulerAngles.x;
            if(inspectPitch>180)inspectPitch-=360;
            inspectYaw=cam.transform.eulerAngles.y;
        }
        else if(finished)UpdateEnding(0);
        else{cam.transform.position=player.eye;cam.transform.rotation=Quaternion.Euler(pitch,yaw,0);}
    }
    void BrowseDecision(int delta)
    {
        int index=Mathf.Clamp(selected+delta,0,history.Count-1);
        if(index==selected)return;
        selected=index;branch=expansion=-1;
        var snapshot=history[selected];
        ShowCrates(snapshot.crate);
        cam.transform.SetPositionAndRotation(snapshot.observed.eye,Quaternion.LookRotation(snapshot.observed.aim));
        inspectYaw=cam.transform.eulerAngles.y;
        inspectPitch=cam.transform.eulerAngles.x;
        if(inspectPitch>180)inspectPitch-=360;
        // The cyan marker represents the sphere at the start of the historical plan.
        subjectRenderer.enabled=false;
    }
    void Replan()
    {
        decision=planner.Plan(++sequence,elapsed,orb.position,anchor,player,flightVelocity,escapeEnabled,alternateEnabled,observedTurnRate,crate,!crateProbe);
        hadChosenImpact|=decision.reason=="Predicted crate impact";
        review.Begin(decision,orb.position);
        history.Add(decision);if(history.Count>48)history.RemoveAt(0);
        log.Write("{\"kind\":\"plan\",\"round\":"+round+",\"data\":"+JsonUtility.ToJson(decision)+"}");
        leg=decision.route.Count==0?new FlightPlanner.Edge{from=orb.position,to=orb.position}:decision.choices[decision.route[0]];
        legTime=0;
    }
    Vector3 VelocityOnLeg()
    {
        if(leg.surfacePath==null || leg.surfacePath.Length==0)
            return Vector3.Lerp(leg.startVelocity,leg.endVelocity,legTime/FlightPlanner.Step);
        int i=Mathf.Clamp(Mathf.FloorToInt(legTime/FlightPlanner.Step*leg.surfacePath.Length),0,leg.surfacePath.Length-1);
        return (leg.surfacePath[i]-(i==0?leg.from:leg.surfacePath[i-1]))*(leg.surfacePath.Length/FlightPlanner.Step);
    }
    void End(string why)
    {
        finished=true;result=why;endingTime=0;
        endingView=new EndingView(cam.transform,why=="Crate impact"?crate.Get(impactCrate).center:envelope.Center);
        gun.gameObject.SetActive(false);Observe("outcome",why);
        if(why=="Rail hit"){
            reviewPending=review.frames.Count>0;
            log.Write(JsonUtility.ToJson(new ReviewLog{round=round,decision=review.plan?.id??-1,
                firstDifference=review.firstDifference,hitTime=elapsed,frames=review.frames}));
        }
        log.Flush();
    }
    void OpenReview()
    {
        if(review.frames.Count==0)return;
        SetView(true);reviewing=true;reviewPending=false;subjectRenderer.enabled=false;
        reviewFrame=review.firstDifference>=0?review.firstDifference:review.frames.Count-1;
        cam.transform.SetPositionAndRotation(review.plan.observed.eye,Quaternion.LookRotation(review.plan.observed.aim));
        inspectYaw=cam.transform.eulerAngles.y;inspectPitch=Mathf.DeltaAngle(0,cam.transform.eulerAngles.x);
    }
    void UpdateEnding(float dt)
    {
        endingTime+=dt;
        endingView.Apply(cam.transform,endingTime);
        railFlash=Mathf.Max(0,railFlash-dt);
    }
    void Update()
    {
        if(deathProbe && reviewing && ++probeFrames>=3){
            if(review.plan==null || review.frames.Count<2 || review.firstDifference<0 || !inspect)
                throw new InvalidOperationException("Missing automatic first-person death comparison");
            Observe("probe-end","death review displayed");log.Flush();Application.Quit();return;
        }
        if(crateProbe && (finished || sequence>=35)){
            if(probeFrames++==0){Observe("probe-end",$"{result}; selected impact={hadChosenImpact}; pushed={hadSpherePush}");log.Flush();}
            if(probeFrames>2)Application.Quit();return;
        }
        if(probe && !crateProbe && !deathProbe && (finished || sequence>=18)){
            if(probeFrames++==0){
                var position=cam.transform.position;var rotation=cam.transform.rotation;
                SetView(true);
                if(cam.orthographic || cam.cullingMask==0 || cam.transform.position!=position || Quaternion.Angle(cam.transform.rotation,rotation)>.01f)
                    throw new InvalidOperationException("Recorder changed FPS perspective on pause");
                Observe("probe-end",result);log.Flush();
            }
            if(probeFrames==2 && selected>0){
                BrowseDecision(-1);
                if(cam.transform.position!=Displayed.observed.eye || Vector3.Angle(cam.transform.forward,Displayed.observed.aim)>.05f)
                    throw new InvalidOperationException("Recorder history pose mismatch");
            }
            if(probeFrames==3)SetView(false);
            if(probeFrames>=4)Application.Quit();
            return;
        }
        if(Input.GetKeyDown(KeyCode.F2))escapeEnabled=!escapeEnabled;
        if(Input.GetKeyDown(KeyCode.F3))alternateEnabled=!alternateEnabled;
        if(finished && result=="Rail hit" && Input.GetKeyDown(KeyCode.V))OpenReview();
        if(Input.GetKeyDown(KeyCode.F1))liveOverlay=!liveOverlay;
        if(Input.GetKeyDown(KeyCode.G))showGraph=!showGraph;
        if(Input.GetKeyDown(KeyCode.Tab))SetView(!inspect);
        if(Input.GetKeyDown(KeyCode.F4) && crate.present){site=site+1>=crate.Count?-1:site+1;ResetRound();return;}
        if(Input.GetKeyDown(KeyCode.R)){ResetRound();return;}
        if(inspect){
            if(reviewing){
                if(Input.GetKeyDown(KeyCode.LeftArrow))reviewFrame=Mathf.Max(0,reviewFrame-1);
                if(Input.GetKeyDown(KeyCode.RightArrow))reviewFrame=Mathf.Min(review.frames.Count-1,reviewFrame+1);
            }
            if(!reviewing && selected>=0){
                var d=history[selected];
                if(Input.GetKeyDown(KeyCode.N) && d.search!=null){
                    expansion=expansion+1<d.search.expansions.Count?expansion+1:-1;
                    branch=-1;
                }
                if(Input.GetKeyDown(KeyCode.B)){
                    if(expansion>=0){
                        var step=d.search.expansions[expansion];
                        branch=branch<0?step.firstChild:branch+1;
                        if(branch>=step.firstChild+step.childCount)branch=-1;
                    }else{
                        do{branch++;}while(branch<d.choices.Count && d.choices[branch].parent!=-1);
                        if(branch>=d.choices.Count)branch=-1;
                    }
                }
            }
            if(!reviewing && Input.GetKeyDown(KeyCode.LeftArrow))BrowseDecision(-1);
            if(!reviewing && Input.GetKeyDown(KeyCode.RightArrow))BrowseDecision(1);
            inspectYaw+=Input.GetAxisRaw("Mouse X")*2.2f;
            inspectPitch=Mathf.Clamp(inspectPitch-Input.GetAxisRaw("Mouse Y")*2.2f,-89,89);
            cam.transform.rotation=Quaternion.Euler(inspectPitch,inspectYaw,0);
            return;
        }
        if(finished){UpdateEnding(Time.unscaledDeltaTime);if(reviewPending && endingTime>=.75f)OpenReview();return;}
        if(!probe && Cursor.lockState!=CursorLockMode.Locked){if(Input.GetMouseButtonDown(0))SetView(false);return;}
        planner.RefreshGuides();
        float dt=probe?1f/60:Mathf.Clamp(Time.deltaTime,.0001f,.04f);elapsed+=dt;
        yaw+=Input.GetAxisRaw("Mouse X")*2.2f;pitch=Mathf.Clamp(pitch-Input.GetAxisRaw("Mouse Y")*2.2f,-85,85);
        cam.transform.rotation=Quaternion.Euler(pitch,yaw,0);
        bool testRail=deathProbe && elapsed>=.1f;
        if(testRail)cam.transform.LookAt(subjectRenderer.transform);
        float rate=Vector3.Angle(player.aim,cam.transform.forward)*Mathf.Deg2Rad/dt;
        // Recent peak turn speed, decaying with a half-life of half a second.
        observedTurnRate=Mathf.Max(Mathf.Clamp(rate,.5f,12f),observedTurnRate*Mathf.Exp(-1.3863f*dt));
        player.aim=cam.transform.forward;
        Vector3 input=new(Input.GetAxisRaw("Horizontal"),0,Input.GetAxisRaw("Vertical"));
        var wish=Quaternion.Euler(0,yaw,0)*Vector3.ClampMagnitude(input,1)*6;
        Vector3 before=player.eye;player.eye=PlayerMotion.Slide(player.eye,wish*dt);
        player.velocity.x=(player.eye.x-before.x)/dt;player.velocity.z=(player.eye.z-before.z)/dt;
        var underfoot=crate.Get(crate.Nearest(player.eye-Vector3.up*1.5f));
        bool onCrate=crate.present && Mathf.Abs(player.eye.y-1.5f-underfoot.Box.max.y)<.05f &&
            Mathf.Abs(player.eye.x-underfoot.center.x)<CargoPhysics.Half+.25f && Mathf.Abs(player.eye.z-underfoot.center.z)<CargoPhysics.Half+.25f;
        if(Input.GetKeyDown(KeyCode.Space) && (onCrate || Physics.Raycast(player.eye,Vector3.down,1.57f,1<<8)))player.velocity.y=6.8f;
        PlayerMotion.Vertical(ref player,dt);cam.transform.position=player.eye;player.ready-=dt;railFlash-=dt;
        Vector3 sphereBefore=orb.position;
        legTime+=dt;orb.position=FlightPlanner.FlightPoint(leg,legTime);
        bool crushed=false,blocked=false;
        if(crate.present){
            bool walkingCrate=leg.supportCenters!=null || (decision.route.Count==0 && graph.surface.Distance(sphereBefore,out _)>body.radius+.065f &&
                crate.Get(crate.Nearest(sphereBefore)).Box.SqrDistance(sphereBefore)<Mathf.Pow(body.radius+.075f,2));
            int support=leg.supportCenters!=null?leg.supportId:crate.Nearest(sphereBefore);
            Vector3 oldCrateCenter=crate.Get(support).center;
            var move=cargoPhysics.Advance(ref crate,before,player.eye,sphereBefore,walkingCrate?sphereBefore:orb.position,body.radius,dt);
            blocked=!walkingCrate && (move.sphere-orb.position).sqrMagnitude>.0036f;
            if(walkingCrate){
                Vector3 desired=orb.position;
                if(graph.surface.Distance(desired,out _)>body.radius+.05f){
                    if(leg.supportCenters==null)desired=sphereBefore+crate.Get(support).center-oldCrateCenter;
                    else{
                        float phase=Mathf.Clamp01(legTime/FlightPlanner.Step)*leg.supportCenters.Length;
                        int i=Mathf.Min((int)phase,leg.supportCenters.Length-1);
                        desired+=crate.Get(support).center-Vector3.Lerp(i==0?leg.supportStart:leg.supportCenters[i-1],leg.supportCenters[i],phase-i);
                    }
                }
                if(graph.surface.Attach(desired,.25f,out var attached,crate))orb.position=attached;
                else {blocked=true;}
            }
            if(Mathf.Abs(move.player.y-player.eye.y)>.0001f)player.velocity.y=0;
            if(blocked)orb.position=sphereBefore;
            player.eye=move.player;cam.transform.position=player.eye;
            player.velocity.x=(player.eye.x-before.x)/dt;player.velocity.z=(player.eye.z-before.z)/dt;
            ShowCrates(crate);crushed=move.crushed;if(crushed)impactCrate=move.crushId;
            if(move.spherePush){hadSpherePush=true;Observe("crate-push","sphere #"+(move.spherePushId+1));}
            if(move.playerPush)Observe("crate-push","player #"+(move.playerPushId+1));
        }
        flightVelocity=VelocityOnLeg();
        envelope.Update(orb.position,player.eye,crate,dt,upcoming:FlightPlanner.FlightPoint(leg,legTime+.12f));
        planner.ObserveSight(elapsed,player,envelope.Center,Mathf.Max(envelope.Width,envelope.Height),crate);
        review.Record(elapsed,player,orb.position);
        bool missedShot=false;
        if((testRail || Input.GetMouseButtonDown(0)) && player.ready<=0)
        {
            bool tagged=envelope.Rail(player.eye,player.aim,crate,out float range);
            railEnd=player.eye+player.aim*range;
            player.ready=FlightPlanner.RailRecharge;railFlash=.09f;Observe("rail",tagged?"hit":"miss");if(tagged){End("Rail hit");return;}
            missedShot=true;
        }
        if(crushed){End("Crate impact");return;}
        if(envelope.Touches(player.eye)){End("Sphere contact");return;}
        if((traceClock-=dt)<=0){Observe("observation");traceClock=.05f;}
        if(blocked || missedShot || legTime>=FlightPlanner.Step)
        {
            float remainder=crate.present || missedShot || blocked?0:legTime-FlightPlanner.Step;
            if(blocked)flightVelocity=(orb.position-sphereBefore)/dt;
            // Reattach the continuous position to a visible graph guide.
            int nextAnchor=graph.NearestVisible(orb.position);
            if(nextAnchor>=0)anchor=nextAnchor;
            Replan();
            legTime=remainder;
            orb.position=FlightPlanner.FlightPoint(leg,legTime);
            if(remainder>0)envelope.Update(orb.position,player.eye,crate,0);
            flightVelocity=Vector3.Lerp(leg.startVelocity,leg.endVelocity,legTime/FlightPlanner.Step);
        }
    }
    void OnPostRender()
    {
        FlightOverlay.Draw(lines,graph,inspect || liveOverlay ? Displayed : null,showGraph,
            inspect,branch,expansion,review,reviewFrame,reviewing,railFlash,player,railEnd);
    }
    void OnGUI()
    {
        bool overlay=inspect || liveOverlay;
        var d=Displayed;
        GUI.Box(new Rect(12,12,Mathf.Min(690,Screen.width-24),reviewing?216:inspect && d?.search!=null?315:overlay && d!=null && d.contactsChecked>0?182:overlay?158:56),"");
        GUI.Label(new Rect(24,16,580,23),(ledgeTrial?CargoPhysics.SiteNames[site]+"   ·   ":"")+(reviewing?"RAIL HIT  /  LAST SURVIVAL FORECAST":inspect?"FLIGHT RECORDER  /  PAUSED":overlay?"FLIGHT RECORDER  /  LIVE":"HANGAR  /  Trial "+round));
        if(reviewing){
            var f=review.frames[reviewFrame];
            GUI.Label(new Rect(24,39,580,22),$"Decision {d.id}   ·   Sample +{f.t:F3}s   ·   Rail hit +{elapsed-d.time:F3}s");
            GUI.Label(new Rect(24,62,580,22),"Cyan  actual pose and sphere     Yellow  forecast     Magenta  alternate aim (if enabled)");
            GUI.Label(new Rect(24,85,580,22),$"Eye offset {f.eyeError:F2} m    Aim offset {f.aimError:F1}°    Sphere offset {f.sphereError:F2} m");
            string mismatch=review.firstDifference<0?"No threshold crossed":$"First pose difference at +{review.frames[review.firstDifference].t:F3}s";
            GUI.Label(new Rect(24,108,580,22),mismatch+"   (0.25 m eye or 5° aim)");
            GUI.Label(new Rect(24,131,580,22),"← →  scrub matched times     Mouse  look     Tab  exit review     R  restart");
            GUI.Label(new Rect(24,154,580,22),$"Comparison ends at +{review.frames[review.frames.Count-1].t:F3}s. Later replans can change the sphere’s path.");
        }else if(overlay && d!=null){
            string rail=d.observed.ready>0?$"Rail recharging ({d.observed.ready:F2}s)":"Rail ready";
            GUI.Label(new Rect(24,39,580,22),$"Decision {d.id}   ·   {d.reason}   ·   {rail} at decision");
            GUI.Label(new Rect(24,62,580,22),"Green  chosen route     Blue  alternatives     Red  predicted hit     Gold  predicted aim");
            GUI.Label(new Rect(24,85,640,22),inspect?"← →  decisions     N  search expansion     B  branch     G  graph     Tab  resume":"F1  hide overlay     Tab  inspect     G  graph     F4  next ledge     R  restart");
            if(inspect && branch>=0 && branch<d.choices.Count){
                var b=d.choices[branch];
                string effect=b.firstShot>=0?$"rail hit at +{b.firstShot:F2}s":b.contactVeto??(b.crush?"crate kill":b.contact?"contact":b.cratePush?"displaces crate":"safe arc");
                GUI.Label(new Rect(24,108,650,22),$"Edge {branch+1}: {FlightPlanner.DescribeInput(b.input)}   ·   estimate {b.estimate:F2}   ·   {effect}");
            }else if(d.choices.Count>0){
                Vector3 source=d.choices[0].from;
                var view=cam.WorldToViewportPoint(source);
                string direction=view.z<=0?"behind you":view.x<0?"to your left":view.x>1?"to your right":view.y<0?"below view":view.y>1?"above view":"at the cyan cross";
                GUI.Label(new Rect(24,108,580,22),"Plan starts "+direction);
            }
            GUI.Label(new Rect(24,131,590,22),$"F2 escape {(escapeEnabled?"on":"off")}   ·   {d.escapeResult}   |   F3 alternate aim {(alternateEnabled?"on":"off")}   ·   {d.alternateRate*Mathf.Rad2Deg:F0}°/s");
            if(d.contactsChecked>0)
                GUI.Label(new Rect(24,154,590,22),$"Contact checks: {d.contactsVetoed}/{d.contactsChecked} declined. {d.lastContactVeto}");
            if(inspect && d.search!=null){
                var t=d.search;
                GUI.Label(new Rect(24,179,660,22),$"{t.source} selected the route   ·   {t.selection}");
                string budget=t.budgetMs>0?$" / {t.budgetMs:F0} ms":" ms";
                GUI.Label(new Rect(24,202,660,22),$"Goal: defeat player before rail hit   ·   {t.stop}   ·   {t.elapsedMs:F2}{budget}");
                GUI.Label(new Rect(24,225,660,22),$"{d.expanded} states expanded   ·   depth {t.maxDepth}/{FlightPlanner.Depth}   ·   {t.attempted} moves tested   ·   {t.physicalImpacts} arcs with impacts");
                GUI.Label(new Rect(24,248,660,22),$"Rejected: {t.invalid} infeasible, {t.railRejected} rail, {t.vetoed} contact veto");
                if(expansion>=0 && expansion<t.expansions.Count){
                    var step=t.expansions[expansion];string from=step.fromEdge<0?"start":$"edge {step.fromEdge+1}";
                    GUI.Label(new Rect(24,271,660,22),$"Expansion {expansion+1}/{t.expansions.Count} from {from}   ·   {step.childCount} arcs   ·   {step.untested} inputs left untested");
                    GUI.Label(new Rect(24,294,660,22),$"Cyan: expanded path   ·   Blue: children   ·   Best leaf so far: {(step.leadingEdge<0?"none":(step.leadingEdge+1).ToString())}");
                }else if(d.route.Count>0){
                    var first=d.choices[d.route[0]];
                    GUI.Label(new Rect(24,271,660,22),$"First move: {FlightPlanner.DescribeInput(first.input)}   ·   edges "+string.Join(" → ",d.route.ConvertAll(i=>i+1)));
                    var miss=d.forecast.Find(sample=>sample.forecastMiss);
                    if(miss.forecastMiss)GUI.Label(new Rect(24,294,660,22),$"Assumes a miss at +{miss.t:F2}s   ·   estimated miss chance if fired {miss.missChance:P0}");

                }
            }
        }else GUI.Label(new Rect(24,39,580,22),"WASD  move    Space  jump    Click  rail    F1  live debug    Tab  inspect");
        if(inspect)GUI.Label(new Rect(16,Screen.height-52,Screen.width-32,50),log.Location);
        GUI.Label(new Rect(Screen.width/2-5,Screen.height/2-11,20,24),"+");
        if(finished && !inspect)GUI.Box(new Rect(Screen.width/2-155,Screen.height/2+30,310,36),result+(result=="Rail hit"?(review.frames.Count>0?"   ·   V  review":"   ·   No prior survival forecast"):"")+"   ·   R  retry");
    }
    void OnDestroy(){cargoPhysics?.Dispose();log.Dispose();if(lines)Destroy(lines);Cursor.lockState=CursorLockMode.None;Cursor.visible=true;}
}

