using System.Collections.Generic;
using UnityEngine;

// The navigation point follows the compact sphere. The body grows outward
// from its support and can change its height and girth independently.
public sealed class SphereEnvelope
{
    readonly float core;
    readonly SurfaceWorld surface;
    readonly List<SurfaceWorld.FaceBox> obstacles=new();
    readonly List<SurfaceWorld.FaceBox> nearby=new();
    Vector3 cacheOrigin;bool cached;
    readonly MeshCollider collider;
    Vector3 up;
    public Vector3 Center { get; private set; }
    public float Width { get; private set; }
    public float Height { get; private set; }
    [System.Serializable] public struct Pose
    {
        public Vector3 center,up;
        public float width,height;
        public bool Touches(Vector3 eye)
        {
            Vector3 delta=eye-Vector3.up*.45f-center;
            const float playerRadius=.32f;
            if(delta.sqrMagnitude>Mathf.Pow(Mathf.Max(width,height)+playerRadius,2))return false;
            float y=Vector3.Dot(delta,up),x=Vector3.ProjectOnPlane(delta,up).magnitude;
            float w2=width*width,h2=height*height;
            if(x*x/w2+y*y/h2<=1)return true;
            // Closest point on the meridian ellipse, expanded by the player radius.
            float lo=0,hi=Mathf.Max(width,height)*delta.magnitude;
            for(int i=0;i<14;i++){
                float m=(lo+hi)*.5f;
                if(w2*x*x/((m+w2)*(m+w2))+h2*y*y/((m+h2)*(m+h2))>1)lo=m;else hi=m;
            }
            float dx=x*hi/(hi+w2),dy=y*hi/(hi+h2);
            return dx*dx+dy*dy<playerRadius*playerRadius;
        }
        public bool Intercepts(Vector3 eye,Vector3 direction,CargoPhysics.State cargo)
        {
            Vector3 origin=eye-center;
            float inv=1/(width*width),axis=1/(height*height)-inv;
            float y=Vector3.Dot(origin,up),dy=Vector3.Dot(direction,up);
            float a=inv+axis*dy*dy,b=Vector3.Dot(origin,direction)*inv+axis*y*dy;
            float c=origin.sqrMagnitude*inv+axis*y*y-1,disc=b*b-a*c;
            if(disc<0)return false;
            float root=Mathf.Sqrt(disc);
            return (-b+root)>=0 && (-b-root)/a<=SphereSight.ObstructionRange(eye,direction,cargo);
        }
    }
    public Pose Current=>new Pose{center=Center,up=up,width=Width,height=Height};

    public SphereEnvelope(SurfaceWorld surface,float core,GameObject visual)
    {
        this.surface=surface;this.core=core;
        visual.GetComponent<SphereCollider>().enabled=false;
        visual.layer=2;
        collider=visual.AddComponent<MeshCollider>();
        collider.sharedMesh=visual.GetComponent<MeshFilter>().sharedMesh;
    }

    bool Fits(Vector3 center,Vector3 normal,float width,float height)
    {
        foreach(var box in obstacles){
            float distance=box.Distance(center,out var direction);
            float cosine=Vector3.Dot(direction,normal);
            float extentSquared=width*width+(height*height-width*width)*cosine*cosine;
            if(distance<0 || distance*distance+.00001f<extentSquared)return false;
        }
        return true;
    }

    // A convex blocker that covers all corners of the body's enclosing box
    // also covers the silhouette between them.
    bool Hidden(Vector3 eye,Vector3 center,Vector3 normal,float width,float height,Collider wall,Bounds box,bool useBox)
    {
        Vector3 right=Vector3.Cross(normal,(center-eye).normalized);
        if(right.sqrMagnitude<.0001f)right=Vector3.Cross(normal,Mathf.Abs(normal.y)<.9f?Vector3.up:Vector3.right);
        right=right.normalized*width;
        Vector3 forward=Vector3.Cross(right,normal);
        for(int i=0;i<8;i++){
            Vector3 corner=center+((i&1)==0?right:-right)+((i&2)==0?forward:-forward)+((i&4)==0?height:-height)*normal;
            Vector3 delta=corner-eye;float distance=delta.magnitude;
            var ray=new Ray(eye,delta/distance);
            if(useBox){if(!box.IntersectRay(ray,out float t) || t>=distance)return false;}
            else if(!wall || !wall.Raycast(ray,out _,distance))return false;
        }
        return true;
    }

