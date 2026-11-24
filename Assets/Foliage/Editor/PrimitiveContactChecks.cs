using System;
using Foliage;
using UnityEditor;
using UnityEngine;

public static class PrimitiveContactChecks
{
    [MenuItem("Tools/Foliage/Check OGC regions")]
    public static void CheckRegions()
    {
        var g=new PlantGeometry();
        g.nodes.AddRange(new[]{Vector3.left,Vector3.right,new Vector3(0,0,1),new Vector3(0,0,-1),
            Vector3.up*3,Vector3.up*4});
        foreach(var p in g.nodes)g.thicknessRatio.Add(1);
        g.rods.Add(new PlantGeometry.Rod{a=4,b=5,radius=.01f});
        g.faces.Add(new PlantGeometry.Face{a=0,b=1,c=2});
        g.faces.Add(new PlantGeometry.Face{a=1,b=0,c=3});
        void Check(Vector3 center,int count,int region)
        {
            using var s=new VbdSheet(plant:new RodPlant(g));
            var c=s.contact;
            c.Configure(ObstacleShape.Sphere,center,Quaternion.identity,.02f);
            s.QueryContacts();
            if(c.faces!=count)throw new Exception($"Expected {count} active faces, got {c.faces}");
            foreach(var hit in c.hits)
                if(hit.depth>0 && hit.region!=region)throw new Exception($"Expected region {region}, got {hit.region}");
        }
        Check(Vector3.up*.027f,1,2);
        Check(Vector3.left+Vector3.up*.027f,1,1);
        g.nodes[2]+=Vector3.up*.5f;g.nodes[3]+=Vector3.up*.5f;
        Check(Vector3.up*.027f,2,3);
        Debug.Log("OGC shared edge, shared vertex and two-sided fold checks passed.");
    }
    [MenuItem("Tools/Foliage/Check primitive collisions")]
    public static void Run()
    {
        if(!Application.isPlaying) throw new InvalidOperationException("Run the foliage lab first.");
        var geometry=PlantGeometry.Generate(PlantSettings.Default);
        foreach(ContactModel model in new[]{ContactModel.Penalty,ContactModel.OGC})
        foreach(ObstacleShape shape in new[]{ObstacleShape.Sphere,ObstacleShape.Capsule,ObstacleShape.Box})
        {
            Debug.Log(Push(geometry,shape,false,model));
            Debug.Log(Push(geometry,shape,true,model));
        }
    }

