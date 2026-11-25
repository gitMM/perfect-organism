using UnityEngine;

namespace Foliage
{
    public enum ObstacleShape { Off,Sphere,Capsule,Box,Gate }
    public enum ContactModel { Penalty,OGC }
    public sealed class PlantContact
    {
        public struct Feature { public int a,b,c; public float skin; }
        public struct Hit
        {
            public Vector3 witness; public float depth;
            public Vector3 normal; public float candidate;
            public Vector3 weights; public float clearance;
            public int layers,region;
        }
        public readonly Feature[] features;
        public Hit[] hits {get;private set;}
        public float gapWidth {get;private set;}
        public int wallCount=>shape==ObstacleShape.Gate?2:1;
        public Vector3 WallOffset(int wall)=>wallCount==1?Vector3.zero:
            Vector3.right*((wall==0?-1:1)*(extents.x+gapWidth*.5f));
        public readonly int rodCount;
        internal readonly int[] featureParts,featurePlants;
        internal readonly System.Collections.Generic.List<Vector2Int> cutTwins;
        public ContactModel model=ContactModel.OGC;
        public const float Offset=.012f, QueryRadius=.05f;
        internal readonly OgcTopology topology;
        public float minClearance,physicalPenetration;
        public int layers;
        internal bool hasClearance;
        public ObstacleShape shape {get;private set;}
        public Vector3 center {get;private set;}
        public Vector3 target;
        public Quaternion rotation {get;private set;}
        public Vector3 extents {get;private set;}
        public int candidates,rods,faces,deepest=-1;
        internal readonly System.Collections.Generic.List<int> candidateSlots=new();
        public float peakDepth;
        Vector3 spawn;
        internal bool needsPlacement;
        internal void Place(Vector3 point)=>center=point;
        public PlantContact(PlantGeometry g)
        {
            rodCount=g.rods.Count;
            featureParts=new int[rodCount+g.faces.Count];
            featurePlants=new int[featureParts.Length];
            cutTwins=g.cutTwins;
            topology=new OgcTopology(g);
            features=new Feature[rodCount+g.faces.Count]; hits=new Hit[features.Length];
            for(int i=0;i<rodCount;i++)
            {
                var r=g.rods[i]; features[i]=new Feature{a=r.a,b=r.b,c=-1,skin=Mathf.Max(r.radius,r.collisionRadius)};
                featureParts[i]=r.part;featurePlants[i]=g.PlantOfPart(r.part);
            }
            for(int i=0;i<g.faces.Count;i++)
            {
                var f=g.faces[i]; featurePlants[rodCount+i]=g.PlantOfPart(f.part);featureParts[rodCount+i]=f.part; features[rodCount+i]=new Feature{a=f.a,b=f.b,c=f.c,skin=.002f};
            }
        }
        public void Configure(ObstacleShape next,Vector3 start,Quaternion orientation,float radius=.15f,float opening=.06f,float capsuleHalfHeight=0)
        {
            playerOrbit=false;
            shape=next; spawn=start; rotation=orientation.normalized;
            extents=next==ObstacleShape.Capsule?new Vector3(radius,capsuleHalfHeight>0?Mathf.Max(radius,capsuleHalfHeight):2.2f*radius,radius):
                next==ObstacleShape.Box || next==ObstacleShape.Gate?new Vector3(radius,1.25f*radius,.85f*radius):Vector3.one*radius;
            gapWidth=Mathf.Max(0,opening);
            if(hits.Length!=features.Length*wallCount)hits=new Hit[features.Length*wallCount];
            Reset();
        }
        public bool playerOrbit {get;private set;}
        internal static float OrbitRadius(int plants)=>plants==100?2.29f:1.5f;
        const float OrbitSpeed=2.4f;
        float orbitRadius;
        Vector3 orbitCenter;
        float orbitAngle;
        public void StartPlayerOrbit(Vector3 pivot,float radius)
        {
            orbitCenter=pivot;
            orbitRadius=radius;
            orbitAngle=0;
            playerOrbit=true;
            model=ContactModel.Penalty;
        }
        public void Reset()
        {
            orbitAngle=0;
            needsPlacement=shape!=ObstacleShape.Off;
            center=target=spawn; System.Array.Clear(hits,0,hits.Length); Summarize();
            hasClearance=false;
        }
        internal void CopyPose(PlantContact old)
        {
            Configure(old.shape,old.spawn,old.rotation,old.extents.x,old.gapWidth,old.extents.y);
            playerOrbit=old.playerOrbit;orbitCenter=old.orbitCenter;orbitRadius=old.orbitRadius;orbitAngle=old.orbitAngle;
            center=old.center;target=old.target;model=old.model;hasClearance=false;needsPlacement=old.needsPlacement;
        }
        public void Advance(float dt)
        {
            if(playerOrbit)
            {
                // Hold the waypoint if spawn recovery or a contact limit delays the capsule.
                var waypoint=orbitCenter+orbitRadius*new Vector3(Mathf.Cos(orbitAngle),0,Mathf.Sin(orbitAngle));
                if((center-waypoint).sqrMagnitude<1e-8f)
                    orbitAngle=Mathf.Repeat(orbitAngle+OrbitSpeed*dt/orbitRadius,2*Mathf.PI);
                target=orbitCenter+orbitRadius*new Vector3(Mathf.Cos(orbitAngle),0,Mathf.Sin(orbitAngle));
            }
            // Keep kinematic mouse motion continuous at the physics clock's rate.
            float travel=Mathf.Min((playerOrbit?OrbitSpeed:.9f)*dt,.18f*Mathf.Min(extents.x,extents.z));
            if(model==ContactModel.OGC)
            {
                float limit=hasClearance?.45f*Mathf.Max(0,minClearance):0;
                // A collider spawned inside a pinned feature can still be pulled out.
                if(hasClearance && minClearance<=0)
                {
                    var direction=(target-center).normalized;
                    limit=.001f;
                    foreach(var hit in hits)
                        if(hit.clearance<=0 && Vector3.Dot(direction,hit.normal)>0){limit=0;break;}
                }
                travel=Mathf.Min(travel,limit);
            }
            center=Vector3.MoveTowards(center,target,travel);
        }
        public void Summarize()
        {
            candidateSlots.Clear();
            peakDepth=0;deepest=-1;candidates=rods=faces=0;
            minClearance=QueryRadius;physicalPenetration=0;layers=0;
            for(int i=0;i<hits.Length;i++)
            {
                var hit=hits[i]; if(hit.candidate>0){candidates++;candidateSlots.Add(i);}
                if(model==ContactModel.OGC)minClearance=Mathf.Min(minClearance,hit.clearance);
                layers+=model==ContactModel.OGC?hit.layers:hit.depth>0?1:0;
                if(hit.depth<=0)continue;
                if(i%features.Length<rodCount)rods++;else faces++;
                if(hit.depth>peakDepth){deepest=i;peakDepth=hit.depth;}
            }
            physicalPenetration=model==ContactModel.OGC?Mathf.Max(0,-minClearance):peakDepth;
            hasClearance=model==ContactModel.OGC;
        }
    }
}
