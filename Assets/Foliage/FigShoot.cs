using System;
using System.Collections.Generic;
using UnityEngine;

namespace Foliage
{
    public sealed partial class FigShoot
    {
        public readonly PlantGeometry geometry=new();
        public readonly Vector3 origin;
        BladeSpawnClearance clearance=new(.12f);
        readonly HashSet<int> aerialRods=new();
        readonly System.Random buds;
        readonly int density;
        readonly float treeScale;
        public readonly List<int> neighboringLeaf = new List<int>();
        public int LeafyTwigCount {get;private set;}
        internal FigShoot(Vector3 origin,Vector3 embed,float radius,FigSettings settings,int seed){
            buds=new System.Random(seed);
            density=Mathf.Clamp(settings.canopyDensity,1,5);
            treeScale=settings.size;
            this.origin=origin;
            geometry.distributedStemSupport=false;
            geometry.fixedCollarVertices=12;
            int baseNode=geometry.Node(-embed),socket=geometry.Node(Vector3.zero);
            geometry.rootNodes.Add(baseNode);
            geometry.rods.Add(new PlantGeometry.Rod{a=baseNode,b=socket,radius=radius});
        }
        internal void AddAerialRoot(int joint,Vector3 drop,float radius,bool grounded,float soilHeight){
            Vector3 start=geometry.nodes[joint],end=start+drop;
            if(grounded)end.y=soilHeight-origin.y;
            var path=new List<int>{joint};
            int segments=Mathf.Clamp(Mathf.CeilToInt(drop.magnitude/.6f),4,28);
            for(int i=1;i<=segments;i++)path.Add(geometry.Node(Vector3.Lerp(start,end,i/(float)segments)));
            int first=geometry.rods.Count;
            geometry.Tube(path,0,radius,grounded?.84f:.24f);
            for(int i=first;i<geometry.rods.Count;i++)aerialRods.Add(i);
            if(grounded)geometry.groundPins.Add(path[segments]);
        }
        public IReadOnlyList<int> GroundPins=>geometry.groundPins;

        readonly List<(int[] path,int depth,float length)> foliage=new();
        internal void PlanFoliage(int[] path,int depth,float length)=>foliage.Add((path,depth,length));
        internal void FinishCrown(){
            foreach(var growth in foliage){
                if(growth.depth<=2)GrowLeafSprays(growth.path,growth.depth,growth.length);
                if(growth.depth>=2)GrowCrownLaterals(growth.path,growth.length);
            }
            foliage.Clear();
            clearance=null;
        }

        internal int[] AddTwig(Vector3 start,Vector3 mid,Vector3 end,float radius,float tip,int joint)
        {
            var g=geometry;
            Vector3 control=2*mid-(start+end)*.5f;
            var path=new List<int>();
            if(joint<0){
                joint=g.Node(start-origin);
                g.rootNodes.Add(joint);
            }
            path.Add(joint);
            const int segments=6;
            for(int i=1;i<=segments;i++){
                float t=i/(float)segments;
                path.Add(g.Node((1-t)*(1-t)*start+2*(1-t)*t*control+t*t*end-origin));
            }
            WoodyTube(path,radius,tip/radius,12);
            return path.ToArray();
        }

        // Ramp along the taper, segment by segment. The solver switches to an
        // augmented material-frame joint once the rigidity gain reaches one.
        internal static float WoodRigidityGain(float relativeRadius){
            float maturity=Mathf.InverseLerp(.032f,.17f,relativeRadius);
            return 11f*Mathf.SmoothStep(0,1,maturity);
        }
        void WoodyTube(List<int> path,float radius,float taper,int sides){
            var g=geometry;
            int first=g.rods.Count;
            g.Tube(path,0,radius,taper,sides);
            for(int i=first;i<g.rods.Count;i++){
                var rod=g.rods[i];
                rod.woodRigidityGain=WoodRigidityGain(rod.radius/treeScale);
                g.rods[i]=rod;
            }
        }

        PlantGeometry.TwigCluster StartCluster(int anchor)=>new PlantGeometry.TwigCluster{
            anchor=anchor,firstNode=geometry.nodes.Count,firstRod=geometry.rods.Count};
        void EndCluster(PlantGeometry.TwigCluster cluster){
            cluster.endNode=geometry.nodes.Count;cluster.endRod=geometry.rods.Count;
            geometry.twigClusters.Add(cluster);
        }