    public static string Push(PlantGeometry g,ObstacleShape shape,bool stem,ContactModel model=ContactModel.Penalty)
    {
        using var s=new VbdSheet(plant:new RodPlant(g));
        s.gravityScale=0;
        var c=s.contact;
        c.model=model;
        var clock=new System.Diagnostics.Stopwatch();
        int id=stem?6:g.rods.Count+g.faces.FindIndex(f=>f.part==2 && f.a==g.bladeRows[0][4][0]);
        if(!stem && id<g.rods.Count)throw new Exception("Missing margin face.");
        var feature=c.features[id];
        Vector3 a=s.rest[feature.a], b=s.rest[feature.b];
        Vector3 axis=stem?Vector3.back:Vector3.Cross(b-a,s.rest[feature.c]-a).normalized;
        Vector3 sample=stem?(a+b)*.5f:(a+b+s.rest[feature.c])/3;
        const float radius=.065f;
        Quaternion orientation=Quaternion.Euler(12,22,19);
        Vector3 start=sample+axis*.19f;
        c.Configure(shape,start,orientation,radius);
        float peakDepth=0,strain=0,motion=0,parentMotion=0,released=0;
        int count=0;bool onFeature=false;
        int parent=g.joints[0];
        void Step()
        {
            clock.Start();s.Step(1f/60);clock.Stop();
            if(s.badVertex>=0)throw new Exception($"Non-finite state at node {s.badVertex}");
            foreach(var h in c.hits)
                if(!float.IsFinite(h.depth) || h.depth>0 && (Mathf.Abs(h.normal.sqrMagnitude-1)>.002f ||
                    Mathf.Abs(h.weights.x+h.weights.y+h.weights.z-1)>.001f))throw new Exception("Invalid contact geometry");
            peakDepth=Math.Max(peakDepth,c.physicalPenetration);
            count=Math.Max(count,stem?c.rods:c.faces);
            onFeature|=c.hits[id].depth>0;
            foreach(var e in s.edges)
                strain=Math.Max(strain,Mathf.Abs(Vector3.Distance(s.positions[e.x],s.positions[e.y])/Vector3.Distance(s.rest[e.x],s.rest[e.y])-1));
            foreach(var r in g.rods)
                strain=Math.Max(strain,Mathf.Abs(Vector3.Distance(s.positions[r.a],s.positions[r.b])/Vector3.Distance(s.rest[r.a],s.rest[r.b])-1));
            Vector3 now=stem?(s.positions[feature.a]+s.positions[feature.b])*.5f:
                (s.positions[feature.a]+s.positions[feature.b]+s.positions[feature.c])/3;
            released=Vector3.Distance(now,sample);
            motion=Math.Max(motion,released);
            parentMotion=Math.Max(parentMotion,Vector3.Distance(s.positions[parent],s.rest[parent]));
        }
        Step();
        if(c.faces+c.rods!=0)throw new Exception("Fixture must start clear of all foliage.");
        for(int i=1;i<=60;i++){c.target=start-axis*.17f*(i/60f);Step();}
        for(int i=0;i<30;i++)Step();
        float shortfall=Vector3.Distance(c.center,c.target);
        for(int i=1;i<=60;i++){c.target=start-axis*.17f*(1-i/60f);Step();}
        for(int i=0;i<480;i++)Step();
        if(!onFeature || count==0 || motion<.005f || released>=motion || c.rods+c.faces!=0 ||
            s.positions[0]!=s.rest[0])throw new Exception($"{shape} {(stem?"rod":"leaf")} contact {onFeature}, count {count}, motion {motion}, released {released}, active {c.rods+c.faces}");
        return $"{model} {shape} {(stem?"rod":"leaf")}: peak post-solve penetration {peakDepth*1000:F2} mm, peak edge strain {strain:P2}, pushed point {motion*1000:F1} mm, stem junction {parentMotion*1000:F2} mm, remaining displacement {released*1000:F2} mm, maximum simultaneous contacts {count}, push target shortfall {shortfall*1000:F2} mm, {clock.Elapsed.TotalMilliseconds/s.Steps:F2} ms/step.";
    }

