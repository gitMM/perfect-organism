using System.Collections.Generic;
using UnityEngine;

// Signed distances to the oriented boxes that make up the graybox. The zero
// surface for a sphere center is the union of the boxes expanded by its radius.
public sealed class SurfaceWorld
{
    public struct FaceBox
    {
        public Vector3 center,half;
        public Quaternion rotation,inverse;
        public bool aligned;
        public float Distance(Vector3 world,out Vector3 normal)
        {
            Vector3 p=world-center;if(!aligned)p=inverse*p;
            Vector3 q=new(Mathf.Abs(p.x)-half.x,Mathf.Abs(p.y)-half.y,Mathf.Abs(p.z)-half.z);
            var outside=new Vector3(Mathf.Max(0,q.x),Mathf.Max(0,q.y),Mathf.Max(0,q.z));
            float length=outside.magnitude;
            if(length>.000001f){
                normal=new Vector3(outside.x*Mathf.Sign(p.x),outside.y*Mathf.Sign(p.y),outside.z*Mathf.Sign(p.z))/length;
                if(!aligned)normal=rotation*normal;
                return length;
            }
            int axis=q.x>q.y?(q.x>q.z?0:2):(q.y>q.z?1:2);
            var local=Vector3.zero;local[axis]=Mathf.Sign(p[axis]);normal=aligned?local:rotation*local;return q[axis];
        }
    }

    public const float Skin=.025f;
    public readonly List<FaceBox> boxes=new();
    readonly Dictionary<Vector3Int,List<int>> nearby=new();
    readonly float offset;
    static Vector3Int Cell(Vector3 p)=>Vector3Int.FloorToInt(p/3);

    public SurfaceWorld(float radius)
    {
        offset=radius+Skin;
        foreach(var box in Object.FindObjectsByType<BoxCollider>(FindObjectsSortMode.InstanceID)){
            if(!box.enabled || box.isTrigger || box.gameObject.layer!=8)continue;
            var t=box.transform;var scale=t.lossyScale;
            var half=Vector3.Scale(box.size*.5f,new Vector3(Mathf.Abs(scale.x),Mathf.Abs(scale.y),Mathf.Abs(scale.z)));
            int id=boxes.Count;
            boxes.Add(new FaceBox{center=t.TransformPoint(box.center),half=half,rotation=t.rotation,inverse=Quaternion.Inverse(t.rotation),aligned=t.rotation==Quaternion.identity});
            Bounds b=box.bounds;b.Expand((offset+1)*2);
            var lo=Cell(b.min);var hi=Cell(b.max);
            for(int x=lo.x;x<=hi.x;x++)for(int y=lo.y;y<=hi.y;y++)for(int z=lo.z;z<=hi.z;z++){
                var key=new Vector3Int(x,y,z);
                if(!nearby.TryGetValue(key,out var list))nearby[key]=list=new();
                list.Add(id);
            }
        }
    }

    static FaceBox CargoBox(CargoPhysics.Item crate)=>new(){center=crate.center,half=Vector3.one*CargoPhysics.Half,rotation=Quaternion.identity,inverse=Quaternion.identity,aligned=true};
    public float Distance(Vector3 p,out Vector3 normal,CargoPhysics.State crate=default)
        => Distance(p,out normal,crate,false);

    // A small fillet joins perpendicular static surfaces. At its midpoint
    // the center can float up to 8 cm above its usual clearance.
    const float CornerBlend=.32f;
    float Distance(Vector3 p,out Vector3 normal,CargoPhysics.State crate,bool glide)
    {
        float best=float.PositiveInfinity,second=float.PositiveInfinity;normal=Vector3.up;
        Vector3 other=Vector3.zero;
        if(nearby.TryGetValue(Cell(p),out var list))foreach(int i in list){
            float d=boxes[i].Distance(p,out var n);
            if(d<best){
                if(glide && Mathf.Abs(Vector3.Dot(n,normal))<.35f){second=best;other=normal;}
                best=d;normal=n;
            }else if(glide && d<second && Mathf.Abs(Vector3.Dot(n,normal))<.35f){second=d;other=n;}
        }
        if(glide && second-best<CornerBlend && Mathf.Abs(Vector3.Dot(normal,other))<.35f){
            float mix=.5f*(1-(second-best)/CornerBlend);
            normal=Vector3.Lerp(normal,other,mix).normalized;
            best-=CornerBlend*mix*mix;
        }
        for(int i=0;i<crate.Count;i++){float d=CargoBox(crate.Get(i)).Distance(p,out var n);if(d<best){best=d;normal=n;}}
        return best;
    }

    public bool Attach(Vector3 p,float reach,out Vector3 result,CargoPhysics.State crate=default)
        => Attach(p,reach,out result,crate,false);

    bool Attach(Vector3 p,float reach,out Vector3 result,CargoPhysics.State crate,bool glide)
    {
        result=p;
        for(int i=0;i<8;i++){
            float d=Distance(result,out var n,crate,glide);
            if(d>offset+reach)return false;
            float correction=offset-d;
            if(Mathf.Abs(correction)<.0005f)return true;
            result+=n*correction;
            if((result-p).sqrMagnitude>(reach+.001f)*(reach+.001f))return false;
        }
        return Mathf.Abs(Distance(result,out _,crate,glide)-offset)<.002f;
    }

    public bool Walk(Vector3 from,Vector3 target,ref Vector3 velocity,float speed,float acceleration,float dt,bool brake,out Vector3 next,CargoPhysics.State crate=default,bool throughGuide=false)
    {
        next=from;
        Vector3 wish=brake?Vector3.zero:Vector3.ClampMagnitude((target-from)/FlightPlanner.Step,speed);
        if(throughGuide && !brake){
            Vector3 delta=target-from;
            wish=delta.normalized*Mathf.Min(speed,Mathf.Sqrt(2*acceleration*delta.magnitude));
        }
        float clearance=Distance(from,out var normal,crate,true);
        if(Mathf.Abs(clearance-offset)>CornerBlend*.25f+.035f)return false;
        Vector3 tangent=Vector3.ProjectOnPlane(wish,normal);
        if(tangent.sqrMagnitude>.0001f && Vector3.Dot(velocity,tangent)>0)
            tangent=tangent.normalized*Mathf.Max(tangent.magnitude,Mathf.Min(velocity.magnitude,wish.magnitude));
        // Carry momentum around the bend, including convex crate edges.
        var along=Vector3.ProjectOnPlane(velocity,normal);
        if(along.sqrMagnitude>.0001f && Vector3.Dot(along,tangent)>0)
            velocity=along.normalized*velocity.magnitude;
        velocity=Vector3.MoveTowards(velocity,tangent,acceleration*dt);
        if(!Attach(from+velocity*dt,.28f,out next,crate,true))return false;
        if((next-from).magnitude>speed*dt+.012f)return false;
        velocity=(next-from)/dt;
        return true;
    }
}