        float Range(float min,float max)=>Mathf.Lerp(min,max,(float)buds.NextDouble());

        // Buds live at real nodes, on alternating sides of the outer three branch orders.
        // Lower shoots sweep outward and sag; shoots at the top turn back toward light.
        internal void GrowLeafSprays(int[] path,int depth,float parentLength)
        {
            var g=geometry;
            int count=Mathf.Max(1,density+1-depth);
            float phase=Range(0,2*Mathf.PI);
            for(int i=0;i<count;i++){
                int at=Mathf.Clamp(Mathf.RoundToInt((i+.65f)/count*(path.Length-1)),1,path.Length-1);
                int baseNode=path[at];
                Vector3 axis=(g.nodes[baseNode]-g.nodes[path[at-1]]).normalized;
                Vector3 side=Vector3.Cross(Vector3.up,axis);
                if(side.sqrMagnitude<.001f)side=Vector3.right;
                side.Normalize();
                float angle=phase+i*2.40f+Range(-.23f,.23f);
                Vector3 radial=side*Mathf.Cos(angle)+Vector3.Cross(axis,side)*Mathf.Sin(angle);
                Vector3 outward=(g.nodes[baseNode]+origin);outward.y=0;
                Vector3 heading=(axis*.58f+radial*.95f+outward.normalized*.28f+Vector3.up*Range(-.18f,.28f)).normalized;
                float length=Mathf.Clamp(parentLength*Range(.60f,1.15f),.65f*treeScale,1.85f*treeScale);
                var cluster=StartCluster(baseNode);
                int[] twig=LeafyTwig(baseNode,heading,length,Range(-.27f,.12f),7);
                // Short lateral branchlets fill the spaces on both sides of the longer twig.
                int laterals=depth==0?2:1;
                for(int j=0;j<laterals;j++){
                    int bud=j==0?3:5;
                    Vector3 across=Vector3.Cross(Vector3.up,heading).normalized*(j==0?1:-1);
                    Vector3 fan=(heading*.5f+across*.85f+Vector3.up*Range(-.35f,.28f)).normalized;
                    LeafyTwig(twig[bud],fan,length*Range(.40f,.68f),Range(-.32f,.12f),5);
                }
                EndCluster(cluster);
            }
        }

        // Intermediate boughs carry long, arched side branches in the gaps between forks.
        internal void GrowCrownLaterals(int[] path,float length)
        {
            var g=geometry;
            float phase=Range(0,2*Mathf.PI);
            for(int i=0;i<density;i++){
                int at=1+Mathf.RoundToInt(i*4f/Mathf.Max(1,density-1));
                int baseNode=path[at];
                Vector3 start=g.nodes[baseNode],axis=(start-g.nodes[path[at-1]]).normalized;
                Vector3 side=Vector3.Cross(Vector3.up,axis).normalized;
                if(side.sqrMagnitude<.5f)side=Vector3.right;
                float angle=phase+i*2.4f;
                Vector3 radial=side*Mathf.Cos(angle)+Vector3.Cross(axis,side)*Mathf.Sin(angle);
                Vector3 dir=(axis*.28f+radial+Vector3.up*.24f).normalized;
                float reach=Mathf.Clamp(length*Range(.8f,1.3f),1.5f*treeScale,4.2f*treeScale);
                var cluster=StartCluster(baseNode);
                var spur=new List<int>{baseNode};
                for(int j=1;j<=5;j++){
                    float t=j/5f;
                    spur.Add(g.Node(start+reach*(dir*t+Vector3.up*(.21f*Mathf.Sin(t*Mathf.PI)-.16f*t*t))));
                }
                WoodyTube(spur,reach*.022f,.23f,8);
                LeafyTwigCount++;
                Vector3 fan=Vector3.Cross(Vector3.up,dir).normalized;
                for(int j=1;j<=5;j++){
                    Vector3 bearing=(dir*.65f+fan*(j%2==0?.85f:-.85f)+Vector3.up*Range(-.22f,.28f)).normalized;
                    var twig=LeafyTwig(spur[j],bearing,reach*Range(.35f,.63f),Range(-.26f,.08f),7);
                    LeafyTwig(twig[4],(dir+bearing*.25f+Vector3.up*Range(-.35f,.2f)).normalized,reach*.28f,-.14f,5);
                }
                EndCluster(cluster);
            }
        }

