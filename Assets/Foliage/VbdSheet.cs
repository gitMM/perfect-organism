using System;
using System.Collections.Generic;
using UnityEngine;

namespace Foliage
{
    public sealed class VbdSheet : IDisposable
    {
        public const int Side=11, MaxSweeps=24;
        public int Count => rest.Length;
        public readonly LeafSurface leaf;
        public readonly RodPlant plant;
        public readonly PlantContact contact;
        public readonly PlantSelfContact selfContact;
        readonly int snapshot;
        readonly ComputeBuffer contactBuffer;
        readonly ComputeBuffer motionBuffer;
        readonly Vector2[] motionStats;
        public float minMotionBound,peakRejectedMotion;
        readonly int detect;
        public float rodStiffness=1;
        public int grabRod=-1;
        public float veinRatio=6;
        public float gravityScale=1;
        public const float Spacing=.02f, Density=.35f;
        public struct Sample
        {
            public Vector3 position,velocity,force;
            public Vector2 energy;
            public Vector3 grabForce;
        }
        struct Element { public int a,b,c,d; public float rest,angle,vein,materialScale; public int extra,kind; public Vector4 reference; }
        public readonly Vector3[] rest, positions;
        public readonly Sample[] samples;
        public readonly int[] colors, triangles;
        public readonly List<Vector2Int> edges=new();
        public readonly float[] residual=new float[MaxSweeps];
        public readonly int colorCount;
        public int iterations=12, grabFace=-1;
        public float stretch=12, bend=.00006f, dampingTime=.012f;
        public float grabK=3, grabD=.012f, grabLimit=.09f;
        public Vector3 weights,target,appliedGrab;
        public Vector2 elasticEnergy;
        public int badVertex=-1;
        public int Steps { get; private set; }
        readonly ComputeShader gpu;
        readonly List<ComputeBuffer> buffers=new();
        readonly int predict,relax,measure,finish;
        readonly ComputeBuffer x,velocity,sampleBuffer,residualBuffer;
        readonly float[] residualData;
        readonly int[] ids=new int[4];

