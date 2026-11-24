using System;
using UnityEngine;
using UnityEngine.SceneManagement;

// One upright rigidbody in a manually stepped PhysX scene. Every branch restores
// its own snapshot before simulating. Only the live snapshot drives the visible crate.
public sealed class CargoPhysics : IDisposable
{
    public const float Half=.7f;
    public static readonly Vector3 Spawn=new(11.8f,3+Half+.01f,6);
    public static readonly Vector3[] Starts={
        Spawn,new(-11.85f,3.2f,4),new(1,4.7f,.75f),new(9,3.9f,13),new(-14.8f,3.7f,-11)
    };
    public static readonly Vector3[] DropDirections={Vector3.left,Vector3.right,Vector3.forward,Vector3.back,Vector3.right};
    public static readonly string[] SiteNames={"East terrace","West terrace","Transfer bridge","Rear dock","West shelf"};
    [Serializable] public struct Item{
        public Vector3 center,velocity;public bool asleep;
        public Bounds Box=>new(center,Vector3.one*(2*Half));
    }
    [Serializable] public struct State
    {
        public Vector3 center,velocity;
        public bool present, asleep;
        public Item[] extra;
        public int Count=>present?1+(extra?.Length??0):0;
        public Item Get(int i)=>i==0?new Item{center=center,velocity=velocity,asleep=asleep}:extra[i-1];
        public int Nearest(Vector3 p){
            int best=0;float nearest=float.PositiveInfinity;
            for(int i=0;i<Count;i++){float d=Get(i).Box.SqrDistance(p);if(d<nearest){nearest=d;best=i;}}
            return best;
        }
        public Bounds Box => new(center,Vector3.one*(Half*2));
    }
    public struct Motion
    {
        public Vector3 player,sphere;
        public bool crushed,playerPush,spherePush; public int crushId,playerPushId,spherePushId;
    }
    readonly Scene scene;
    readonly PhysicsScene physics;
    readonly System.Collections.Generic.List<Rigidbody> bodies=new();
    Item[] items; Vector3[] prior;
    readonly PhysicsMaterial material;
    public CargoPhysics()
    {
        scene=SceneManager.CreateScene("Crate simulation "+Guid.NewGuid().ToString("N"),new CreateSceneParameters(LocalPhysicsMode.Physics3D));
        physics=scene.GetPhysicsScene();
        foreach(var source in UnityEngine.Object.FindObjectsByType<BoxCollider>(FindObjectsSortMode.None)){
            if(!source.enabled || source.gameObject.layer!=8 || source.isTrigger)continue;
            var copy=new GameObject(source.name);
            SceneManager.MoveGameObjectToScene(copy,scene);
            copy.transform.SetPositionAndRotation(source.transform.TransformPoint(source.center),source.transform.rotation);
            var collider=copy.AddComponent<BoxCollider>();
            collider.size=Vector3.Scale(source.size,source.transform.lossyScale);
        }
        material=new PhysicsMaterial("Cargo friction"){dynamicFriction=.25f,staticFriction=.3f,frictionCombine=PhysicsMaterialCombine.Minimum};
        Physics.SyncTransforms();
    }
    public static State Layout(){
        var s=At(Starts[0]);s.center.y=3+Half;s.asleep=true;s.extra=new Item[Starts.Length-1];
        for(int i=1;i<Starts.Length;i++)s.extra[i-1]=new Item{center=Starts[i],asleep=true};
        return s;
    }
    void Capacity(int count){
        if(items==null || items.Length<count){items=new Item[count];prior=new Vector3[count];}
        while(bodies.Count<count){
            var box=new GameObject("Simulated cargo "+bodies.Count);SceneManager.MoveGameObjectToScene(box,scene);
            var shape=box.AddComponent<BoxCollider>();shape.size=Vector3.one*Half*2;shape.sharedMaterial=material;
            var rb=box.AddComponent<Rigidbody>();rb.mass=12;rb.constraints=RigidbodyConstraints.FreezeRotation;rb.sleepThreshold=.015f;
            bodies.Add(rb);
        }
    }
    public static State At(Vector3 center)=>new(){present=true,center=center};

    // Actor proxies use their actual height and a conservative box footprint.
    static bool Sweep(Bounds crate,Vector3 start,Vector3 end,Vector3 extent,out float travel,out Vector3 normal)
    {
        crate.Expand(extent*2);Vector3 delta=end-start;
        float length=delta.magnitude;normal=Vector3.zero;travel=0;
        if(length<.000001f)return false;
        Vector3 direction=delta/length;
        if(!crate.Contains(start) && (!crate.IntersectRay(new Ray(start,direction),out travel) || travel>length))return false;
        Vector3 at=start+direction*travel-crate.center;
        Vector3 gap=crate.extents-new Vector3(Mathf.Abs(at.x),Mathf.Abs(at.y),Mathf.Abs(at.z));
        if(gap.y<gap.x && gap.y<gap.z)normal=Vector3.up*Mathf.Sign(at.y);
        else if(gap.x<gap.z)normal=Vector3.right*Mathf.Sign(at.x);
        else normal=Vector3.forward*Mathf.Sign(at.z);
        return Vector3.Dot(direction,normal)<-.0001f;
    }
    static bool Push(ref Vector3 velocity,Bounds box,Vector3 from,Vector3 to,Vector3 extent,float dt)
    {
        if(!Sweep(box,from,to,extent,out _,out var normal) || normal.y!=0)return false;
        float speed=Mathf.Min(9,Vector3.Dot((to-from)/dt,-normal));
        float gain=speed-Vector3.Dot(velocity,-normal);
        if(gain<=.001f)return false;
        velocity-=normal*gain;return true;
    }
    static Vector3 Clip(Bounds box,Vector3 from,Vector3 to,Vector3 extent)
    {
        if(!Sweep(box,from,to,extent,out var distance,out var normal))return to;
        Vector3 contact=from+(to-from).normalized*Mathf.Max(0,distance-.001f);
        return contact+Vector3.ProjectOnPlane(to-contact,normal);
    }