        int[] LeafyTwig(int parent,Vector3 heading,float length,float droop,int nodes)
        {
            LeafyTwigCount++;
            var g=geometry;
            var path=new List<int>{parent};
            Vector3 start=g.nodes[parent];
            Vector3 side=Vector3.Cross(Vector3.up,heading).normalized;
            if(side.sqrMagnitude<.5f)side=Vector3.right;
            float curve=Range(-.18f,.18f);
            for(int j=1;j<=nodes;j++){
                float t=j/(float)nodes;
                path.Add(g.Node(start+length*(heading*t+Vector3.up*(droop*t*t+.13f*t*(1-t))+side*(curve*t*t))));
            }
            WoodyTube(path,Mathf.Clamp(length*.020f,.011f*treeScale,.038f*treeScale),.26f,6);
            var leafSettings=PlantSettings.Default;
            leafSettings.resolution=4;leafSettings.width=Range(.21f,.30f);leafSettings.curvature=Range(.25f,.5f);
            float bladeLength=Range(.27f,.40f)*treeScale;
            float roll=Range(-32,32);
            float tone=Range(.16f,.80f);
            for(int j=1;j<=nodes;j++){
                Vector3 axis=(g.nodes[path[j]]-g.nodes[path[j-1]]).normalized;
                Vector3 lateral=Quaternion.AngleAxis(roll+Range(-12,12),axis)*side*(j%2==0?1:-1);
                Vector3 direction=(lateral+axis*Range(.24f,.68f)+Vector3.up*Range(-.24f,.20f)).normalized;
                int leaf=g.bladeRows.Count;
                neighboringLeaf.Add(j==nodes?leaf-1:leaf+1);
                int part=2*leaf+1;
                int neck=g.Node(g.nodes[path[j]]+direction*(.04f*treeScale));
                int first=g.colors.Count;
                g.Blade(neck,direction,bladeLength*Range(.82f,1.13f),leafSettings,part+1,clearance);
                float flush=Mathf.Clamp01(tone+Range(-.12f,.12f)+(j==nodes?.15f:0));
                for(int v=first;v<g.colors.Count;v++){var color=g.colors[v];color.a=flush;g.colors[v]=color;}
                g.Tube(new List<int>{path[j],neck},part,.006f*treeScale,.65f,5);
            }
            return path.ToArray();
        }
        internal static float YoungWood(float radius)=>1-Mathf.SmoothStep(0,1,Mathf.InverseLerp(.008f,.075f,radius));

        readonly int[] boundarySource=new int[12];
        public IReadOnlyList<int> FixedRingVertices=>boundarySource;
        readonly Vector3[] boundaryGrain=new Vector3[12],boundaryNormals=new Vector3[12];
        readonly Vector4[] boundaryDirection=new Vector4[12];
        readonly float[] boundaryAge=new float[12];
        internal void SetBoundary(int vertex,int source,Vector3 grain,Vector4 direction,float age){
            boundarySource[vertex]=source;boundaryGrain[vertex]=grain;
            boundaryDirection[vertex]=direction;boundaryAge[vertex]=age;
        }
        internal void StoreBoundaryNormals(Vector3[] normals){
            for(int i=0;i<12;i++)boundaryNormals[i]=normals[boundarySource[i]];
        }
        readonly List<Vector3> normalBuffer=new();
        internal void UpdateNormals(Mesh mesh){
            mesh.RecalculateNormals();mesh.GetNormals(normalBuffer);
            for(int i=0;i<12;i++)normalBuffer[i]=boundaryNormals[i];
            mesh.SetNormals(normalBuffer);
        }
        internal sealed class MeshData
        {
            internal readonly int[] wood,leaves;
            internal readonly Vector3[] grain;
            internal readonly Vector4[] direction,age;