    public void Update(Vector3 anchor,Vector3 eye,CargoPhysics.State cargo,float dt,bool reset=false,Vector3? upcoming=null)
    {
        Apply(Predict(Current,anchor,eye,cargo,dt,reset,upcoming));
    }

    public void Apply(Pose next)
    {
        up=next.up;Width=next.width;Height=next.height;Center=next.center;
        var tform=collider.transform;
        tform.SetPositionAndRotation(Center,Quaternion.FromToRotation(Vector3.up,up));
        tform.localScale=new Vector3(Width,Height,Width)*2;
    }

    public Pose Predict(Pose prior,Vector3 anchor,Vector3 eye,CargoPhysics.State cargo,float dt,bool reset=false,Vector3? upcoming=null)
    {
        surface.Distance(anchor,out var normal,cargo);
        var up=reset || prior.up==Vector3.zero?normal:Vector3.RotateTowards(prior.up,normal,12*dt,0).normalized;
        float large=core*3;
        Vector3 ahead=upcoming??anchor;
        surface.Distance(ahead,out var nextNormal,cargo);
        obstacles.Clear();
        if(!cached || (anchor-cacheOrigin).sqrMagnitude>9 || (ahead-cacheOrigin).sqrMagnitude>9){
            nearby.Clear();cached=true;cacheOrigin=anchor;
            float reach=large*2+Mathf.Max(3,(ahead-anchor).magnitude);
            foreach(var box in surface.boxes)if(box.Distance(anchor,out _)<=reach)nearby.Add(box);
        }
        foreach(var box in nearby)
            if(box.Distance(anchor,out _)<=large*2 || box.Distance(ahead,out _)<=large*2)obstacles.Add(box);
        for(int i=0;i<cargo.Count;i++)obstacles.Add(new SurfaceWorld.FaceBox{center=cargo.Get(i).center,half=Vector3.one*CargoPhysics.Half,aligned=true});

        Collider wall=null;Bounds blocker=default;bool useBox=false;
        bool hiding=!SphereSight.Exposed(eye,anchor,out _,core,cargo);
        if(hiding){
            Vector3 delta=anchor-eye;
            var ray=new Ray(eye,delta.normalized);
            float depth=delta.magnitude;
            if(Physics.Raycast(ray,out var hit,depth,1<<8)){wall=hit.collider;depth=hit.distance;}
            for(int i=0;i<cargo.Count;i++){
                var box=cargo.Get(i).Box;
                if(box.IntersectRay(ray,out float t) && t<depth){blocker=box;useBox=true;depth=t;}
            }
        }
        bool Allowed(float w,float h,bool cover)
        {
            Vector3 center=anchor+up*(h-core);
            return Fits(center,up,w,h) && (!cover ||
                ((!upcoming.HasValue || Fits(ahead+nextNormal*(h-core),nextNormal,w,h)) &&
                 (!hiding || Hidden(eye,center,up,w,h,wall,blocker,useBox))));
        }
        float Limit(float low,float high,System.Func<float,bool> allowed)
        {
            if(allowed(high))return high;
            for(int i=0;i<7;i++){float mid=(low+high)*.5f;if(allowed(mid))low=mid;else high=mid;}
            return low;
        }
        float hGoal=large,wGoal=large;
        if(!Allowed(large,large,true)){
            hGoal=Limit(core,large,h=>Allowed(core,h,true));
            wGoal=Limit(core,large,w=>Allowed(w,hGoal,true));
        }
        float Animate(float value,float target)=>reset?target:Mathf.MoveTowards(value,target,dt*core*(target<value?12:5));
        float hNext=Animate(prior.height,hGoal),wNext=Animate(prior.width,wGoal);
        if(!Allowed(wNext,hNext,false)){
            hNext=Limit(core,hNext,h=>Allowed(core,h,false));
            wNext=Limit(core,wNext,w=>Allowed(w,hNext,false));
        }
        return new Pose{width=wNext,height=hNext,center=anchor+up*(hNext-core),up=up};
    }

    public bool Rail(Vector3 eye,Vector3 aim,CargoPhysics.State cargo,out float range)
    {
        range=SphereSight.ObstructionRange(eye,aim,cargo);
        Physics.SyncTransforms();
        if(!collider.Raycast(new Ray(eye,aim),out var hit,range))return false;
        range=hit.distance;return true;
    }

    public bool Touches(Vector3 eye)=>Current.Touches(eye);
}
