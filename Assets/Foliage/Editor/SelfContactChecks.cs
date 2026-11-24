using System;
using UnityEditor;
using UnityEngine;
using Foliage;

public static class SelfContactChecks
{
    [MenuItem("Tools/Foliage/Check plant self-collision")]
    public static void Run()
    {
        if(!Application.isPlaying)throw new InvalidOperationException("Enter Play mode first.");
        for(int kind=0;kind<3;kind++)Debug.Log(Pull(kind));
    }
    // Isolate the contact load path. The support rod is clamped and the test
    // element can move freely. Gravity and external obstacles are disabled.
    public static string Pull(int kind)
        =>Drag(kind,0);

    [MenuItem("Tools/Foliage/Check fast drag safeguards")]
    public static void FastDrags()
    {
        if(!Application.isPlaying)throw new InvalidOperationException("Enter Play mode first.");
        for(int kind=0;kind<2;kind++)
            foreach(int ticks in new[]{8,2,1})Debug.Log(Drag(kind,ticks));
    }
    public static string Drag(int kind,int approachTicks)
    {
        bool supportLeaf=kind==0,movingRod=kind==2;
        var g=new PlantGeometry();
        int Node(Vector3 p){g.nodes.Add(p);g.thicknessRatio.Add(1);return g.nodes.Count-1;}
        void Rod(int a,int b,float radius)=>g.rods.Add(new PlantGeometry.Rod{a=a,b=b,radius=radius});
        int left=Node(new Vector3(-.2f,0,0)),right=Node(new Vector3(.2f,0,0));
        Rod(left,right,.009f);
        float z=supportLeaf?.08f:0;
        if(supportLeaf)
        {
            int apex=Node(new Vector3(0,0,.24f));
            Rod(right,apex,.007f);
            g.faces.Add(new PlantGeometry.Face{a=left,b=right,c=apex});
        }
        int a=Node(new Vector3(movingRod?0:-.025f,.04f,z-.025f));
        int b=Node(new Vector3(movingRod?0:.025f,.04f,z+(movingRod?.025f:-.025f)));
        if(movingRod)Rod(a,b,.006f);
        else g.faces.Add(new PlantGeometry.Face{a=a,b=b,c=Node(new Vector3(0,.04f,z+.05f))});
        using var s=new VbdSheet(plant:new RodPlant(g));
        s.gravityScale=0;
        Vector3 w=movingRod?new Vector3(.5f,.5f,0):Vector3.one/3;
        Vector3 origin=movingRod?(s.rest[a]+s.rest[b])/2:s.Point(g.faces.Count-1,w);
        if(approachTicks>0)
        {
            float Press(bool dat,out float rejected)
            {
                s.Reset();s.selfContact.dat=dat;s.grabFace=g.faces.Count-1;s.weights=w;
                s.grabK=30;s.grabLimit=2;rejected=0;
                float minimum=float.PositiveInfinity;
                for(int i=1;i<=approachTicks+12;i++)
                {
                    s.target=origin+Vector3.down*(.08f*Mathf.Min(1,(float)i/approachTicks));
                    s.Step(1f/60);
                    if(s.badVertex>=0)throw new Exception("Non-finite fast drag");
                    var p=s.Point(s.grabFace,w);
                    float clearance=BladeRodGap();
                    if(p.y<0)clearance=Mathf.Min(clearance,p.y);
                    if(supportLeaf)
                    {
                        var face=g.faces[0];
                        Vector3 n=Vector3.Cross(s.positions[face.c]-s.positions[face.a],s.positions[face.b]-s.positions[face.a]).normalized;
                        var blade=g.faces[s.grabFace];
                        float Height(int v)=>Vector3.Dot(s.positions[v]-s.positions[face.a],n);
                        clearance=Mathf.Min(Height(blade.a),Mathf.Min(Height(blade.b),Height(blade.c)))-.004f;
                    }
                    minimum=Mathf.Min(minimum,clearance);
                    if(dat)rejected=Mathf.Max(rejected,s.peakRejectedMotion);
                }
                return minimum;
            }
            float baseline=Press(false,out _),limited=Press(true,out float rejected);
            string report=$"{(supportLeaf?"Leaf/leaf":"Leaf/stem")}, {4.8f/approachTicks:F1} m/s target: signed clearance {baseline*1000:F2} mm without DAT, {limited*1000:F2} mm with DAT, largest rejected update {rejected*1000:F2} mm.";
            if(limited<-.00005f || (approachTicks==1 && (baseline>=0 || rejected<=0)))throw new Exception(report);
            return report;
        }
        // The clamped rod lies on X. Projecting the moving triangle into YZ
        // gives its distance to the rod axis, even as it rolls around the rod.
        float BladeRodGap()
        {
            var face=g.faces[g.faces.Count-1];
            Vector2 Plane(int v)=>new Vector2(s.positions[v].y,s.positions[v].z);
            Vector2 p=Plane(face.a),q=Plane(face.b),r=Plane(face.c);
            float Side(Vector2 v,Vector2 end)=>v.x*end.y-v.y*end.x;
            float u=Side(p,q),v=Side(q,r),w=Side(r,p);
            if(Mathf.Min(u,Mathf.Min(v,w))>0 || Mathf.Max(u,Mathf.Max(v,w))<0)return -.011f;
            float Edge(Vector2 start,Vector2 end)
            {
                Vector2 e=end-start;
                return (start+Mathf.Clamp01(-Vector2.Dot(start,e)/Mathf.Max(1e-15f,e.sqrMagnitude))*e).magnitude;
            }
            return Mathf.Min(Edge(p,q),Mathf.Min(Edge(q,r),Edge(r,p)))-.011f;
        }
        float Cross(bool enable,out int hits,out float peak,out float slide)
        {
            s.Reset();s.selfContact.enabled=enable;
            if(movingRod)s.grabRod=g.rods.Count-1;
            else s.grabFace=g.faces.Count-1;
            s.weights=w;s.grabK=.5f;s.grabLimit=.025f;
            float minimum=float.PositiveInfinity;hits=0;peak=0;slide=0;
            for(int i=0;i<210;i++)
            {
                float down=Mathf.Clamp01(i/75f),side=Mathf.Clamp01((i-75)/60f);
                float lift=Mathf.Clamp01((i-145)/48f);
                s.target=origin+new Vector3(.045f*side,-.08f*down+.13f*lift,0);
                s.Step(1f/60);
                if(s.badVertex>=0)throw new Exception($"Invalid self-contact state at {s.badVertex}");
                var p=movingRod?(s.positions[a]+s.positions[b])/2:s.Point(g.faces.Count-1,w);
                float signed=p.y;
                if(kind==1)signed=BladeRodGap();
                if(supportLeaf)
                {
                    var f=g.faces[0];var n=Vector3.Cross(s.positions[f.c]-s.positions[f.a],s.positions[f.b]-s.positions[f.a]).normalized;
                    signed=Vector3.Dot(p-s.positions[f.a],n);
                }
                if(i<=145)minimum=Mathf.Min(minimum,signed);
                if(i==145)slide=p.x-origin.x;
                hits=Mathf.Max(hits,s.selfContact.active);peak=Mathf.Max(peak,s.selfContact.penetration);
            }
            s.grabFace=s.grabRod=-1;
            for(int i=0;i<20;i++)s.Step(1f/60);
            if(s.selfContact.active!=0)throw new Exception("Contact persisted after separation.");
            return minimum;
        }
        float free=Cross(false,out _,out _,out _);
        float guarded=Cross(true,out int hits,out float peak,out float slide);
        string result=$"{(supportLeaf?"Leaf/leaf":movingRod?"Rod/rod":"Leaf/rod")}: minimum signed separation {guarded*1000:F1} mm (disabled {free*1000:F1}), slide {slide*1000:F1} mm, peak overlap {peak*1000:F2} mm, active witnesses {hits}.";
        if(free>=0 || guarded<=0 || hits==0 || slide<.01f)throw new Exception(result);
        return result;
    }
}