            internal MeshData(FigShoot shoot){
                var g=shoot.geometry;
                int woodyFaces=0;
                bool Woody(PlantGeometry.SkinFace f)=>f.rod>=0 && g.rods[f.rod].part==0;
                foreach(var f in g.surface)if(Woody(f))woodyFaces++;
                wood=new int[3*woodyFaces];
                leaves=new int[3*(g.surface.Count-woodyFaces)];
                var owner=new int[g.skin.Count];
                int w=0,l=0;
                foreach(var f in g.surface){
                    if(Woody(f)){
                        wood[w++]=f.a;wood[w++]=f.b;wood[w++]=f.c;
                        owner[f.a]=owner[f.b]=owner[f.c]=f.rod+1;
                    }else{
                        leaves[l++]=f.a;leaves[l++]=f.b;leaves[l++]=f.c;
                    }
                }
                grain=new Vector3[owner.Length];
                direction=new Vector4[owner.Length];
                age=new Vector4[owner.Length];
                var axes=new Vector3[g.rods.Count];
                var maturity=new float[g.rods.Count];
                for(int i=0;i<g.rods.Count;i++){
                    var rod=g.rods[i];
                    if(rod.part!=0)continue;
                    axes[i]=(g.nodes[rod.b]-g.nodes[rod.a]).normalized;
                    maturity[i]=shoot.aerialRods.Contains(i)?4:2+YoungWood(rod.radius);
                }
                for(int v=0;v<owner.Length;v++){
                    int id=owner[v]-1;
                    if(id<0){grain[v]=g.bladeUV[v];continue;}
                    var p=g.skin[v];
                    var rest=p+shoot.origin;
                    grain[v]=Vector3.ProjectOnPlane(p-g.nodes[g.rods[id].a],axes[id]);
                    direction[v]=axes[id];
                    age[v]=new Vector4(rest.x,rest.y,rest.z,maturity[id]);
                }
                for(int i=0;i<12;i++){
                    grain[i]=shoot.boundaryGrain[i];direction[i]=shoot.boundaryDirection[i];
                    Vector3 p=g.skin[i]+shoot.origin;
                    age[i]=new Vector4(p.x,p.y,p.z,2+shoot.boundaryAge[i]);
                }
            }
        }

