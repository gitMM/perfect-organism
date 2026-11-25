using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Foliage
{
    public sealed partial class VbdSheet : IDisposable
    {
        public const int Side=11, MaxSweeps=24;
        public int Count => rest.Length;
        public readonly LeafSurface leaf;
        public readonly RodPlant plant;
        public readonly PlantContact contact;
        public PlantSelfContact selfContact {get;private set;}
        int localRelax;
        readonly int localPlants;
        int snapshot,cacheRestMotion;
        readonly int structuralCount;
        ComputeBuffer contactBuffer;
        ComputeBuffer motionBuffer;
        readonly Vector2[] motionStats;
        public float minMotionBound,peakRejectedMotion;
        int detect,prepareFall;
        bool hasFragments;
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
        readonly List<int> stiffFigJoints = new();
        struct FigAxisState { public Vector4 multiplierAndPenalty, initialError; }
        int figDualKernel;
        ComputeBuffer figDual;
        struct Element { public int a,b,c,d; public float rest,angle,vein,materialScale; public int extra,kind; public Vector4 reference; }
        public readonly Vector3[] rest, positions;
        public readonly Sample[] samples;
        public readonly int[] colors, triangles;
        internal readonly List<int> pins=new();
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
        ComputeShader gpu;
        Action uploadTopology;
        readonly List<ComputeBuffer> buffers=new();
        int predict,relax,measure,finish;
        ComputeBuffer x,oldPositions,velocity,sampleBuffer,residualBuffer;
        readonly float[] residualData;
        readonly int[] ids=new int[4];
        readonly int[] groupOffsets;

        public VbdSheet(LeafSurface leaf=null,RodPlant plant=null):this(leaf,plant,false){}
        internal VbdSheet(LeafSurface leaf,RodPlant plant,bool deferUpload)
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
                if(stiffFigJoints.Count>0)iterations=8;
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
            structuralCount=elements.Count-(contact?.features.Length??0);
            triangles=faces.ToArray();
            // Each spring and each four-vertex hinge is a clique, including grab triangle edges.
            Array.Fill(colors,-1);
            var clamped=new HashSet<int>();
            if(plant!=null)
            {
                clamped.UnionWith(plant.geometry.groundPins);
                var roots=plant.geometry.rootNodes;
                for(int i=0;i<plant.parent.Length;i++)
                {
                    var r=plant.geometry.rods[i];
                    if(roots.Count==0?i!=0:!roots.Contains(r.a) || plant.parent[i]>=0)continue;
                    clamped.Add(r.a);clamped.Add(r.b);clamped.Add(plant.Ghost(i));
                }
            }
            var usedColors=new int[Count+1];
            for(int v=0;v<Count;v++)
            {
                if(plant!=null ? clamped.Contains(v) :
                    leaf==null?v<Side:leaf.clamped[v]) continue;
                int mark=v+1;
                foreach(int i in incident[v])
                {
                    var e=elements[i];
                    usedColors[colors[e.a]+1]=mark;usedColors[colors[e.b]+1]=mark;
                    if(e.c>=0) usedColors[colors[e.c]+1]=mark;
                    if(e.d>=0) usedColors[colors[e.d]+1]=mark;
                    if(e.extra>=0) usedColors[colors[e.extra]+1]=mark;
                }
                int color=0; while(usedColors[color+1]==mark) color++;
                colors[v]=color; colorCount=Math.Max(colorCount,color+1);
            }
            for(int i=0;i<Count;i++)if(colors[i]<0)pins.Add(i);
            // Pack each color into whole thread groups, in topology order for coherent reads.
            groupOffsets=new int[colorCount+1];
            var groupNodes=new List<int>();
            // Adjacent lanes solve similar stencils; no dependencies exist within a color.
            int[] order=null;
            if(plant?.geometry.twigClusters.Count>0){
                order=new int[Count];
                var keys=new long[Count];
                for(int v=0;v<Count;v++){
                    int frames=0,shellTerms=0;
                    foreach(int id in incident[v]){
                        var kind=elements[id].kind;
                        if(kind==2 || kind==3)frames++;
                        if(kind==0)shellTerms++;
                    }
                    order[v]=v;
                    keys[v]=((long)frames<<48)+((long)shellTerms<<32)+v;
                }
                Array.Sort(keys,order);
            }
            for(int group=0;group<colorCount;group++)
            {
                groupOffsets[group]=groupNodes.Count;
                for(int i=0;i<Count;i++){
                    int v=order==null?i:order[i];
                    if(colors[v]==group)groupNodes.Add(v);
                }
                while(groupNodes.Count%64!=0)groupNodes.Add(-1);
            }
            groupOffsets[colorCount]=groupNodes.Count;
            var offsets=new int[Count+1];
            var flat=new List<int>();
            var rodCounts=new int[Count];
            bool Centerline(int id)=>elements[id].kind==1 && elements[id].materialScale>0;
            for(int v=0;v<Count;v++)
            {
                offsets[v]=flat.Count;
                foreach(int id in incident[v])if(Centerline(id))flat.Add(id);
                rodCounts[v]=flat.Count-offsets[v];
                foreach(int id in incident[v])if(!Centerline(id))flat.Add(id);
            }
            offsets[Count]=flat.Count;

            var localRanges=new List<PlantRange>();
            var localOrder=new List<int>();
            var colorOffsets=new List<int>();
            if(plant!=null && plant.geometry.rootNodes.Count>=10 && plant.geometry.cutTwins.Count==0)
            {
                var geom=plant.geometry;
                int rod=0;
                for(int i=0;i<geom.rootNodes.Count;i++)
                {
                    int root=geom.rootNodes[i],end=i+1<geom.rootNodes.Count?geom.rootNodes[i+1]:geom.nodes.Count;
                    int first=rod;
                    while(rod<geom.rods.Count && geom.rods[rod].a<end)rod++;
                    int count=end-root+rod-first;
                    if(count>1536 || count<=0){localRanges.Clear();break;}
                    localRanges.Add(new PlantRange{firstNode=root,nodes=end-root,firstGhost=plant.Ghost(first),ghosts=rod-first});
                    for(int color=0;color<colorCount;color++)
                    {
                        colorOffsets.Add(localOrder.Count);
                        for(int n=0;n<count;n++)
                        {
                            int id=n<end-root?root+n:plant.Ghost(first+n-(end-root));
                            if(colors[id]==color)localOrder.Add(id);
                        }
                    }
                    colorOffsets.Add(localOrder.Count);
                }
            }
            int[] packedNodes=null,localIndices=null;
            if(plant!=null && plant.geometry.rootNodes.Count>=10 && plant.geometry.cutTwins.Count>0)
            {
                var geom=plant.geometry;
                var owners=new int[Count];
                for(int i=0;i<geom.rods.Count;i++)
                {
                    var r=geom.rods[i];
                    owners[r.a]=owners[r.b]=owners[plant.Ghost(i)]=geom.PlantOfPart(r.part);
                }
                foreach(var f in geom.faces)owners[f.a]=owners[f.b]=owners[f.c]=geom.PlantOfPart(f.part);
                var groups=new List<int>[geom.rootNodes.Count];
                for(int i=0;i<groups.Length;i++)groups[i]=new List<int>();
                for(int i=0;i<Count;i++)groups[owners[i]].Add(i);
                localIndices=new int[Count];packedNodes=new int[Count];
                int start=0;
                foreach(var group in groups)
                {
                    if(group.Count>1536){localRanges.Clear();break;}
                    localRanges.Add(new PlantRange{firstNode=start,nodes=group.Count});
                    for(int i=0;i<group.Count;i++){localIndices[group[i]]=i;packedNodes[start+i]=group[i];}
                    for(int color=0;color<colorCount;color++)
                    {
                        colorOffsets.Add(localOrder.Count);
                        foreach(int id in group)if(colors[id]==color)localOrder.Add(id);
                    }
                    colorOffsets.Add(localOrder.Count);start+=group.Count;
                }
            }
            localPlants=localRanges.Count;
            ComputeBuffer Buffer(string name,int count,int stride,Array data,params int[] kernels)
            {
                var buffer=new ComputeBuffer(count,stride); buffers.Add(buffer);
                if(data!=null) buffer.SetData(data);
                foreach(int kernel in kernels)
                {
                    gpu.SetBuffer(kernel,name,buffer);
                    if(kernel==relax && figClusterRelax>=0 && name!="Mass")gpu.SetBuffer(figClusterRelax,name,buffer);
                    if(kernel==relax && localPlants>0 && name!="GroupNodes")gpu.SetBuffer(localRelax,name,buffer);
                }
                return buffer;
            }
            uploadTopology=()=>
            {
                if(!SystemInfo.supportsComputeShaders) throw new NotSupportedException("VBD requires compute shader support.");
                gpu=UnityEngine.Object.Instantiate(Resources.Load<ComputeShader>("SheetVbd"));
                predict=gpu.FindKernel("Predict"); relax=gpu.FindKernel("Relax");
                if(plant?.geometry.twigClusters.Count>0)figClusterRelax=gpu.FindKernel("FigClusterRelax");
                detect=gpu.FindKernel("DetectContacts");
                cacheRestMotion=gpu.FindKernel("CacheRestMotion");
                snapshot=gpu.FindKernel("Snapshot");
                measure=gpu.FindKernel("Measure"); finish=gpu.FindKernel("Finish");
                localRelax=gpu.FindKernel(packedNodes==null?"RelaxPlant":"RelaxCutPlant");
                try
                {
                    if(localPlants>0)
                    {
                        if(packedNodes!=null)
                        {
                            Buffer("PackedNodes",Count,4,packedNodes,localRelax);
                            Buffer("LocalIndices",Count,4,localIndices,localRelax);
                        }
                        Buffer("PlantRanges",localPlants,16,localRanges.ToArray(),localRelax);
                        Buffer("PlantOrder",localOrder.Count,4,localOrder.ToArray(),localRelax);
                        Buffer("PlantColors",colorOffsets.Count,4,colorOffsets.ToArray(),localRelax);
                        gpu.SetInt("ColorCount",colorCount);
                    }
                    prepareFall=gpu.FindKernel("PrepareFall");
                    hasFragments=plant!=null && plant.geometry.cutTwins.Count>0;
                    var fall=new Vector4[hasFragments?Count:1];
                    if(hasFragments)
                    {
                        var geom=plant.geometry;
                        var rooted=new HashSet<int>();
                        foreach(int root in geom.rootNodes)rooted.Add(geom.ComponentOf(root));
                        for(int v=0;v<geom.nodes.Count;v++)
                            if(!rooted.Contains(geom.ComponentOf(v)))fall[v].w=1;
                        for(int i=0;i<geom.rods.Count;i++)fall[plant.Ghost(i)].w=fall[geom.rods[i].a].w;
                    }
                    gpu.SetInt("HasFragments",hasFragments?1:0);
                    Buffer("FallMotion",fall.Length,16,fall,prepareFall,predict);
                    x=Buffer("X",Count,12,null,predict,relax,measure,finish,detect,snapshot,prepareFall);
                    Buffer("SelfPositions",Count,12,null,snapshot,relax,measure);
                    selfContact=new PlantSelfContact(contact,Count,gpu,predict,relax,measure,localPlants>0?localRelax:figClusterRelax,plant!=null && (plant.geometry.rootNodes.Count>=10 || plant.geometry.faces.Count>1000));
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
                    velocity=Buffer("Velocity",Count,12,null,predict,relax,finish,prepareFall);
                    oldPositions=Buffer("Old",Count,12,null,predict,relax,measure,finish,cacheRestMotion);
                    var inertia=Buffer("Inertia",Count,12,null,predict,relax,measure);
                    var baseMass=Buffer("Mass",Count,4,mass,relax,measure,prepareFall);
                    if(figClusterRelax>=0)BuildFigClusters(mass,baseMass);
                    var elementBuffer=Buffer("Elements",elements.Count,56,elements.ToArray(),predict,relax,measure,cacheRestMotion,prepareFall);
                    int jointCount=stiffFigJoints.Count;
                    figDual=Buffer("FigDual",Math.Max(1,3*jointCount),32,null,relax,measure);
                    if(jointCount>0){
                        if(deferUpload)figDual.SetData(new FigAxisState[3*jointCount]);
                        figDualKernel=gpu.FindKernel("UpdateFigDual");
                        gpu.SetInt("FigJointCount",jointCount);
                        Buffer("FigJointIds",jointCount,4,stiffFigJoints.ToArray(),figDualKernel);
                        gpu.SetBuffer(figDualKernel,"FigDualOut",figDual);
                        gpu.SetBuffer(figDualKernel,"Elements",elementBuffer);
                        gpu.SetBuffer(figDualKernel,"X",x);
                    }
                    var oldMeasure=Buffer("OldMeasure",Math.Max(1,structuralCount),4,null,cacheRestMotion,relax,measure);
                    if(figClusterRelax>=0){
                        gpu.SetBuffer(figTrialEnergy,"Inertia",inertia);
                        gpu.SetBuffer(figTrialEnergy,"Elements",elementBuffer);
                        gpu.SetBuffer(figTrialEnergy,"FigDual",figDual);
                        gpu.SetBuffer(figTrialEnergy,"OldMeasure",oldMeasure);
                    }
                    gpu.SetInt("StructuralCount",structuralCount);
                    Buffer("RodCounts",Count,4,rodCounts,predict,relax);
                    Buffer("Offsets",offsets.Length,4,offsets,predict,relax,measure,prepareFall);
                    Buffer("Incident",flat.Count,4,flat.ToArray(),predict,relax,measure,prepareFall);
                    var orderedNodes=Buffer("GroupNodes",Math.Max(1,groupNodes.Count),4,groupNodes.Count>0?groupNodes.ToArray():null,relax);
                    if(figClusterRelax>=0)BuildFigTiles(orderedNodes);
                    Buffer("Groups",Count,4,colors,predict,relax,measure);
                    sampleBuffer=Buffer("Samples",Count,56,null,measure);
                    residualBuffer=Buffer("Residual",Count*MaxSweeps,4,null,measure);
                    gpu.SetInt("Count",Count);
                    // A partial velocity guess limits overshoot of the eight-sweep fig solve.
                    // Inertia still uses the entire velocity from the preceding tick.
                    gpu.SetFloat("VelocityGuess",figClusterRelax>=0?.7f:1f);
                    gpu.SetInt("WarmStartVelocity",shell?1:0);
                    gpu.SetFloat("DragRate",plant!=null?6:shell?3:0);
                    gpu.SetInt("PhysicalNodes",plant?.geometry.nodes.Count??Count);
                    if(!deferUpload)Reset();
                }
                catch { Dispose(); throw; }
            };
            if(!deferUpload)UploadTopology();
        }
        internal void UploadTopology()
        {
            var upload=uploadTopology;uploadTopology=null;
            upload();
        }
        void AddRods(RodPlant plant,List<Element> elements,List<int>[] incident,float[] mass)
        {
            var rods=plant.geometry.rods; var p=plant.rest;
            void Register(Element e)
            {
                int id=elements.Count;
                elements.Add(e);
                foreach(int v in new[]{e.a,e.b,e.c,e.d,e.extra}) if(v>=0) incident[v].Add(id);
            }
            void Spring(int a,int b,float stiffness,bool centerline=false)
            {
                Register(new Element{kind=1,a=a,b=b,c=-1,d=-1,extra=-1,rest=Vector3.Distance(p[a],p[b]),angle=stiffness,materialScale=centerline?1:0});
            }
            var rootedTrunk=new Dictionary<int,int>();
            for(int i=0;i<rods.Count;i++)
            {
                var r=rods[i]; int g=plant.Ghost(i);
                float len=Vector3.Distance(p[r.a],p[r.b]);
                float area=Mathf.PI*r.radius*r.radius;
                float segmentMass=130*area*len;
                mass[r.a]+=.5f*segmentMass; mass[r.b]+=.5f*segmentMass;
                // Match polar inertia m*r²/2 at the director's numerical lever arm.
                mass[g]=2*segmentMass*r.radius*r.radius/(len*len);
                float woodRigidity=1+r.woodRigidityGain;
                float axial=320000*woodRigidity*area/len;
                Spring(r.a,r.b,axial,true);
                Spring(r.a,g,axial*.45f); Spring(r.b,g,axial*.45f);
                // Distributed elastic support in the rooted stem's material frame.
                // Weight by segment length so coarse and detailed trunks have comparable support.
                if(r.part<=0 && plant.geometry.distributedStemSupport)
                {
                    if(plant.parent[i]<0 && plant.geometry.rootNodes.Contains(r.a))
                        rootedTrunk[r.b]=i;
                    else if(rootedTrunk.TryGetValue(r.a,out int baseRod))
                    {
                        rootedTrunk[r.b]=baseRod;
                        var root=rods[baseRod];
                        Vector3 offset=Quaternion.Inverse(plant.restFrames[baseRod])*(p[r.b]-p[root.b]);
                        Register(new Element{kind=3,a=root.a,b=root.b,c=plant.Ghost(baseRod),d=r.b,extra=-1,
                            rest=350f*len,reference=new Vector4(offset.x,offset.y,offset.z,0)});
                    }
                }
                if(plant.parent[i]<0) continue;
                int parent=plant.parent[i];
                var previous=rods[parent];
                Quaternion q=Quaternion.Inverse(plant.restFrames[parent])*plant.restFrames[i];
                float dual=(Vector3.Distance(p[previous.a],p[previous.b])+len)*.5f;
                float flexuralRigidity=(r.part<=0?5.8f:.24f)*Mathf.Pow(r.radius/(r.part<=0?.028f:.009f),4);
                // Kind 2 uses materialScale as a one-based AVBD joint slot (zero for elastic rods).
                int jointSlot=0;
                if(r.woodRigidityGain>=1f){
                    stiffFigJoints.Add(elements.Count);
                    jointSlot=stiffFigJoints.Count;
                }
                Register(new Element{kind=2,a=previous.a,b=r.a,c=plant.Ghost(parent),d=r.b,extra=g,
                    materialScale=jointSlot,rest=woodRigidity*flexuralRigidity/dual,reference=new Vector4(q.x,q.y,q.z,q.w)});
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
            CompletePendingStep();
            contact?.Reset();
            selfContact.Clear();
            ResetFigClusters();
            if(stiffFigJoints.Count>0)figDual.SetData(new FigAxisState[stiffFigJoints.Count*3]);
            x.SetData(rest); velocity.SetData(new Vector3[Count]);
            Array.Copy(rest,positions,Count);
            Array.Clear(samples,0,Count); Array.Clear(residual,0,residual.Length);
            for(int v=0;v<Count;v++) samples[v].position=rest[v];
            ResolveObstacleSpawn();
            figPositionsStale=false;badVertex=-1; Steps=0; grabFace=grabRod=-1; appliedGrab=default; elasticEnergy=default;
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
            selfContact.RegisterSplitFaces(cut,old.plant.geometry);
            contact.CopyPose(old.contact);
            var speeds=new Vector3[Count];
            for(int i=0;i<cut.positions.Count;i++){positions[i]=cut.positions[i];speeds[i]=cut.velocities[i];}
            for(int i=0;i<plant.parent.Length;i++)
            {
                int src=cut.rodSource[i],dst=plant.Ghost(i);var r=plant.geometry.rods[i];var prev=old.plant.geometry.rods[src];
                if(src!=cut.ChangedRod)
                {
                    int ghost=old.plant.Ghost(src);
                    positions[dst]=old.positions[ghost];speeds[dst]=old.samples[ghost].velocity;
                    continue;
                }
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
        internal void ResolveObstacleSpawn()
        {
            if(contact==null || !contact.needsPlacement)return;
            CompletePendingStep();
            contact.needsPlacement=false;
            Vector3 requested=contact.center;
            bool stationary=contact.target==requested;
            if(contact.hits.Length==0)return;
            QueryContacts();
            int Worst()
            {
                int worst=0;
                for(int i=1;i<contact.hits.Length;i++)
                    if(contact.hits[i].clearance<contact.hits[worst].clearance)worst=i;
                return worst;
            }
            var hit=contact.hits[Worst()];
            if(hit.clearance>=0)return;
            const float gap=.0001f;
            for(int i=0;i<8 && hit.clearance<gap;i++)
            {
                if(hit.normal.sqrMagnitude<.5f)break;
                contact.Place(contact.center-hit.normal*(PlantContact.Offset+gap-hit.clearance));
                QueryContacts();
                hit=contact.hits[Worst()];
            }
            if(hit.clearance<gap)
            {
                // Opposing normals (including pinned leaves between gate walls) can
                // make local projection cycle. Either vertical exit is guaranteed clear.
                float top=float.NegativeInfinity,bottom=float.PositiveInfinity;
                foreach(var f in contact.features)
                {
                    void Include(int v)
                    {
                        top=Mathf.Max(top,positions[v].y+f.skin);
                        bottom=Mathf.Min(bottom,positions[v].y-f.skin);
                    }
                    Include(f.a);Include(f.b);if(f.c>=0)Include(f.c);
                }
                Vector3 up=Quaternion.Inverse(contact.rotation)*Vector3.up;
                var size=contact.extents;
                float half=Mathf.Abs(up.x)*(size.x+Mathf.Abs(contact.WallOffset(0).x))+
                    Mathf.Abs(up.y)*size.y+Mathf.Abs(up.z)*size.z+PlantContact.Offset+gap;
                float y=top+half-requested.y<requested.y-bottom+half?top+half:bottom-half;
                contact.Place(new Vector3(requested.x,y,requested.z));
                QueryContacts();
            }
            if(stationary)contact.target=contact.center;
        }
        public void QueryContacts()
        {
            if(contact==null || contact.shape==ObstacleShape.Off)return;
            SetContactPose();
            gpu.Dispatch(detect,(contact.hits.Length+63)/64,1,1);
            contactBuffer.GetData(contact.hits);
            contact.Summarize();
        }
        AsyncGPUReadbackRequest pendingReadback;
        bool pendingSelfContact;
        PlantContact.Hit[] pendingHits;
        AsyncGPUReadbackRequest obstacleReadback;
        public bool Pending {get;private set;}
        internal bool ReadbackReady=>!Pending || (pendingReadback.done && (pendingHits==null || obstacleReadback.done));

        public void Step(float dt, bool measureSweeps=true)
        {
            CompletePendingStep();
            DispatchStep(dt,measureSweeps,false);
        }
        internal void QueueStep(float dt)
        {
            CompletePendingStep();
            DispatchStep(dt,false,true);
        }
        void DispatchStep(float dt,bool measureSweeps,bool deferReadback,bool figSummaryOnly=false)
        {
            bool collide=contact!=null && contact.shape!=ObstacleShape.Off;
            gpu.SetInt("PlayerOrbit",collide && contact.playerOrbit?1:0);
            gpu.SetInt("Shape",collide?(int)contact.shape:0);
            if(collide)
            {
                ResolveObstacleSpawn();
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
            if(hasFragments)gpu.Dispatch(prepareFall,blocks,1,1);
            bool trackClusters=figSummaryOnly && useFigClusters && figClusterRelax>=0 && !collide && !self;
            if(trackClusters)UpdateFigClusters();
            bool selective=trackClusters && !fullFigSweeps;
            FigSelectiveStep=selective;
            int dualBlocks=(stiffFigJoints.Count+63)/64;
            if(dualBlocks>0){
                bool grabbed=grabRod>=0 || grabFace>=0;
                gpu.SetInt("FigLoadReleased",figWasGrabbed && !grabbed && !collide && !self && gravityScale==0?1:0);
                figWasGrabbed=grabbed;
                gpu.SetInt("FigBeginStep",1);
                gpu.Dispatch(figDualKernel,dualBlocks,1,1);
                gpu.SetInt("FigBeginStep",0);
            }
            bool coarseRecovery=figClusterRelax>=0 && !collide && !self && gravityScale==0 && grabFace<0 && grabRod<0;
            if(figClusterRelax>=0){
                float guess=.7f;
                // Suppress extrapolation as the worst node approaches the 1 mm
                // sleep envelope. The inertial target still uses the full velocity.
                if(figSummaryOnly && coarseRecovery)
                    guess=Mathf.Lerp(.15f,.7f,Mathf.InverseLerp(.001f,.012f,FigMaxRestError));
                gpu.SetFloat("VelocityGuess",guess);
            }
            gpu.Dispatch(predict,blocks,1,1);
            if(structuralCount>0) gpu.Dispatch(cacheRestMotion,(structuralCount+63)/64,1,1);
            if(self)gpu.Dispatch(snapshot,blocks,1,1);
            if(collide)gpu.Dispatch(detect,contactBlocks,1,1);
            // Unloaded boughs also get an energy-tested whole-bough correction below.
            // Reserve the full local iteration budget for loaded or contacting geometry.
            if(trackClusters)RunFigSweeps(selective,dualBlocks,coarseRecovery?Math.Min(3,iterations):iterations);
            else for(int i=0;i<iterations;i++)
            {
                if(localPlants>0)gpu.Dispatch(localRelax,localPlants,1,1);
                else for(int group=0;group<colorCount;group++)
                {
                    int start=groupOffsets[group];
                    gpu.SetInt("GroupStart",start);
                    gpu.Dispatch(relax,(groupOffsets[group+1]-start)/64,1,1);
                }
                if(dualBlocks>0)gpu.Dispatch(figDualKernel,dualBlocks,1,1);
                if(collide)gpu.Dispatch(detect,contactBlocks,1,1);
                if(self)gpu.Dispatch(snapshot,blocks,1,1);
                if(!figSummaryOnly && (measureSweeps || i==iterations-1) && !(coarseRecovery && i==iterations-1))
                {
                    gpu.SetInt("Sweep",i); gpu.Dispatch(measure,blocks,1,1);
                }
            }
            if(selective)gpu.Dispatch(figFollow,blocks,1,1);
            if(coarseRecovery){
                gpu.Dispatch(figTrialEnergy,figTrialGroups,1,1);
                gpu.Dispatch(figChooseTrial,1,1,1);
                gpu.Dispatch(figApplyTrial,blocks,1,1);
                if(!figSummaryOnly){
                    gpu.SetInt("Sweep",iterations-1);
                    gpu.Dispatch(measure,blocks,1,1);
                }
            }
            if(!figSummaryOnly)gpu.Dispatch(finish,blocks,1,1);
            if(figSummaryOnly)
            {
                gpu.Dispatch(figParticles,figGroupCount,1,1);
                gpu.Dispatch(figBough,1,1,1);
                if(deferReadback){
                    pendingReadback=AsyncGPUReadback.Request(figResult);
                    pendingFig=true;
                    Pending=true;
                }else ReadFigSummary(false);
                return;
            }
            if(deferReadback)
            {
                // The next tick needs positions for its CPU contact search. Enqueue the
                // copy now, so rendering can proceed before the CPU consumes it.
                pendingReadback=AsyncGPUReadback.Request(sampleBuffer);
                pendingSelfContact=self;
                pendingHits=collide?contact.hits:null;
                if(collide)obstacleReadback=AsyncGPUReadback.Request(contactBuffer,contact.hits.Length*contactBuffer.stride,0);
                Pending=true;
                return;
            }
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
            int firstSweep=measureSweeps?0:iterations-1;
            residualBuffer.GetData(residualData,Count*firstSweep,Count*firstSweep,Count*(iterations-firstSweep));
            AcceptSamples(self);
            for(int i=firstSweep;i<iterations;i++)
            {
                float max=0;
                for(int v=0;v<Count;v++) {
                    float r=residualData[i*Count+v];
                    if(!float.IsFinite(r)) badVertex=v;
                    max=Math.Max(max,r);
                }
                residual[i]=max;
            }
        }
        bool pendingFig;
        internal float FigMaxRestError {get;private set;}
        internal float FigMaxTravel {get;private set;}
        struct FigSummary { public Vector3 lo; public float maxTravelSq; public Vector3 hi; public int invalid,awakeTwigNodes; public float maxRestErrorSq; }
        int figParticles,figBough,figGroupCount;
        ComputeBuffer figResult;
        readonly FigSummary[] figFallback=new FigSummary[1];
        bool figPositionsStale;
        internal void QueueFigStep()
        {
            DispatchStep(1f/60,false,SystemInfo.supportsAsyncGPUReadback,true);
        }

        void ReadFigSummary(bool async)
        {
            FigSummary summary;
            if(async && !pendingReadback.hasError)summary=pendingReadback.GetData<FigSummary>()[0];
            else {figResult.GetData(figFallback);summary=figFallback[0];}
            // Choose the next sweep from the completed tick. Separate thresholds avoid mode chatter.
            if(useFigClusters && figTwigNodes>0)
                fullFigSweeps=summary.awakeTwigNodes >= figTwigNodes*(fullFigSweeps?.70f:.85f);
            badVertex=summary.invalid;
            FigMaxRestError=Mathf.Sqrt(summary.maxRestErrorSq);
            FigMaxTravel=Mathf.Sqrt(summary.maxTravelSq);
            particleBounds.SetMinMax(summary.lo,summary.hi);
            figPositionsStale=true;
            Steps++;
        }

        internal void RefreshFigPickPositions()
        {
            if(!figPositionsStale)return;
            x.GetData(positions);
            figPositionsStale=false;
        }

        internal void CompletePendingStep()
        {
            if(!Pending)return;
            if(!pendingReadback.done)pendingReadback.WaitForCompletion();
            if(pendingFig){
                pendingFig=false;Pending=false;ReadFigSummary(true);return;
            }
            bool mapped=!pendingReadback.hasError;
            if(!mapped)sampleBuffer.GetData(samples);
            if(pendingHits!=null && pendingHits==contact.hits && contact.shape!=ObstacleShape.Off && !contact.needsPlacement)
            {
                if(!obstacleReadback.done)obstacleReadback.WaitForCompletion();
                if(obstacleReadback.hasError)contactBuffer.GetData(contact.hits);
                else obstacleReadback.GetData<PlantContact.Hit>().CopyTo(contact.hits);
                contact.Summarize();
            }
            Pending=false;
            if(mapped)
            {
                var source=pendingReadback.GetData<Sample>();
                source.CopyTo(samples);
                AcceptMapped(source,pendingSelfContact);
            }
            else AcceptSamples(pendingSelfContact);
        }
        const int SampleBatch=256;
        struct Summary {public Vector3 lo,hi,grab;public Vector2 energy;public int invalid;}
        NativeArray<Vector3> mappedPositions;
        NativeArray<Summary> summaries;
        [BurstCompile(FloatPrecision.High,FloatMode.Strict)]
        struct ReadSamples : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Sample> source;
            [NativeDisableParallelForRestriction] public NativeArray<Vector3> positions;
            public NativeArray<Summary> summaries;
            public void Execute(int batch)
            {
                int first=batch*SampleBatch,end=Math.Min(source.Length,first+SampleBatch);
                Summary sum=new Summary{lo=source[first].position,hi=source[first].position,invalid=-1};
                for(int v=first;v<end;v++)
                {
                    Sample item=source[v];
                    positions[v]=item.position;
                    sum.lo=Vector3.Min(sum.lo,item.position);sum.hi=Vector3.Max(sum.hi,item.position);
                    sum.energy+=item.energy;sum.grab+=item.grabForce;
                    if(!float.IsFinite(item.position.sqrMagnitude+item.velocity.sqrMagnitude+item.force.sqrMagnitude+
                        item.energy.sqrMagnitude+item.grabForce.sqrMagnitude))sum.invalid=v;
                }
                summaries[batch]=sum;
            }
        }
        void AcceptMapped(NativeArray<Sample> source,bool self)
        {
            if(!mappedPositions.IsCreated)
            {
                mappedPositions=new NativeArray<Vector3>(Count,Allocator.Persistent);
                summaries=new NativeArray<Summary>((Count+SampleBatch-1)/SampleBatch,Allocator.Persistent);
            }
            new ReadSamples{source=source,positions=mappedPositions,summaries=summaries}.Schedule(summaries.Length,4).Complete();
            mappedPositions.CopyTo(positions);
            elasticEnergy=default;appliedGrab=default;badVertex=-1;
            Vector3 lo=summaries[0].lo,hi=summaries[0].hi;
            for(int i=0;i<summaries.Length;i++)
            {
                var sum=summaries[i];lo=Vector3.Min(lo,sum.lo);hi=Vector3.Max(hi,sum.hi);
                elasticEnergy+=sum.energy;appliedGrab+=sum.grab;badVertex=Math.Max(badVertex,sum.invalid);
            }
            particleBounds.SetMinMax(lo,hi);
            if(self)selfContact.Measure(positions);else selfContact.ClearMeasurements();
            Steps++;
        }
        void AcceptSamples(bool self)
        {
            elasticEnergy=default; appliedGrab=default; badVertex=-1;
            Vector3 lo=samples[0].position,hi=lo;
            for(int v=0;v<Count;v++)
            {
                Sample s=samples[v];
                lo=Vector3.Min(lo,s.position);hi=Vector3.Max(hi,s.position);
                positions[v]=s.position; elasticEnergy+=s.energy; appliedGrab+=s.grabForce;
                if(!float.IsFinite(s.position.sqrMagnitude+s.velocity.sqrMagnitude+s.force.sqrMagnitude+
                    s.energy.sqrMagnitude+s.grabForce.sqrMagnitude)) badVertex=v;
            }
            particleBounds.SetMinMax(lo,hi);
            if(self)selfContact.Measure(positions);else selfContact.ClearMeasurements();
            Steps++;
        }
        // Only the displayed sheet owns a render mesh. Rebind after cuts and mode changes.
        GraphicsBuffer skinVertices;
        int skinKernel,normalKernel,skinCount,shellVertexCount;
        float skinRadius;
        Bounds particleBounds;
        public Bounds SkinBounds { get { var b=particleBounds;b.Expand(2*skinRadius);return b; } }
        struct RenderVertex
        {
            public Vector3 position,normal;
            public Color32 color;
            public Vector2 uv;
        }
        const MeshUpdateFlags CutMeshFlags=MeshUpdateFlags.DontRecalculateBounds|MeshUpdateFlags.DontValidateIndices;
        int indexCapacity;
        Vector3[] restSkinNormals;
        Vector2Int[] renderBindings;
        Vector3Int[] renderRods;
        ComputeBuffer bindingBuffer,skinOffsetBuffer,skinNormalBuffer;
        Vector3[] localNormals;
        int[] shellVertices,normalOffsets;
        Vector2Int[] normalEdges;
        internal void PrepareRenderData(Vector3[] normals,PlantCut cut=null,VbdSheet previous=null)
        {
            skinCount=plant.skin.Length;
            restSkinNormals=normals??new Vector3[skinCount];
            int retained=previous?.skinCount??0;
            if(previous!=null)
            {
                Array.Copy(previous.restSkinNormals,restSkinNormals,retained);
                foreach(int i in cut.changedTriangles)
                {
                    var f=plant.geometry.surface[i];var p=plant.geometry.skin;
                    var n=Vector3.Cross(p[f.b]-p[f.a],p[f.c]-p[f.a]);
                    if(f.a>=retained)restSkinNormals[f.a]+=n;
                    if(f.b>=retained)restSkinNormals[f.b]+=n;
                    if(f.c>=retained)restSkinNormals[f.c]+=n;
                }
                for(int i=retained;i<skinCount;i++)restSkinNormals[i].Normalize();
            }
            renderBindings=new Vector2Int[skinCount];
            localNormals=new Vector3[skinCount];
            if(previous!=null)
            {
                Array.Copy(previous.localNormals,localNormals,retained);
                Array.Copy(previous.renderBindings,renderBindings,retained);
                skinRadius=previous.skinRadius;
            }
            renderRods=new Vector3Int[plant.parent.Length];
            for(int i=0;i<renderRods.Length;i++)
            {
                var rod=plant.geometry.rods[i];
                renderRods[plant.renderSlot[i]]=new Vector3Int(rod.a,rod.b,plant.Ghost(i));
            }
            void BindVertex(int v)
            {
                int r=plant.skinRod[v];
                renderBindings[v]=new Vector2Int(plant.skinNode[v],r<0?-1:plant.renderSlot[r]);
                if(r<0)return;
                localNormals[v]=Quaternion.Inverse(plant.restFrames[r])*restSkinNormals[v];
                skinRadius=Mathf.Max(skinRadius,plant.skinOffset[v].magnitude);
            }
            for(int i=retained;i<skinCount;i++)BindVertex(i);
            foreach(int v in plant.rebound)BindVertex(v);
            var shells=new List<int>();
            var triangles=plant.skinTriangles;
            var vertexRows=new Dictionary<int,int>();
            var counts=new List<int>();
            foreach(int f in plant.shells)for(int j=0;j<3;j++)
            {
                int v=triangles[3*f+j];
                if(!vertexRows.TryGetValue(v,out int row))
                {
                    row=counts.Count;vertexRows.Add(v,row);shells.Add(v);counts.Add(0);
                }
                counts[row]++;
            }
            shellVertices=shells.ToArray();shellVertexCount=shellVertices.Length;
            normalOffsets=new int[shellVertexCount+1];
            for(int i=0;i<counts.Count;i++)normalOffsets[i+1]=normalOffsets[i]+counts[i];
            var heads=(int[])normalOffsets.Clone();normalEdges=new Vector2Int[3*plant.shells.Count];
            foreach(int f in plant.shells)for(int j=0;j<3;j++)
            {
                int i=f*3;
                normalEdges[heads[vertexRows[triangles[i+j]]]++]=new Vector2Int(triangles[i+(j+1)%3],triangles[i+(j+2)%3]);
            }
        }
        public void BindRenderMesh(Mesh mesh)=>BindRenderMesh(mesh,null,null);
        internal void BindRenderMesh(Mesh mesh,PlantCut cut,VbdSheet previous)
        {
            skinKernel=gpu.FindKernel("DeformSkin");normalKernel=gpu.FindKernel("SkinNormals");
            if(renderBindings==null)
            {
                mesh.RecalculateNormals();
                PrepareRenderData(mesh.normals);
            }
            mesh.vertexBufferTarget|=GraphicsBuffer.Target.Raw;
            var bounds=mesh.bounds;
            int[] indices=plant.skinTriangles;
            bool allocate=previous==null || mesh.vertexCount<skinCount || previous.indexCapacity<indices.Length;
            indexCapacity=allocate?indices.Length+6144:previous.indexCapacity;
            if(allocate)
            {
                mesh.SetVertexBufferParams(skinCount+2048,
                    new VertexAttributeDescriptor(VertexAttribute.Position),
                    new VertexAttributeDescriptor(VertexAttribute.Normal),
                    new VertexAttributeDescriptor(VertexAttribute.Color,VertexAttributeFormat.UNorm8,4),
                    new VertexAttributeDescriptor(VertexAttribute.TexCoord0,VertexAttributeFormat.Float32,2));
                mesh.SetIndexBufferParams(indexCapacity,IndexFormat.UInt32);
                mesh.SetIndexBufferData(indices,0,0,indices.Length,CutMeshFlags);
            }
            else
            {
                cut.changedTriangles.Sort();
                for(int i=0;i<cut.changedTriangles.Count;)
                {
                    int first=cut.changedTriangles[i++],last=first;
                    while(i<cut.changedTriangles.Count && cut.changedTriangles[i]==last+1)last=cut.changedTriangles[i++];
                    mesh.SetIndexBufferData(indices,first*3,first*3,3*(last-first+1),CutMeshFlags);
                }
            }
            int begin=allocate?0:previous.skinCount;
            var vertices=new RenderVertex[skinCount-begin];var geom=plant.geometry;
            for(int i=begin;i<skinCount;i++)vertices[i-begin]=new RenderVertex{
                position=geom.skin[i],normal=restSkinNormals[i],color=geom.colors[i],uv=geom.bladeUV[i]};
            if(vertices.Length>0)mesh.SetVertexBufferData(vertices,0,begin,vertices.Length,0,CutMeshFlags);
            mesh.subMeshCount=1;
            mesh.SetSubMesh(0,new SubMeshDescriptor(0,indices.Length){bounds=bounds,vertexCount=skinCount},CutMeshFlags);
            mesh.bounds=bounds;particleBounds=bounds;
            BindSkinBuffers(mesh,previous);
        }

        internal void BindFigMesh(Mesh mesh)
        {
            figParticles=gpu.FindKernel("FigParticleSummary");
            figBough=gpu.FindKernel(figStates==null?"FigBoughSummary":"FigClusterBoughSummary");
            if(figStates!=null){
                gpu.SetBuffer(figBough,"FigState",figStates);
                gpu.SetBuffer(figBough,"FigRange",figRanges);
                gpu.SetInt("FigClusterCount",initialFigStates.Length-1);
            }
            figGroupCount=(Count+127)/128;
            var groups=new ComputeBuffer(figGroupCount,40);
            buffers.Add(groups);
            figResult=new ComputeBuffer(1,40);
            buffers.Add(figResult);
            gpu.SetBuffer(figParticles,"X",x);gpu.SetBuffer(figParticles,"Old",oldPositions);
            if(figRest==null){
                figRest=new ComputeBuffer(Count,12);
                buffers.Add(figRest);
                figRest.SetData(rest);
            }
            gpu.SetBuffer(figParticles,"FigRest",figRest);
            gpu.SetBuffer(figParticles,"Velocity",velocity);
            gpu.SetBuffer(figParticles,"FigSummaryOut",groups);
            gpu.SetInt("FigGroupCount",figGroupCount);
            gpu.SetBuffer(figBough,"FigSummaryIn",groups);gpu.SetBuffer(figBough,"FigSummaryOut",figResult);
            skinKernel=gpu.FindKernel("DeformSkin");normalKernel=gpu.FindKernel("SkinNormals");
            PrepareRenderData(mesh.normals);
            mesh.vertexBufferTarget|=GraphicsBuffer.Target.Raw;
            particleBounds=mesh.bounds;
            BindSkinBuffers(mesh,null);
        }

        void BindSkinBuffers(Mesh mesh,VbdSheet previous)
        {
            skinVertices=mesh.GetVertexBuffer(0);
            gpu.SetBuffer(skinKernel,"SkinVertices",skinVertices);
            gpu.SetBuffer(normalKernel,"SkinVertices",skinVertices);
            gpu.SetInt("SkinCount",skinCount);gpu.SetInt("SkinStride",mesh.GetVertexBufferStride(0));
            gpu.SetInt("PositionOffset",mesh.GetVertexAttributeOffset(VertexAttribute.Position));
            gpu.SetInt("NormalOffset",mesh.GetVertexAttributeOffset(VertexAttribute.Normal));
            gpu.SetBuffer(skinKernel,"X",x);
            void Bind<T>(string name,T[] values,int stride,int kernel) where T:struct
            {
                var buf=new ComputeBuffer(Math.Max(1,values.Length),stride);
                buffers.Add(buf);if(values.Length>0)buf.SetData(values);gpu.SetBuffer(kernel,name,buf);
            }
            Bind("RenderRods",renderRods,12,skinKernel);
            bool reuse=previous!=null && previous.bindingBuffer.count>=skinCount;
            ComputeBuffer Patch<T>(ComputeBuffer old,string name,T[] data,int stride) where T:struct
            {
                ComputeBuffer buf;
                if(reuse)
                {
                    buf=old;
                    int first=previous.skinCount;
                    if(skinCount>first)buf.SetData(data,first,first,skinCount-first);
                    foreach(int v in plant.rebound)buf.SetData(data,v,v,1);
                    previous.buffers.Remove(buf);
                }
                else
                {
                    buf=new ComputeBuffer(skinCount+2048,stride);
                    buf.SetData(data,0,0,skinCount);
                }
                buffers.Add(buf);gpu.SetBuffer(skinKernel,name,buf);return buf;
            }
            bindingBuffer=Patch(previous?.bindingBuffer,"SkinNodes",renderBindings,8);
            skinOffsetBuffer=Patch(previous?.skinOffsetBuffer,"SkinOffsets",plant.skinOffset,12);
            skinNormalBuffer=Patch(previous?.skinNormalBuffer,"SkinRestNormals",localNormals,12);
            gpu.SetInt("ShellVertexCount",shellVertexCount);
            if(shellVertexCount>0)
            {
                Bind("ShellVertices",shellVertices,4,normalKernel);Bind("NormalOffsets",normalOffsets,4,normalKernel);
                Bind("NormalEdges",normalEdges,8,normalKernel);
            }
            shellVertices=normalOffsets=null;normalEdges=null;
        }
        struct PlantRange {public int firstNode,nodes,firstGhost,ghosts;}
        public bool DrawSkinOnGpu()
        {
            if(skinVertices==null)return false;
            gpu.Dispatch(skinKernel,(skinCount+63)/64,1,1);
            if(shellVertexCount>0)gpu.Dispatch(normalKernel,(shellVertexCount+63)/64,1,1);
            return true;
        }
        public void Dispose()
        {
            CompletePendingStep();
            if(mappedPositions.IsCreated)mappedPositions.Dispose();
            if(summaries.IsCreated)summaries.Dispose();
            figSparseSweeps?.Release();figDenseSweeps?.Release();
            skinVertices?.Dispose();
            selfContact?.Dispose();
            foreach(var buffer in buffers) buffer.Release();
            buffers.Clear();
            if(Application.isPlaying)UnityEngine.Object.Destroy(gpu);
            else UnityEngine.Object.DestroyImmediate(gpu);
        }
    }
}