    [MenuItem("Tools/Foliage/Check stationary sphere wrap")]
    public static void Wrap()
    {
        foreach(ContactModel model in new[]{ContactModel.Penalty,ContactModel.OGC})
            Debug.Log(WrapModel(model));
    }
    [MenuItem("Tools/Foliage/Check cylinder wrap and narrow gap")]
    public static void CheckPassages()
    {
        var lab=UnityEngine.Object.FindFirstObjectByType<FoliageLab>();
        if(!Application.isPlaying || lab==null)throw new InvalidOperationException("Run the foliage lab first.");
        var previous=lab.settings;
        bool changePlant=!previous.Equals(PlantSettings.Default);
        void Load(PlantSettings settings)
        {
            lab.SetSheetMode(false);
            lab.settings=settings;lab.Rebuild();lab.OpenStems();
        }
        try
        {
            if(changePlant)Load(PlantSettings.Default);
            Debug.Log(WrapModel(ContactModel.OGC,true));
            Debug.Log(PullThroughGap());
        }
        finally{if(changePlant)Load(previous);}
    }
    public static string WrapModel(ContactModel model,bool barrel=false)
    {
        var lab=UnityEngine.Object.FindFirstObjectByType<FoliageLab>();
        if(!Application.isPlaying || lab==null)throw new InvalidOperationException("Run the foliage lab first.");
        bool paused=lab.paused;
        try
        {
            lab.paused=true;lab.OpenStems();
            var s=lab.Sheet;var c=s.contact;s.gravityScale=0;
            c.model=model;
            // Exercise the collider mouse path in free space before the stationary-obstacle test.
            Vector3 free=Vector3.up*lab.settings.height*.54f+lab.view.transform.right*lab.settings.height*.47f;
            lab.SetObstacle(ObstacleShape.Sphere,free,Quaternion.identity);
            Vector2 cursor=lab.view.WorldToScreenPoint(free);
            if(!lab.BeginLabDrag(lab.view.ScreenPointToRay(cursor)))throw new Exception("Collider pick missed.");
            lab.MoveObstacleTarget(cursor+Vector2.right*30,.5f);
            for(int i=0;i<45;i++)lab.Advance();
            if(Vector3.Distance(free,c.center)<.01f || s.grabFace>=0 || s.grabRod>=0)throw new Exception("Collider drag failed.");
            lab.EndDrag();lab.ResetState();

            var rows=lab.Geometry.bladeRows[0];
            var mid=rows[rows.Count*2/3];
            Vector3 support=s.rest[mid[mid.Length/2]];
            int tip=rows[rows.Count-1][0];
            float length=Vector3.Distance(s.rest[tip],s.rest[rows[0][0]]);
            float radius=.14f*length;
            Vector3 center=support-Vector3.up*(radius+.060f*length);
            Vector3 barrelAxis=Vector3.Cross(Vector3.up,s.rest[tip]-s.rest[rows[0][0]]).normalized;
            lab.SetObstacle(barrel?ObstacleShape.Capsule:ObstacleShape.Sphere,center,
                barrel?Quaternion.FromToRotation(Vector3.up,barrelAxis):Quaternion.identity,radius);
            s.QueryContacts();
            if(c.physicalPenetration>1e-6f)throw new Exception($"Wrap starts overlapped by {c.physicalPenetration*1000:F2} mm");
            int face=-1;
            for(int i=0;i<lab.Geometry.faces.Count;i++)
            {
                var f=lab.Geometry.faces[i];if(f.a!=tip && f.b!=tip && f.c!=tip)continue;
                Vector3 w=Vector3.one*.15f;w[f.a==tip?0:f.b==tip?1:2]=.7f;
                cursor=lab.view.WorldToScreenPoint(s.Point(i,w));
                if(lab.BeginLabDrag(lab.view.ScreenPointToRay(cursor)) && s.grabFace==i){face=i;break;}
            }
            if(face<0)throw new Exception("Leaf tip not visible.");
            Vector3 original=s.Point(face,s.weights);
            Vector2 end=lab.view.WorldToScreenPoint(original-Vector3.up*length*.48f);
            float depth=0,arc=0,travel=0,strain=0;
            bool OnSupport(PlantContact.Hit hit)=>hit.depth>0 && (!barrel ||
                (Mathf.Abs(Vector3.Dot(hit.witness-center,barrelAxis))<c.extents.y-radius-.001f &&
                 Mathf.Abs(Vector3.Dot(hit.normal,barrelAxis))<.01f));
            for(int i=1;i<=180;i++)
            {
                lab.MoveSheetTarget(Vector2.Lerp(cursor,end,i/120f),0);lab.Advance();
                travel=Mathf.Max(travel,Vector3.Distance(s.Point(face,s.weights),original));
                depth=Mathf.Max(depth,c.physicalPenetration);
                foreach(var edge in s.edges)
                    strain=Mathf.Max(strain,Mathf.Abs(Vector3.Distance(s.positions[edge.x],s.positions[edge.y])/
                        Vector3.Distance(s.rest[edge.x],s.rest[edge.y])-1));
                for(int j=c.rodCount;j<c.hits.Length;j++)
                {
                    if(!OnSupport(c.hits[j]) || lab.Geometry.faces[j-c.rodCount].part!=2)continue;
                    for(int k=j+1;k<c.hits.Length;k++)
                        if(OnSupport(c.hits[k]) && lab.Geometry.faces[k-c.rodCount].part==2)
                            arc=Mathf.Max(arc,Vector3.Angle(c.hits[j].normal,c.hits[k].normal));
                }
            }
            if(c.center!=center)throw new Exception("Stationary collider moved during the wrap.");
            lab.EndDrag();
            for(int i=0;i<60;i++)lab.Advance();
            float supported=Vector3.Distance(original,s.Point(face,s.weights));
            // Move the support clear before measuring free recovery, using the live kinematic path.
            c.target=center-Vector3.up*(length*.85f);
            for(int i=0;i<120;i++)lab.Advance();
            for(int i=0;i<480;i++)lab.Advance();
            float remaining=Vector3.Distance(original,s.Point(face,s.weights));
            string surface=barrel?"cylinder barrel":"sphere";
            if(s.badVertex>=0 || travel<.01f || arc<10 || remaining>=travel || c.faces+c.rods!=0 ||
                (barrel && (depth>.0001f || strain>.10f)))
                throw new Exception($"Stationary {surface} check: travel {travel}, arc {arc}, supported {supported}, remaining {remaining}, depth {depth}, residual contacts {c.faces+c.rods}.");
            return $"{model} stationary {surface}: simultaneous contact normals spanned {arc:F1}°, peak penetration {depth*1000:F2} mm, peak blade strain {strain:P2}, tip travel {travel*1000:F1} mm, supported after release {supported*1000:F1} mm, remaining after withdrawal {remaining*1000:F1} mm.";
        }
        finally {lab.OpenStems();lab.paused=paused;}
    }