        public Mesh CreateMesh()=>CreateMesh(new MeshData(this));
        internal Mesh CreateMesh(MeshData data,bool gpuSkin=false){
            var mesh=new Mesh{name="Fig twig and leaves",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
            if(gpuSkin)mesh.vertexBufferTarget|=GraphicsBuffer.Target.Raw;
            mesh.SetVertices(geometry.skin);mesh.SetColors(geometry.colors);
            mesh.SetUVs(0,data.grain);mesh.SetUVs(1,data.direction);mesh.SetUVs(2,data.age);
            mesh.subMeshCount=2;
            mesh.SetTriangles(data.wood,0,false);mesh.SetTriangles(data.leaves,1,true);
            UpdateNormals(mesh);
            return mesh;
        }

#if UNITY_EDITOR
        [UnityEditor.MenuItem("Tools/Foliage/Check twig rebound against a sphere")]
        static void CheckTwigContactRebound()
        {
            var shoot=new FigShoot(Vector3.zero,Vector3.right*.15f,.15f,new FigSettings{size=1},2);
            var scaffold=shoot.AddTwig(Vector3.zero,Vector3.right*.3f,Vector3.right*.6f,.12f,.07f,1);
            int socket=scaffold[scaffold.Length-1];
            var cluster=shoot.StartCluster(socket);
            int[] twig=shoot.LeafyTwig(socket,Vector3.right,.8f,-.12f,7);
            shoot.EndCluster(cluster);
            int tip=twig[6];
            foreach(var model in new[]{ContactModel.Penalty,ContactModel.OGC}){
                using var solver=new VbdSheet(plant:new RodPlant(shoot.geometry)){gravityScale=0};
                solver.selfContact.enabled=false;
                var obstacle=solver.contact;
                Vector3 rest=solver.rest[tip],home=rest-Vector3.up*.32f;
                obstacle.model=model;
                obstacle.Configure(ObstacleShape.Sphere,home,Quaternion.identity,.075f);
                int rodContacts=0;
                float peak=0,peakPenetration=0;
                float Step(){
                    solver.Step(1f/60,false);
                    if(solver.badVertex>=0)throw new InvalidOperationException($"Invalid twig node {solver.badVertex}");
                    float error=Vector3.Distance(solver.positions[tip],rest);
                    peak=Mathf.Max(peak,error);
                    peakPenetration=Mathf.Max(peakPenetration,obstacle.physicalPenetration);
                    rodContacts=Mathf.Max(rodContacts,obstacle.rods);
                    return error;
                }
                for(int i=0;i<70;i++){
                    if(i==2)obstacle.target=rest+Vector3.up*.1f;
                    Step();
                }
                // Leave the sphere in the solver as it retreats; the ordinary contact
                // and recovery passes must run together throughout this test.
                obstacle.target=home;
                int lastContact=0,lastOutside=0;
                for(int i=1;i<=120;i++){
                    if(Step()>.001f)lastOutside=i;
                    if(obstacle.rods+obstacle.faces>0)lastContact=i;
                }
                if(rodContacts==0 || peak<.02f || lastContact>=120 || lastOutside>90 ||
                   (model==ContactModel.OGC && peakPenetration>.001f))
                    throw new InvalidOperationException($"{model}: {rodContacts} rod contacts, {peak:F3} m deflection, last contact {lastContact}, last outside 1 mm {lastOutside}, {peakPenetration:F4} m penetration.");
                Debug.Log($"{model}: twig deflected {peak*1000:F0} mm and stayed within 1 mm from withdrawal tick {lastOutside+1}. Peak penetration {peakPenetration*1000:F2} mm.");
            }
        }
#endif
    }
    public sealed class FigBranchInstance
        {
            const float MouseGrabForceScale=1000f;
            public FigShoot shoot;
            public Mesh mesh;
            public RodPlant rig;
            public VbdSheet solver;
            public Vector3 Center=>shoot.origin+mesh.bounds.center;
            public FigBranchInstance(FigShoot shoot){this.shoot=shoot;mesh=shoot.CreateMesh();}
            internal FigBranchInstance(FigShoot shoot,Mesh template){this.shoot=shoot;mesh=UnityEngine.Object.Instantiate(template);}
            FigBranchInstance(FigShoot shoot,FigShoot.MeshData data){this.shoot=shoot;mesh=shoot.CreateMesh(data,true);}
            public static Mesh BuildTree(int seed,FigSettings settings,List<FigBranchInstance> branches,
                Vector3? groundGradient=null,bool validate=false){
                var shoots=new List<FigShoot>();
                var prepared=new System.Collections.Concurrent.ConcurrentDictionary<int,(FigShoot.MeshData mesh,VbdSheet solver)>();
                var wood=JungleFig.Generate(seed,settings,shoots,(i,shoot)=>{
                    if(validate){
                        shoot.geometry.Validate();
                        if(shoot.geometry.issues.Count>0)throw new InvalidOperationException(string.Join(", ",shoot.geometry.issues));
                    }
                    prepared[i]=(new FigShoot.MeshData(shoot),PrepareSolver(shoot));
                },groundGradient);
                for(int i=0;i<shoots.Count;i++){
                    prepared.TryRemove(i,out var data);
                    var branch=new FigBranchInstance(shoots[i],data.mesh);
                    branches.Add(branch);
                    branch.Activate(data.solver);
                    branch.EnableRuntimeSkin();
                }
                return wood;
            }
            internal static VbdSheet PrepareSolver(FigShoot shoot,FigSolverLayout baked=null){
                var rig=new RodPlant(shoot.geometry,null,null,trackCutNeighbors:false);
                var solver=new VbdSheet(null,rig,true,obstacleContacts:false,baked:baked);
                solver.PrepareShellNormals();
                return solver;
            }
            public void Activate(){
                if(solver!=null)return;
                rig??=new RodPlant(shoot.geometry);
                // The generated crown describes the settled, unloaded configuration.
                solver=new VbdSheet(plant:rig);
                ConfigureSolver();
            }
            internal void Activate(VbdSheet prepared){
                rig=prepared.plant;
                solver=prepared;
                solver.UploadTopology();
                solver.Reset();
                ConfigureSolver();
            }
            void ConfigureSolver(){
                solver.gravityScale=0;solver.grabK=22;solver.grabLimit=2.5f;
                solver.grabD*=MouseGrabForceScale;
                solver.selfContact.enabled=false;
            }
            public const string DebugLegend="VBD: blue sleeping / cyan awake\nAVBD joint: purple sleeping / orange awake\nNatural bark: static wood";
            Material debugMaterial;
            ComputeBuffer debugVertices;
            public Material DebugMaterial(bool runtime){
                if(!debugMaterial){
                    var g=shoot.geometry;
                    var owners=new int[g.nodes.Count];
                    for(int i=0;i<g.twigClusters.Count;i++){
                        var c=g.twigClusters[i];
                        for(int n=c.firstNode;n<c.endNode;n++)owners[n]=i+1;
                    }
                    var flags=new int[rig.skinNode.Length];
                    for(int v=0;v<flags.Length;v++){
                        int r=rig.skinRod[v];
                        int node=r<0?rig.skinNode[v]:g.rods[r].b;
                        bool augmented=r>=0 && rig.parent[r]>=0 && g.rods[r].woodRigidityGain>=1f;
                        flags[v]=owners[node]*2+(augmented?1:0);
                    }
                    debugVertices=new ComputeBuffer(flags.Length,4);
                    debugVertices.SetData(flags);
                    debugMaterial=new Material(Resources.Load<Shader>("FigSolverDebug"));
                    debugMaterial.SetBuffer("_VertexFlags",debugVertices);
                    debugMaterial.SetBuffer("_ClusterState",solver.FigStates??debugVertices);
                }
                debugMaterial.SetInt("_BoughAwake",!runtime || runtimeAwake?1:0);
                debugMaterial.SetInt("_Selective",solver.FigSelectiveStep?1:0);
                return debugMaterial;
            }
            bool runtimeAwake,runtimeSkin;
            int quietTicks;
            public void EnableRuntimeSkin(){
                if(runtimeSkin)return;
                solver.BindFigMesh(mesh);
                runtimeSkin=true;
            }
            public bool RuntimeTick(){
                bool presented=false;
                if(solver.Pending){
                    if(!solver.ReadbackReady)return false;
                    FinishRuntimeTick();
                    presented=true;
                }
                if(!runtimeAwake)return presented;
                if(!runtimeSkin)EnableRuntimeSkin();
                solver.QueueFigStep();
                if(solver.Pending)return presented;
                PresentRuntimeTick(solver.FigMaxTravel);
                return true;
            }
            internal void FinishRuntimeTick(){
                if(!solver.Pending)return;
                solver.CompletePendingStep();
                PresentRuntimeTick(solver.FigMaxTravel);
            }
            void PresentRuntimeTick(float travel){
                if(solver.badVertex>=0)throw new InvalidOperationException("Invalid fig solver node "+solver.badVertex);
                solver.DrawSkinOnGpu();
                mesh.bounds=solver.SkinBounds;
                // Observe every particle, including rod directors, for half a simulated second.
                quietTicks=solver.grabFace<0 && solver.grabRod<0 && travel<.0003f && solver.FigMaxRestError<.001f?quietTicks+1:0;
                if(quietTicks>=30)runtimeAwake=false;
            }
            public int PickSurface(Ray ray,ref float nearest,out Vector3 weights){
                FinishRuntimeTick();
                weights=default;
                if(!mesh.bounds.IntersectRay(ray,out float entry) || entry>nearest)return -1;
                solver.RefreshFigPickPositions();
                return rig.PickSurface(ray,solver.positions,ref nearest,out weights);
            }
            public void ReleaseGrab(){solver.grabFace=solver.grabRod=-1;}
            public Vector3 BeginGrab(int triangle,Vector3 weights,Vector3 localHit){
                solver.RefreshFigPickPositions();
                runtimeAwake=true;quietTicks=0;
                var surface=shoot.geometry.surface[triangle];
                solver.grabFace=surface.face;solver.grabRod=surface.rod;
                float stiffness=260;
                if(surface.rod>=0){
                    var rod=shoot.geometry.rods[surface.rod];
                    Vector3 a=solver.positions[rod.a],axis=solver.positions[rod.b]-a;
                    float t=Mathf.Clamp01(Vector3.Dot(localHit-a,axis)/axis.sqrMagnitude);
                    solver.weights=new Vector3(1-t,t,0);solver.target=a+t*axis;
                    // Maintain a useful drag handle as woody cross section and mass grow.
                    stiffness=Mathf.Max(stiffness,320000*rod.radius*rod.radius);
                }else{
                    solver.weights=weights;solver.target=solver.Point(surface.face,weights);
                }
                solver.grabK=MouseGrabForceScale*stiffness;
                solver.grabLimit=1.5f*solver.grabK;
                return solver.target;
            }
            public void Sleep(){
                if(solver==null)return;
                debugVertices?.Release();debugVertices=null;
                if(debugMaterial){
                    if(Application.isPlaying)UnityEngine.Object.Destroy(debugMaterial);
                    else UnityEngine.Object.DestroyImmediate(debugMaterial);
                    debugMaterial=null;
                }
                solver.Dispose();solver=null;runtimeSkin=false;runtimeAwake=false;quietTicks=0;
                mesh.SetVertices(shoot.geometry.skin);shoot.UpdateNormals(mesh);mesh.RecalculateBounds();
            }
            public void Step(){
                solver.Step(1f/60,false);
                if(solver.badVertex>=0)throw new InvalidOperationException("Invalid fig solver node "+solver.badVertex);
                rig.UpdateSkin(solver.positions);
                mesh.vertices=rig.skin;shoot.UpdateNormals(mesh);mesh.RecalculateBounds();
            }
        }


#if UNITY_EDITOR
    static class FigReleaseCheck
    {
        [UnityEditor.MenuItem("Tools/Foliage/Check jungle fig settling")]
        static void Check()
        {
            var shoots = new List<FigShoot>();
            Mesh wood = JungleFig.Generate(2, new FigSettings(), shoots);
            var branch = new FigBranchInstance(shoots[0]);
            try {
                branch.Activate();
                branch.EnableRuntimeSkin();
                var solver = branch.solver;
                var g = branch.shoot.geometry;
                int leafRod = branch.rig.leafRod[branch.rig.leafRod.Length / 2];
                void Tick() {
                    branch.RuntimeTick();
                    branch.FinishRuntimeTick();
                }

                // Re-grab the same sleeping bough without resetting the solver.
                foreach (int rod in new[] { 4, branch.rig.parent[leafRod], -1 }) {
                    var segment = g.rods[rod < 0 ? leafRod : rod];
                    Vector3 axis = solver.rest[segment.b] - solver.rest[segment.a];
                    Vector3 direction = Vector3.Cross(axis.normalized, Vector3.right).normalized;
                    int face = g.faces.Count / 2;
                    int surface = rod < 0 ? g.surface.FindIndex(f => f.rod < 0 && f.face == face)
                                          : g.surface.FindIndex(f => f.rod == rod);
                    Vector3 center = (solver.rest[segment.a] + solver.rest[segment.b]) * .5f;
                    Vector3 start = branch.BeginGrab(surface, Vector3.one / 3, center);
                    for (int i = 1; i <= 90; i++) {
                        solver.target = start + direction * (1.5f * Mathf.Min(1, i / 30f));
                        Tick();
                    }
                    float loaded = solver.FigMaxRestError;
                    branch.ReleaseGrab();
                    int released = solver.Steps;
                    int firstWithinTolerance = -1;
                    for (int i = 0; i < 600; i++) {
                        Tick();
                        if (firstWithinTolerance < 0 && solver.FigMaxRestError < .001f)
                            firstWithinTolerance = solver.Steps - released;
                    }
                    int recovery = solver.Steps - released;
                    if (loaded < .01f || recovery <= 0 || recovery >= 600 || solver.FigMaxRestError >= .001f)
                        throw new InvalidOperationException($"Fig rod {rod}: load {loaded} m, recovery {recovery} ticks, final error {solver.FigMaxRestError} m.");
                    Debug.Log($"Fig {(rod < 0 ? "leaf" : $"rod {rod}")} entered 1 mm at tick {firstWithinTolerance}, settled in {recovery} ticks, maximum rest error {1000 * solver.FigMaxRestError:F2} mm.");
                }
            } finally {
                branch.Sleep();
                UnityEngine.Object.DestroyImmediate(branch.mesh);
                UnityEngine.Object.DestroyImmediate(wood);
            }
        }
    }
#endif
}