    public Motion Advance(ref State state,Vector3 playerFrom,Vector3 playerTo,Vector3 sphereFrom,Vector3 sphereTo,float radius,float dt)
    {
        var result=new Motion{player=playerFrom,sphere=sphereFrom};
        Vector3 halfPlayer=new(.31f,.90f,.31f),offset=Vector3.down*.61f,halfSphere=Vector3.one*radius;
        int steps=Mathf.Max(1,Mathf.CeilToInt(dt*60-.0001f));float h=dt/steps;
        int count=state.Count;if(count==0){result.player=playerTo;result.sphere=sphereTo;return result;}
        Capacity(count);
        bool relevant=false;
        float reach=1.6f+radius+Mathf.Max((playerTo-playerFrom).magnitude,(sphereTo-sphereFrom).magnitude);
        for(int i=0;i<count;i++){
            items[i]=state.Get(i);
            var b=items[i].Box;
            relevant|=!items[i].asleep || b.SqrDistance(playerFrom)<=reach*reach || b.SqrDistance(sphereFrom)<=reach*reach;
        }
        if(!relevant){result.player=playerTo;result.sphere=sphereTo;return result;}
        bool loaded=false,changed=false;
        for(int step=1;step<=steps;step++){
            Vector3 nextPlayer=result.player+(playerTo-playerFrom)/steps,nextSphere=result.sphere+(sphereTo-sphereFrom)/steps;
            bool moving=false;
            for(int i=0;i<count;i++){
                ref Item item=ref items[i];prior[i]=item.center;
                if(Push(ref item.velocity,item.Box,result.player+offset,nextPlayer+offset,halfPlayer,h)){result.playerPush=true;result.playerPushId=i;item.asleep=false;}
                if(Push(ref item.velocity,item.Box,result.sphere,nextSphere,halfSphere,h)){result.spherePush=true;result.spherePushId=i;item.asleep=false;}
                moving|=!item.asleep;
            }
            if(moving){
                for(int i=0;i<bodies.Count;i++){
                    var rb=bodies[i];
                    if(!loaded){rb.detectCollisions=i<count;if(i>=count)continue;rb.position=items[i].center;rb.rotation=Quaternion.identity;}
                    if(i>=count)continue;
                    if(!items[i].asleep){rb.linearVelocity=items[i].velocity;rb.WakeUp();}
                    else if(!loaded){rb.linearVelocity=Vector3.zero;rb.Sleep();}
                }
                physics.Simulate(h);loaded=changed=true;
                for(int i=0;i<count;i++){
                    var rb=bodies[i];ref Item item=ref items[i];item.center=rb.position;item.velocity=rb.linearVelocity;
                    item.asleep=rb.IsSleeping() || (item.velocity.sqrMagnitude<.00001f && (item.center-prior[i]).sqrMagnitude<.0000001f);
                }
            }
            for(int i=0;i<count;i++){
                var after=items[i].Box;
                float prevGap=prior[i].y-Half-(result.player.y+.29f),gap=after.min.y-(nextPlayer.y+.29f);
                if(prevGap>=-.025f && gap<=0 && after.center.y<prior[i].y-2*h){
                    float u=Mathf.Clamp01(prevGap/Mathf.Max(.00001f,prevGap-gap));
                    Vector3 separation=Vector3.Lerp(result.player,nextPlayer,u)-Vector3.Lerp(prior[i],after.center,u);
                    float dx=Mathf.Max(0,Mathf.Abs(separation.x)-Half),dz=Mathf.Max(0,Mathf.Abs(separation.z)-Half);
                    if(dx*dx+dz*dz<=.31f*.31f){result.crushed=true;result.crushId=i;}
                }
                nextPlayer=Clip(after,result.player+offset,nextPlayer+offset,halfPlayer)-offset;
                nextSphere=Clip(after,result.sphere,nextSphere,halfSphere);
            }
            result.player=nextPlayer;result.sphere=nextSphere;
        }
        if(changed){
            state.center=items[0].center;state.velocity=items[0].velocity;state.asleep=items[0].asleep;
            if(count>1){state.extra=new Item[count-1];Array.Copy(items,1,state.extra,0,count-1);}
        }
        return result;
    }
    public void Dispose()
    {
        if(scene.IsValid() && scene.isLoaded)SceneManager.UnloadSceneAsync(scene);
        UnityEngine.Object.Destroy(material);
    }
}