    public static string PullThroughGap()
    {
        var g=PlantGeometry.Generate(PlantSettings.Default);
        using var s=new VbdSheet(plant:new RodPlant(g));
        s.gravityScale=0;
        int id=g.rods.FindLastIndex(r=>r.part==0);
        var rod=g.rods[id];
        Vector3 w=new Vector3(.25f,.75f,0);
        Vector3 Point()=>s.positions[rod.a]*w.x+s.positions[rod.b]*w.y;
        Vector3 start=Point(),up=(s.rest[rod.b]-s.rest[rod.a]).normalized;
        var rotation=Quaternion.LookRotation(Vector3.ProjectOnPlane(Vector3.forward,up),up);
        const float wall=.035f;
        float gap=2*rod.radius+1.98f*PlantContact.Offset;
        Vector3 center=start+rotation*Vector3.forward*.09f;
        var c=s.contact;c.Configure(ObstacleShape.Gate,center,rotation,wall,gap);
        s.QueryContacts();
        if(c.physicalPenetration>1e-6f)throw new Exception("Gap must start clear.");
        s.grabRod=id;s.weights=w;
        int both=0;bool traversed=false;float depth=0,strain=0,far=float.NegativeInfinity;
        for(int i=0;i<320;i++)
        {
            s.target=start+rotation*Vector3.forward*(.24f*Mathf.Clamp01(i/180f));
            s.Step(1f/60);
            Vector3 local=Quaternion.Inverse(rotation)*(Point()-center);
            bool inSlot=Mathf.Abs(local.x)<=gap*.5f-rod.radius+.0001f &&
                        Mathf.Abs(local.y)<c.extents.y-rod.radius;
            if(inSlot && Mathf.Abs(local.z)<c.extents.z &&
               c.hits[id].depth>0 && c.hits[c.features.Length+id].depth>0)both++;
            if(inSlot && both>0 && local.z>c.extents.z+rod.radius)traversed=true;
            far=Mathf.Max(far,local.z-c.extents.z);
            depth=Mathf.Max(depth,c.physicalPenetration);
            foreach(var r in g.rods)strain=Mathf.Max(strain,Mathf.Abs(Vector3.Distance(s.positions[r.a],s.positions[r.b])/
                Vector3.Distance(s.rest[r.a],s.rest[r.b])-1));
            if(s.badVertex>=0)throw new Exception("Non-finite state during narrow-gap pull.");
        }
        s.grabRod=-1;
        for(int i=0;i<660;i++)
        {
            // First let the stem rest against the gate, then lift the
            // fixture over its free end before measuring elastic recovery.
            if(i==60)c.target=center+rotation*Vector3.up*.35f;
            s.Step(1f/60);
            depth=Mathf.Max(depth,c.physicalPenetration);
            if(s.badVertex>=0)throw new Exception("Non-finite state after narrow-gap release.");
        }
        float remaining=Vector3.Distance(Point(),start);
        string report=$"Stem diameter {2*rod.radius*1000:F1} mm, gap {gap*1000:F1} mm, bilateral ticks {both}, traversed {traversed}, beyond exit {far*1000:F1} mm, peak penetration {depth*1000:F3} mm, peak rod strain {strain:P2}, recovery error {remaining*1000:F1} mm, remaining contacts {c.rods+c.faces}.";
        if(!traversed || depth>.0001f || strain>.1f || remaining>.06f || c.rods+c.faces!=0 ||
            Vector3.Distance(c.center,c.target)>.0001f)throw new Exception(report);
        return report;
    }
}