        public VbdSheet(LeafSurface leaf=null,RodPlant plant=null)
        {
            this.leaf=leaf;
            this.plant=plant;
            if(plant!=null) contact=new PlantContact(plant.geometry);
            bool shell=leaf!=null || plant!=null;
            bool[] rib=leaf?.midrib??plant?.midrib;
            float Thickness(int node) => leaf!=null?leaf.thicknessRatio[node]:plant!=null?plant.geometry.thicknessRatio[node]:1f;
            rest=leaf?.rest??plant?.rest??new Vector3[Side*Side];
            positions=new Vector3[Count]; samples=new Sample[Count];
            motionStats=new Vector2[Count];
            colors=new int[Count]; residualData=new float[Count*MaxSweeps];
            if(shell)
            {
                stretch=90; bend=.12f; dampingTime=.025f;
                grabK=8; grabLimit=.6f; iterations=18;
            }
            if(!SystemInfo.supportsComputeShaders) throw new NotSupportedException("VBD requires compute shader support.");
            gpu=UnityEngine.Object.Instantiate(Resources.Load<ComputeShader>("SheetVbd"));
            predict=gpu.FindKernel("Predict"); relax=gpu.FindKernel("Relax");
            detect=gpu.FindKernel("DetectContacts");
            snapshot=gpu.FindKernel("Snapshot");
            measure=gpu.FindKernel("Measure"); finish=gpu.FindKernel("Finish");
            var elements=new List<Element>();
            var faces=new List<int>();
            var firstOpposite=new Dictionary<Vector2Int,int>();
            var mass=new float[Count];
            var incident=new List<int>[Count];
            for(int v=0;v<Count;v++)
            {
                if(!shell) rest[v]=new Vector3((v%Side-5)*Spacing,(5-v/Side)*Spacing,0);
                incident[v]=new List<int>();
            }
            void Add(int a,int b,int c,int d,float length)
            {
                int id=elements.Count;
                float angle=0,vein=0;
                if(c>=0 && shell)
                {
                    var e=(rest[d]-rest[c]).normalized;
                    var n=Vector3.Cross(rest[c]-rest[a],rest[d]-rest[a]).normalized;
                    var m=Vector3.Cross(rest[d]-rest[b],rest[c]-rest[b]).normalized;
                    angle=Mathf.Atan2(Vector3.Dot(Vector3.Cross(n,m),e),Vector3.Dot(n,m));
                    // Bending along the rib crosses hinges spanning the rib at each row.
                    if(rib[c]!=rib[d] && (rib[a] || rib[b])) vein=1;
                }
                else if(shell && rib[a] && rib[b]) vein=1;
                // Membrane rigidity scales with thickness; flexural rigidity scales with its cube.
                float scale=c<0?(Thickness(a)+Thickness(b))*.5f:Mathf.Pow((Thickness(c)+Thickness(d))*.5f,3);
                elements.Add(new Element{a=a,b=b,c=c,d=d,extra=-1,rest=length,angle=angle,vein=vein,materialScale=scale});
                incident[a].Add(id); incident[b].Add(id);
                if(c>=0) {incident[c].Add(id); incident[d].Add(id);}
            }
            void Edge(int a,int b,int opposite)
            {
                var key=new Vector2Int(Math.Min(a,b),Math.Max(a,b));
                float length=Vector3.Distance(rest[a],rest[b]);
                if(firstOpposite.TryGetValue(key,out int other)) Add(other,opposite,a,b,length);
                else { firstOpposite.Add(key,opposite); edges.Add(key); Add(a,b,-1,-1,length); }
            }
            void Face(int a,int b,int c)
            {
                faces.Add(a); faces.Add(b); faces.Add(c);
                float m=!shell?Density*Spacing*Spacing/6:
                    .075f*Vector3.Cross(rest[b]-rest[a],rest[c]-rest[a]).magnitude*(Thickness(a)+Thickness(b)+Thickness(c))/18;
                mass[a]+=m; mass[b]+=m; mass[c]+=m;
                Edge(a,b,c); Edge(b,c,a); Edge(c,a,b);
            }
            if(plant!=null)
            {
                foreach(var f in plant.geometry.faces) Face(f.a,f.b,f.c);
                AddRods(plant,elements,incident,mass);
                for(int i=0;i<contact.features.Length;i++)
                {
                    var f=contact.features[i]; int id=elements.Count;
                    elements.Add(new Element{a=f.a,b=f.b,c=f.c,d=-1,extra=-1,kind=4,rest=i});
                    incident[f.a].Add(id);incident[f.b].Add(id);
                    if(f.c>=0)incident[f.c].Add(id);
                }
            }
            else if(leaf!=null)
                for(int i=0;i<leaf.triangles.Length;i+=3) Face(leaf.triangles[i],leaf.triangles[i+1],leaf.triangles[i+2]);
            else for(int row=0;row<Side-1;row++)
                for(int col=0;col<Side-1;col++)
                {
                    int a=row*Side+col;
                    Face(a,a+1,a+Side+1); Face(a,a+Side+1,a+Side);
                }
            triangles=faces.ToArray();
            // Each spring and each four-vertex hinge is a clique, including grab triangle edges.
            Array.Fill(colors,-1);
            var neighbors=new HashSet<int>();
            for(int v=0;v<Count;v++)
            {
                if(plant!=null ? (v==plant.geometry.rods[0].a || v==plant.geometry.rods[0].b || v==plant.Ghost(0)) :
                    leaf==null?v<Side:leaf.clamped[v]) continue;
                neighbors.Clear();
                foreach(int i in incident[v])
                {
                    var e=elements[i];
                    neighbors.Add(colors[e.a]); neighbors.Add(colors[e.b]);
                    if(e.c>=0) neighbors.Add(colors[e.c]);
                    if(e.d>=0) neighbors.Add(colors[e.d]);
                    if(e.extra>=0) neighbors.Add(colors[e.extra]);
                }
                int color=0; while(neighbors.Contains(color)) color++;
                colors[v]=color; colorCount=Math.Max(colorCount,color+1);
            }
            var offsets=new int[Count+1];
            var flat=new List<int>();
            for(int v=0;v<Count;v++){ offsets[v]=flat.Count; flat.AddRange(incident[v]); }
            offsets[Count]=flat.Count;
            ComputeBuffer Buffer(string name,int count,int stride,Array data,params int[] kernels)
            {
                var buffer=new ComputeBuffer(count,stride); buffers.Add(buffer);
                if(data!=null) buffer.SetData(data);
                foreach(int kernel in kernels) gpu.SetBuffer(kernel,name,buffer);
                return buffer;
            }
            try
            {
                x=Buffer("X",Count,12,null,predict,relax,measure,finish,detect,snapshot);
                Buffer("SelfPositions",Count,12,null,snapshot,relax,measure);
                selfContact=new PlantSelfContact(contact,Count,gpu,predict,relax,measure);
                int featureCount=contact?.features.Length??0;
                Buffer("Features",Math.Max(1,featureCount),16,contact?.features,detect);
                contactBuffer=Buffer("Contacts",Math.Max(1,featureCount*2),56,null,detect,predict,relax,measure);
                var topology=contact?.topology;
                Buffer("Regions",Math.Max(1,featureCount),36,topology?.regions,detect);
                Buffer("RingOffsets",topology?.offsets.Length??1,4,topology?.offsets,detect);
                Buffer("RingNodes",Math.Max(1,topology?.neighbors.Length??0),4,topology?.neighbors,detect);
                gpu.SetFloat("ContactOffset",PlantContact.Offset);
                gpu.SetFloat("QueryRadius",PlantContact.QueryRadius);
                motionBuffer=Buffer("MotionStats",Count,8,null,predict,relax);
                gpu.SetInt("FeatureCount",featureCount);
                gpu.SetFloat("ContactK",3200);
                velocity=Buffer("Velocity",Count,12,null,predict,finish);
                Buffer("Old",Count,12,null,predict,relax,measure,finish);
                Buffer("Inertia",Count,12,null,predict,relax,measure);
                Buffer("Mass",Count,4,mass,relax,measure);
                Buffer("Elements",elements.Count,56,elements.ToArray(),predict,relax,measure);
                Buffer("Offsets",offsets.Length,4,offsets,predict,relax,measure);
                Buffer("Incident",flat.Count,4,flat.ToArray(),predict,relax,measure);
                Buffer("Groups",Count,4,colors,predict,relax,measure);
                sampleBuffer=Buffer("Samples",Count,56,null,measure);
                residualBuffer=Buffer("Residual",Count*MaxSweeps,4,null,measure);
                gpu.SetInt("Count",Count);
                gpu.SetInt("WarmStartVelocity",shell?1:0);
                gpu.SetFloat("DragRate",plant!=null?6:shell?3:0);
                gpu.SetInt("PhysicalNodes",plant?.geometry.nodes.Count??Count);
                Reset();
            }
            catch { Dispose(); throw; }
        }
        static void AddRods(RodPlant plant,List<Element> elements,List<int>[] incident,float[] mass)
        {
            var rods=plant.geometry.rods; var p=plant.rest;
            void Register(Element e)
            {
                int id=elements.Count;
                elements.Add(e);
                foreach(int v in new[]{e.a,e.b,e.c,e.d,e.extra}) if(v>=0) incident[v].Add(id);
            }
            void Spring(int a,int b,float stiffness)
            {
                Register(new Element{kind=1,a=a,b=b,c=-1,d=-1,extra=-1,rest=Vector3.Distance(p[a],p[b]),angle=stiffness});
            }
            for(int i=0;i<rods.Count;i++)
            {
                var r=rods[i]; int g=plant.Ghost(i);
                float len=Vector3.Distance(p[r.a],p[r.b]);
                float area=Mathf.PI*r.radius*r.radius;
                float segmentMass=130*area*len;
                mass[r.a]+=.5f*segmentMass; mass[r.b]+=.5f*segmentMass;
                // Match polar inertia m*r²/2 at the director's numerical lever arm.
                mass[g]=2*segmentMass*r.radius*r.radius/(len*len);
                float axial=320000*area/len;
                Spring(r.a,r.b,axial);
                Spring(r.a,g,axial*.45f); Spring(r.b,g,axial*.45f);
                if(plant.parent[i]<0) continue;
                int parent=plant.parent[i];
                var previous=rods[parent];
                Quaternion q=Quaternion.Inverse(plant.restFrames[parent])*plant.restFrames[i];
                float dual=(Vector3.Distance(p[previous.a],p[previous.b])+len)*.5f;
                float flexuralRigidity=(r.part==0?5.8f:.24f)*Mathf.Pow(r.radius/(r.part==0?.028f:.009f),4);
                Register(new Element{kind=2,a=previous.a,b=r.a,c=plant.Ghost(parent),d=r.b,extra=g,
                    rest=flexuralRigidity/dual,reference=new Vector4(q.x,q.y,q.z,q.w)});
            }
            for(int i=0;i<plant.leafRod.Length;i++)
            {
                int rod=plant.leafRod[i]; if(rod<0)continue;
                var r=rods[rod];
                var row=plant.geometry.bladeRows[i][1];
                for(int j=row.Length/2-1;j<=row.Length/2+1;j++)
                {
                    int v=row[j];
                    if(plant.geometry.severedCollars.Contains(v))continue;
                    Vector3 offset=Quaternion.Inverse(plant.restFrames[rod])*(p[v]-p[r.b]);
                    Register(new Element{kind=3,a=r.a,b=r.b,c=plant.Ghost(rod),d=v,extra=-1,
                        rest=220,reference=new Vector4(offset.x,offset.y,offset.z,0)});
                }
            }
        }
        public void Reset()
        {
            contact?.Reset();
            selfContact.Clear();
            x.SetData(rest); velocity.SetData(new Vector3[Count]);
            Array.Copy(rest,positions,Count);
            Array.Clear(samples,0,Count); Array.Clear(residual,0,residual.Length);
            for(int v=0;v<Count;v++) samples[v].position=rest[v];
            badVertex=-1; Steps=0; grabFace=grabRod=-1; appliedGrab=default; elasticEnergy=default;
        }
        public Vector3 Point(int face,Vector3 w)
        {
            int i=3*face;
            return positions[triangles[i]]*w.x+positions[triangles[i+1]]*w.y+positions[triangles[i+2]]*w.z;
        }
        internal void ContinueCut(VbdSheet old,PlantCut cut)
        {
            iterations=old.iterations;stretch=old.stretch;bend=old.bend;dampingTime=old.dampingTime;
            rodStiffness=old.rodStiffness;veinRatio=old.veinRatio;gravityScale=old.gravityScale;
            grabK=old.grabK;grabD=old.grabD;grabLimit=old.grabLimit;Steps=old.Steps;
            selfContact.enabled=old.selfContact.enabled;selfContact.dat=old.selfContact.dat;
            contact.CopyPose(old.contact);
            var speeds=new Vector3[Count];
            for(int i=0;i<cut.positions.Count;i++){positions[i]=cut.positions[i];speeds[i]=cut.velocities[i];}
            for(int i=0;i<plant.parent.Length;i++)
            {
                int src=cut.rodSource[i],dst=plant.Ghost(i);var r=plant.geometry.rods[i];var prev=old.plant.geometry.rods[src];
                float ratio=Vector3.Distance(rest[r.a],rest[r.b])/Vector3.Distance(old.rest[prev.a],old.rest[prev.b]);
                Vector3 Director(Vector3 ghost,Vector3 a,Vector3 b,Vector3 na,Vector3 nb)=>
                    .5f*(na+nb)+ratio*(ghost-.5f*(a+b));
                int gh=old.plant.Ghost(src);
                positions[dst]=Director(old.positions[gh],old.positions[prev.a],old.positions[prev.b],positions[r.a],positions[r.b]);
                speeds[dst]=Director(old.samples[gh].velocity,old.samples[prev.a].velocity,old.samples[prev.b].velocity,speeds[r.a],speeds[r.b]);
            }
            for(int i=0;i<Count;i++){samples[i].position=positions[i];samples[i].velocity=speeds[i];}
            x.SetData(positions);velocity.SetData(speeds);
        }
        void SetContactPose()
        {
            gpu.SetInt("Shape",contact.shape==ObstacleShape.Gate?(int)ObstacleShape.Box:(int)contact.shape);
            gpu.SetInt("WallCount",contact.wallCount);
            gpu.SetFloat("WallOffset",contact.WallOffset(0).x);
            gpu.SetInt("UseOgc",contact.model==ContactModel.OGC?1:0);
            gpu.SetVector("ObstacleCenter",contact.center);
            gpu.SetVector("Extents",contact.extents);
            Quaternion q=contact.rotation;
            gpu.SetVector("ObstacleRotation",new Vector4(q.x,q.y,q.z,q.w));
        }
        public void QueryContacts()
        {
            if(contact==null || contact.shape==ObstacleShape.Off)return;
            SetContactPose();
            gpu.Dispatch(detect,(contact.hits.Length+63)/64,1,1);
            contactBuffer.GetData(contact.hits);
            contact.Summarize();
        }
        public void Step(float dt)
        {
            bool collide=contact!=null && contact.shape!=ObstacleShape.Off;
            gpu.SetInt("Shape",collide?(int)contact.shape:0);
            if(collide)
            {
                contact.Advance(dt);SetContactPose();
            }
            gpu.SetFloat("Dt",dt); gpu.SetFloat("Stretch",stretch); gpu.SetFloat("Bend",bend);
            gpu.SetFloat("DampingTime",dampingTime);
            gpu.SetFloat("VeinRatio",veinRatio);
            gpu.SetFloat("RodStiffness",rodStiffness);
            gpu.SetFloat("GravityScale",gravityScale);
            gpu.SetInt("GrabEnabled",grabFace>=0 || grabRod>=0?1:0);
            if(grabFace>=0 || grabRod>=0)
            {
                if(grabRod>=0)
                {
                    var rod=plant.geometry.rods[grabRod];
                    ids[0]=rod.a; ids[1]=rod.b; ids[2]=plant.Ghost(grabRod);
                }
                else Array.Copy(triangles,3*grabFace,ids,0,3);
                gpu.SetInts("GrabIds",ids); gpu.SetVector("Weights",weights); gpu.SetVector("Target",target);
                gpu.SetFloat("GrabK",grabK); gpu.SetFloat("GrabD",grabD); gpu.SetFloat("GrabLimit",grabLimit);
            }
            int blocks=(Count+63)/64;
            bool self=selfContact.Prepare(positions,samples,dt);
            int contactBlocks=collide?(contact.hits.Length+63)/64:0;
            bool ogc=collide && contact.model==ContactModel.OGC;
            gpu.SetInt("UseOgc",ogc?1:0);
            if(ogc)gpu.Dispatch(detect,contactBlocks,1,1);
            gpu.Dispatch(predict,blocks,1,1);
            if(self)gpu.Dispatch(snapshot,blocks,1,1);
            if(collide)gpu.Dispatch(detect,contactBlocks,1,1);
            for(int i=0;i<iterations;i++)
            {
                for(int group=0;group<colorCount;group++)
                {
                    gpu.SetInt("Group",group);
                    gpu.Dispatch(relax,blocks,1,1);
                }
                if(collide)gpu.Dispatch(detect,contactBlocks,1,1);
                if(self)gpu.Dispatch(snapshot,blocks,1,1);
                gpu.SetInt("Sweep",i); gpu.Dispatch(measure,blocks,1,1);
            }
            gpu.Dispatch(finish,blocks,1,1);
            sampleBuffer.GetData(samples);
            if(collide){contactBuffer.GetData(contact.hits);contact.Summarize();}
            if(ogc || selfContact.safeguarding)
            {
                motionBuffer.GetData(motionStats);
                minMotionBound=float.PositiveInfinity;peakRejectedMotion=0;
                foreach(var m in motionStats)
                {
                    minMotionBound=Mathf.Min(minMotionBound,m.x);
                    peakRejectedMotion=Mathf.Max(peakRejectedMotion,m.y);
                }
            }
            residualBuffer.GetData(residualData,0,0,Count*iterations);
            elasticEnergy=default; appliedGrab=default; badVertex=-1;
            for(int v=0;v<Count;v++)
            {
                Sample s=samples[v];
                positions[v]=s.position; elasticEnergy+=s.energy; appliedGrab+=s.grabForce;
                if(!float.IsFinite(s.position.sqrMagnitude+s.velocity.sqrMagnitude+s.force.sqrMagnitude+
                    s.energy.sqrMagnitude+s.grabForce.sqrMagnitude)) badVertex=v;
            }
            for(int i=0;i<iterations;i++)
            {
                float max=0;
                for(int v=0;v<Count;v++) {
                    float r=residualData[i*Count+v];
                    if(!float.IsFinite(r)) badVertex=v;
                    max=Math.Max(max,r);
                }
                residual[i]=max;
            }
            if(self)selfContact.Measure(positions);
            Steps++;
        }
        public void Dispose()
        {
            selfContact?.Dispose();
            foreach(var buffer in buffers) buffer.Release();
            buffers.Clear();
            UnityEngine.Object.Destroy(gpu);
        }
    }
}
