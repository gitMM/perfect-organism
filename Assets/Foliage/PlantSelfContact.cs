using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Foliage
{
    // Each row is a vertex/face or edge/edge witness. Signed interpolation
    // weights distribute the same normal load to both sides of the contact.
    public sealed class PlantSelfContact : IDisposable
    {
        struct Row
        {
            public int a,b,c,d;
            public Vector4 weights;
            public Vector3 normal;
            public float distance;
            public int Node(int i)=>i==0?a:i==1?b:i==2?c:d;
            public Vector3 Delta(Vector3[] x)=>weights.x*x[a]+weights.y*x[b]+weights.z*x[c]+weights.w*x[d];
        }
        struct Edge
        {
            public int start,end;
            public Vector3 axis;
            public float squared;
        }
        struct FeatureGeometry
        {
            public Edge first,second,third;
            public Vector3 normal;
            public float normalSquared,speed;
            public Vector3 center;
            public Edge GetEdge(int i)=>i==0?first:i==1?second:third;
        }
        const int MaxPairBatch=4096, RowsPerPair=15;
        struct PairResult
        {
            public float squared,low,high;
            public Vector3 axis;
            public int count,planeCount;
        }
        NativeArray<FeatureGeometry> geometry;
        NativeArray<PlantContact.Feature> nativeFeatures;
        NativeArray<Vector3> nativePositions;
        NativeArray<Vector2Int> pairs;
        NativeArray<Row> candidates;
        NativeArray<PairResult> results;
        NativeArray<VbdSheet.Sample> nativeSamples;
        struct NodePlane { public int node; public Vector4 plane; }
        NativeArray<NodePlane> pairPlanes;
        struct Box { public Vector3 min,max; }
        readonly PlantContact.Feature[] features;
        NativeArray<Box> boxes;
        const int LeafSize=8;
        NativeArray<int> order,clusterStarts;
        NativeArray<Box> tree;
        bool ordered;
        readonly bool useTree;
        float uniformTravel;
        NativeArray<Vector2Int> overlaps;
        NativeArray<int> overlapCount;
        readonly HashSet<(int,int)> freshSeams=new();
        readonly List<(int,int)> clearedSeams=new();
        readonly int[] offsets,cursors;
        readonly List<Row> rows=new();
        [Flags] enum ContactPair { Other=0, Branches=1, Blades=2, BetweenPlants=4 }
        readonly List<ContactPair> pairKinds=new();
        readonly int[] featureParts,featurePlants;
        public float betweenPlantPenetration {get;private set;}
        public int betweenPlantContacts {get;private set;}
        readonly int rodCount;
        public int bladeContacts {get;private set;}
        public int branchContacts {get;private set;}
        readonly Dictionary<(int,int,int,int),int> witnesses=new();
        readonly ComputeShader gpu;
        readonly int relax,measure,localRelax;
        readonly int predict;
        NativeArray<NodePlane> gatheredPlanes;
        NativeArray<Vector4> flatPlanes;
        NativeArray<int> planeOffsets,planeCursors,gatheredCount;
        ComputeBuffer planeBuffer,planeOffsetBuffer;
        public bool dat=true;
        public bool safeguarding=>enabled && dat && features.Length>0;
        ComputeBuffer rowBuffer,indexBuffer,offsetBuffer;
        int[] indices=Array.Empty<int>();
        public bool enabled=true;
        public int active {get;private set;}
        public float penetration {get;private set;}
        public readonly List<Vector3> points=new();
        const float Envelope=.002f, Search=.012f;

        internal PlantSelfContact(PlantContact contact,int nodes,ComputeShader gpu,int predict,int relax,int measure,int localRelax,bool useTree)
        {
            this.useTree=useTree;this.localRelax=localRelax;this.gpu=gpu;this.relax=relax;this.measure=measure;
            this.predict=predict;
            features=contact?.features??Array.Empty<PlantContact.Feature>();
            featureParts=contact?.featureParts??Array.Empty<int>();
            featurePlants=contact?.featurePlants??Array.Empty<int>();
            rodCount=contact?.rodCount??0;
            if(contact!=null)
                foreach(var twin in contact.cutTwins)
                for(int a=0;a<features.Length;a++)
                {
                    if(!Contains(features[a],twin.x))continue;
                    for(int b=0;b<features.Length;b++)if(a!=b && Contains(features[b],twin.y))
                        freshSeams.Add((Math.Min(a,b),Math.Max(a,b)));
                }
            if(features.Length>0)
            {
                geometry=new NativeArray<FeatureGeometry>(features.Length,Allocator.Persistent);
                nativeFeatures=new NativeArray<PlantContact.Feature>(features,Allocator.Persistent);
                nativePositions=new NativeArray<Vector3>(nodes,Allocator.Persistent);
                nativeSamples=new NativeArray<VbdSheet.Sample>(nodes,Allocator.Persistent);
                int batch=Math.Min(MaxPairBatch,Math.Max(32,features.Length*4));
                pairPlanes=new NativeArray<NodePlane>(6*batch,Allocator.Persistent);
                pairs=new NativeArray<Vector2Int>(batch,Allocator.Persistent);
                candidates=new NativeArray<Row>(batch*RowsPerPair,Allocator.Persistent);
                results=new NativeArray<PairResult>(batch,Allocator.Persistent);
            }
            boxes=new NativeArray<Box>(features.Length,Allocator.Persistent);
            order=new NativeArray<int>(features.Length,Allocator.Persistent);
            overlaps=new NativeArray<Vector2Int>(Math.Max(16,features.Length*8),Allocator.Persistent);
            overlapCount=new NativeArray<int>(1,Allocator.Persistent);
            int clusters=(features.Length+LeafSize-1)/LeafSize;
            for(int i=0;i<order.Length;i++)order[i]=i;
            clusterStarts=new NativeArray<int>(clusters+1,Allocator.Persistent);
            tree=new NativeArray<Box>(2*Mathf.NextPowerOfTwo(Math.Max(1,clusters)),Allocator.Persistent);
            offsets=new int[nodes+1];cursors=new int[nodes];
            planeOffsets=new NativeArray<int>(nodes+1,Allocator.Persistent);
            planeCursors=new NativeArray<int>(nodes,Allocator.Persistent);
            gatheredCount=new NativeArray<int>(1,Allocator.Persistent);
            BindPlanes(ref planeBuffer,"DatPlanes",1,16);
            BindPlanes(ref planeOffsetBuffer,"DatOffsets",nodes+1,4);
            Bind(ref rowBuffer,"SelfRows",1,48);Bind(ref indexBuffer,"SelfIndices",1,4);
            Bind(ref offsetBuffer,"SelfOffsets",offsets.Length,4);
        }
        void BindPlanes(ref ComputeBuffer buffer,string name,int count,int stride)
        {
            if(buffer!=null && buffer.count>=count)return;
            buffer?.Release();buffer=new ComputeBuffer(Mathf.NextPowerOfTwo(count),stride);
            gpu.SetBuffer(predict,name,buffer);gpu.SetBuffer(relax,name,buffer);if(localRelax>=0)gpu.SetBuffer(localRelax,name,buffer);
        }
        void Bind(ref ComputeBuffer buffer,string name,int count,int stride)
        {
            if(buffer!=null && buffer.count>=count)return;
            buffer?.Release();
            buffer=new ComputeBuffer(Mathf.NextPowerOfTwo(count),stride);
            gpu.SetBuffer(relax,name,buffer);if(localRelax>=0)gpu.SetBuffer(localRelax,name,buffer);gpu.SetBuffer(measure,name,buffer);
        }
        static int Node(PlantContact.Feature f,int i)=>i==0?f.a:i==1?f.b:f.c;
        static bool Contains(PlantContact.Feature f,int v)=>v>=0 && (f.a==v || f.b==v || f.c==v);
        internal void RegisterSplitFaces(PlantCut cut,PlantGeometry original)
        {
            if(cut.faceSource.Count==0)return;
            // Retriangulation also creates close faces which do not share a twin.
            // Give descendants of formerly adjacent faces the same seam clearance.
            var seamNodes=new HashSet<int>();
            foreach(var twin in cut.seam){seamNodes.Add(twin.x);seamNodes.Add(twin.y);}
            for(int i=rodCount;i<features.Length;i++)
            {
                var a=features[i];
                if(!seamNodes.Contains(a.a) && !seamNodes.Contains(a.b) && !seamNodes.Contains(a.c))continue;
                var oldA=original.faces[cut.faceSource[i-rodCount]];
                for(int j=rodCount;j<features.Length;j++)
                {
                    if(i==j || featureParts[i]!=featureParts[j])continue;
                    var b=features[j];
                    if(Contains(a,b.a)||Contains(a,b.b)||Contains(a,b.c))continue;
                    var oldB=original.faces[cut.faceSource[j-rodCount]];
                    bool Shared(int v)=>v==oldA.a || v==oldA.b || v==oldA.c;
                    if(Shared(oldB.a)||Shared(oldB.b)||Shared(oldB.c))
                        freshSeams.Add((Math.Min(i,j),Math.Max(i,j)));
                }
            }
        }
        public void Clear()
        {
            ClearMeasurements();
            rows.Clear();
            pairKinds.Clear();
        }
        internal void ClearMeasurements()
        {
            active=0;betweenPlantContacts=0;betweenPlantPenetration=0;branchContacts=0;bladeContacts=0;penetration=0;points.Clear();
        }
        internal bool Prepare(Vector3[] x,VbdSheet.Sample[] samples,float dt)
        {
            rows.Clear();pairKinds.Clear();witnesses.Clear();
            gpu.SetInt("UseDat",safeguarding?1:0);
            if(safeguarding)
            {
                if(!useTree)
                {
                    float speed2=0;
                    foreach(var sample in samples)speed2=Mathf.Max(speed2,sample.velocity.sqrMagnitude);
                    uniformTravel=Mathf.Min(.15f,Search+dt*Mathf.Sqrt(speed2));
                }
                gpu.SetFloat("UniformTravel",uniformTravel);
                gpu.SetInt("LocalTravel",useTree?1:0);
                gatheredCount[0]=0;
                gpu.SetFloat("DatTravelBase",Search);
            }
            if(enabled && features.Length>0)
            {
                nativePositions.CopyFrom(x);
                nativeSamples.CopyFrom(samples);
                CollectOverlaps(dt);
                // Coincident material at a new seam shares a collision skin until
                // the two sides have cleared it once. Recontact is then ordinary contact.
                clearedSeams.Clear();
                foreach(var pair in freshSeams)
                {
                    var a=boxes[pair.Item1];var b=boxes[pair.Item2];
                    if(a.min.x>b.max.x || b.min.x>a.max.x || a.min.y>b.max.y || b.min.y>a.max.y ||
                        a.min.z>b.max.z || b.min.z>a.max.z)clearedSeams.Add(pair);
                }
                foreach(var pair in clearedSeams)freshSeams.Remove(pair);
                if(safeguarding)
                {
                    // Each candidate contributes at most three planes on each side.
                    int capacity=Math.Max(1,checked(6*overlapCount[0]));
                    if(!gatheredPlanes.IsCreated || gatheredPlanes.Length<capacity)
                    {
                        if(gatheredPlanes.IsCreated)gatheredPlanes.Dispose();
                        gatheredPlanes=new NativeArray<NodePlane>(Mathf.NextPowerOfTwo(capacity),Allocator.Persistent,NativeArrayOptions.UninitializedMemory);
                    }
                }
                for(int start=0;start<overlapCount[0];start+=pairs.Length)
                {
                    int batch=Math.Min(pairs.Length,overlapCount[0]-start);
                    NativeArray<Vector2Int>.Copy(overlaps,start,pairs,0,batch);
                    ProcessPairs(dt,batch);
                }
            }
            gpu.SetInt("HasSelfContact",rows.Count>0?1:0);
            if(safeguarding)
            {
                int planes=gatheredCount[0];
                if(!flatPlanes.IsCreated || flatPlanes.Length<planes)
                {
                    if(flatPlanes.IsCreated)flatPlanes.Dispose();
                    flatPlanes=new NativeArray<Vector4>(Mathf.NextPowerOfTwo(Math.Max(1,planes)),Allocator.Persistent,NativeArrayOptions.UninitializedMemory);
                }
                new PackPlanes {source=gatheredPlanes,packed=flatPlanes,offsets=planeOffsets,cursors=planeCursors,count=planes}.Run();
                planeOffsetBuffer.SetData(planeOffsets);
                if(planes>0)
                {
                    BindPlanes(ref planeBuffer,"DatPlanes",planes,16);
                    planeBuffer.SetData(flatPlanes,0,0,planes);
                }
            }
            if(rows.Count==0)return false;
            Array.Clear(offsets,0,offsets.Length);
            foreach(var row in rows)for(int i=0;i<4;i++)if(row.weights[i]!=0)offsets[row.Node(i)+1]++;
            for(int i=1;i<offsets.Length;i++)offsets[i]+=offsets[i-1];
            Array.Copy(offsets,cursors,cursors.Length);
            int count=offsets[offsets.Length-1];
            if(indices.Length<count)indices=new int[Mathf.NextPowerOfTwo(count)];
            for(int j=0;j<rows.Count;j++)
            {
                var row=rows[j];
                for(int i=0;i<4;i++)if(row.weights[i]!=0)indices[cursors[row.Node(i)]++]=j;
            }
            Bind(ref rowBuffer,"SelfRows",rows.Count,48);Bind(ref indexBuffer,"SelfIndices",count,4);
            rowBuffer.SetData(rows);indexBuffer.SetData(indices,0,0,count);offsetBuffer.SetData(offsets);
            return true;
        }
        static uint MortonBits(uint x)
        {
            x=(x|(x<<16))&0x030000ff;
            x=(x|(x<<8))&0x0300f00f;
            x=(x|(x<<4))&0x030c30c3;
            return (x|(x<<2))&0x09249249;
        }
        void InitializeOrder()
        {
            var bounds=boxes[0];
            for(int i=1;i<boxes.Length;i++)bounds=Union(bounds,boxes[i]);
            var span=bounds.max-bounds.min;
            var scale=new Vector3(511.5f/Mathf.Max(span.x,.001f),511.5f/Mathf.Max(span.y,.001f),511.5f/Mathf.Max(span.z,.001f));
            var keys=new uint[boxes.Length];var ids=new int[boxes.Length];
            for(int i=0;i<ids.Length;i++)
            {
                var point=Vector3.Scale(boxes[i].min+boxes[i].max-2*bounds.min,scale);
                keys[i]=MortonBits((uint)point.x)|(MortonBits((uint)point.y)<<1)|(MortonBits((uint)point.z)<<2);
                ids[i]=i;
            }
            Array.Sort(keys,ids);order.CopyFrom(ids);ordered=true;
        }
        static Box Union(Box a,Box b)=>new Box{min=Vector3.Min(a.min,b.min),max=Vector3.Max(a.max,b.max)};
        static bool Intersects(Box a,Box b)=>a.min.x<=b.max.x && b.min.x<=a.max.x && a.min.y<=b.max.y && b.min.y<=a.max.y && a.min.z<=b.max.z && b.min.z<=a.max.z;

        void CollectOverlaps(float dt)
        {
            new FeatureBounds{x=nativePositions,samples=nativeSamples,features=nativeFeatures,geometry=geometry,
                boxes=boxes,dat=safeguarding,dt=dt,uniformTravel=useTree?0:uniformTravel}.Schedule(features.Length,64).Complete();
            if(!useTree)
            {
                var sweep=new SweepBounds{boxes=boxes,features=nativeFeatures,order=order,overlaps=overlaps,overlapCount=overlapCount};
                sweep.Run();
                if(overlapCount[0]>overlaps.Length)
                {
                    overlaps.Dispose();
                    overlaps=new NativeArray<Vector2Int>(Mathf.NextPowerOfTwo(overlapCount[0]),Allocator.Persistent);
                    sweep.overlaps=overlaps;sweep.Run();
                }
                return;
            }
            if(!ordered)InitializeOrder();
            new RefitTree{tree=tree,boxes=boxes,order=order}.Run();
            int clusters=clusterStarts.Length-1;
            var query=new QueryTree{boxes=boxes,features=nativeFeatures,order=order,tree=tree,starts=clusterStarts,output=overlaps};
            query.Schedule(clusters,8).Complete();
            new PrefixClusters{starts=clusterStarts}.Run();
            int count=clusterStarts[clusters];
            overlapCount[0]=count;
            if(count>overlaps.Length)
            {
                overlaps.Dispose();
                overlaps=new NativeArray<Vector2Int>(Mathf.NextPowerOfTwo(count),Allocator.Persistent,NativeArrayOptions.UninitializedMemory);
            }
            query.output=overlaps;
            query.emit=true;
            query.Schedule(clusters,8).Complete();
        }

        [BurstCompile]
        struct PrefixClusters : IJob
        {
            public NativeArray<int> starts;
            public void Execute()
            {
                int total=0;
                for(int i=0;i<starts.Length-1;i++){int n=starts[i];starts[i]=total;total+=n;}
                starts[starts.Length-1]=total;
            }
        }

        [BurstCompile]
        struct RefitTree : IJob
        {
            public NativeArray<Box> tree;
            [ReadOnly] public NativeArray<Box> boxes;
            [ReadOnly] public NativeArray<int> order;
            public void Execute()
            {
                int leaves=tree.Length/2;
                for(int leaf=0;leaf<leaves;leaf++)
                {
                    var box=new Box{min=Vector3.positiveInfinity,max=Vector3.negativeInfinity};
                    int end=Math.Min(order.Length,(leaf+1)*LeafSize);
                    for(int i=leaf*LeafSize;i<end;i++)box=Union(box,boxes[order[i]]);
                    tree[leaves+leaf]=box;
                }
                for(int i=leaves-1;i>0;i--)tree[i]=Union(tree[i*2],tree[i*2+1]);
            }
        }

        [BurstCompile]
        struct QueryTree : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Box> tree,boxes;
            [ReadOnly] public NativeArray<int> order;
            [ReadOnly] public NativeArray<PlantContact.Feature> features;
            public NativeArray<int> starts;
            [NativeDisableParallelForRestriction] public NativeArray<Vector2Int> output;
            public bool emit;
            public void Execute(int cluster)
            {
                int leaves=tree.Length/2,leaf=leaves+cluster,node=1;
                var box=tree[leaf];
                int first=cluster*LeafSize,last=Math.Min(order.Length,first+LeafSize);
                int next=emit?starts[cluster]:0;
                while(true)
                {
                    if(Intersects(box,tree[node]))
                    {
                        if(node<leaves){node*=2;continue;}
                        if(node>=leaf)
                        {
                            int begin=(node-leaves)*LeafSize,end=Math.Min(order.Length,begin+LeafSize);
                            for(int i=first;i<last;i++)
                            {
                                int id=order[i];var a=features[id];var ab=boxes[id];
                                for(int j=Math.Max(i+1,begin);j<end;j++)
                                {
                                    int other=order[j];var bb=boxes[other];
                                    if(!Intersects(ab,bb))continue;
                                    var b=features[other];
                                    if(Contains(a,b.a)||Contains(a,b.b)||Contains(a,b.c))continue;
                                    if(emit)output[next]=ab.min.x<=bb.min.x?new Vector2Int(id,other):new Vector2Int(other,id);
                                    next++;
                                }
                            }
                        }
                    }
                    // Ascend past completed right subtrees and visit the next sibling.
                    while((node&1)!=0 && node>1)node/=2;
                    if(node==1)break;
                    node++;
                }
                if(!emit)starts[cluster]=next;
            }
        }

        [BurstCompile]
        struct SweepBounds : IJob
        {
            [ReadOnly] public NativeArray<Box> boxes;
            [ReadOnly] public NativeArray<PlantContact.Feature> features;
            public NativeArray<int> order,overlapCount;
            public NativeArray<Vector2Int> overlaps;
            public void Execute()
            {
                // Reuse the previous tick's sweep order. Bounds move coherently.
                for(int i=1;i<order.Length;i++)
                {
                    int id=order[i],j=i;
                    while(j>0 && boxes[order[j-1]].min.x>boxes[id].min.x)
                    {
                        order[j]=order[j-1];
                        j--;
                    }
                    order[j]=id;
                }
                int count=0;
                for(int i=0;i<order.Length;i++)
                {
                    int id=order[i];var box=boxes[id];var a=features[id];
                    for(int j=i+1;j<order.Length;j++)
                    {
                        int other=order[j];var b=boxes[other];
                        if(b.min.x>box.max.x)break;
                        if(b.min.y>box.max.y || box.min.y>b.max.y || b.min.z>box.max.z || box.min.z>b.max.z)continue;
                        var f=features[other];
                        if(Contains(a,f.a)||Contains(a,f.b)||Contains(a,f.c))continue;
                        if(count<overlaps.Length)overlaps[count]=new Vector2Int(id,other);
                        count++;
                    }
                }
                overlapCount[0]=count;
            }
        }
        [BurstCompile(FloatPrecision.High,FloatMode.Strict)]
        struct FeatureBounds : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> x;
            [ReadOnly] public NativeArray<VbdSheet.Sample> samples;
            [ReadOnly] public NativeArray<PlantContact.Feature> features;
            public NativeArray<FeatureGeometry> geometry;
            public NativeArray<Box> boxes;
            public float dt;
            public bool dat;
            public float uniformTravel;
            public void Execute(int id)
            {
                    var f=features[id];Vector3 lo=x[f.a],hi=lo;
                    FeatureGeometry geom=default;
                    geom.first=MakeEdge(x,f.a,f.b);
                    if(f.c>=0)
                    {
                        geom.second=MakeEdge(x,f.b,f.c);
                        geom.third=MakeEdge(x,f.c,f.a);
                        geom.normal=Vector3.Cross(geom.first.axis,-geom.third.axis);
                        geom.normalSquared=geom.normal.sqrMagnitude;
                    }
                    geom.center=f.c<0?(x[f.a]+x[f.b])*.5f:(x[f.a]+x[f.b]+x[f.c])/3;
                    for(int i=0;i<(f.c<0?2:3);i++)
                    {
                        int v=Node(f,i);Vector3 end=x[v]+samples[v].velocity*dt;
                        lo=Vector3.Min(lo,Vector3.Min(x[v],end));hi=Vector3.Max(hi,Vector3.Max(x[v],end));
                    }
                    float speed2=Mathf.Max(samples[f.a].velocity.sqrMagnitude,samples[f.b].velocity.sqrMagnitude);
                    if(f.c>=0)speed2=Mathf.Max(speed2,samples[f.c].velocity.sqrMagnitude);
                    geom.speed=Mathf.Sqrt(speed2);
                    geometry[id]=geom;
                    var pad=Vector3.one*(f.skin+(dat?(uniformTravel>0?uniformTravel:Mathf.Min(.15f,Search+dt*geom.speed)):Search*.5f));
                    boxes[id]=new Box{min=lo-pad,max=hi+pad};
            }
        }
        static Edge MakeEdge(NativeArray<Vector3> x,int start,int end)
        {
            Vector3 axis=x[end]-x[start];
            return new Edge{start=start,end=end,axis=axis,squared=axis.sqrMagnitude};
        }
        [BurstCompile(FloatPrecision.High,FloatMode.Strict)]
        struct ClosestPairs : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> x;
            [ReadOnly] public NativeArray<PlantContact.Feature> features;
            [ReadOnly] public NativeArray<FeatureGeometry> geometry;
            [ReadOnly] public NativeArray<Vector2Int> pairs;
            public NativeArray<PairResult> results;
            // Each pair owns 15 adjacent rows, one per possible witness.
            [NativeDisableParallelForRestriction] public NativeArray<Row> rows;
            [ReadOnly] public NativeArray<VbdSheet.Sample> samples;
            [NativeDisableParallelForRestriction] public NativeArray<NodePlane> planes;
            public float dt;
            public bool safeguard,useTree;
            public float uniformTravel;
            public void Execute(int index)
            {
                var pair=pairs[index];
                int start=index*RowsPerPair;
                var a=features[pair.x];var b=features[pair.y];
                var ga=geometry[pair.x];var gb=geometry[pair.y];
                // A separating plane certifies a bound for every witness, including
                // interpolated velocities. Only unresolved pairs need exact closest points.
                var diff=ga.center-gb.center;
                float len=diff.magnitude;
                PairResult result=default;
                bool separated=false;
                if(useTree && len>1e-8f)
                {
                    var n=diff/len;
                    float lo=Mathf.Min(Vector3.Dot(n,x[a.a]),Vector3.Dot(n,x[a.b]));
                    float hi=Mathf.Max(Vector3.Dot(n,x[b.a]),Vector3.Dot(n,x[b.b]));
                    if(a.c>=0)lo=Mathf.Min(lo,Vector3.Dot(n,x[a.c]));
                    if(b.c>=0)hi=Mathf.Max(hi,Vector3.Dot(n,x[b.c]));
                    float gap=lo-hi;
                    float maxTravel=Mathf.Min(.15f,Search+dt*Mathf.Max(ga.speed,gb.speed));
                    separated=gap>a.skin+b.skin+Mathf.Max(Envelope+Search+dt*(ga.speed+gb.speed),3.466f*maxTravel);
                    if(separated)result=new PairResult{squared=gap*gap,axis=n,low=lo,high=hi};
                }
                if(!separated)result=Find(x,features,geometry,rows,start,pair.x,pair.y,safeguard);
                if(safeguard)result.planeCount=WritePlanes(x,samples,planes,index*6,a,b,result,dt,uniformTravel);
                int kept=0;
                for(int i=0;i<result.count;i++)
                {
                    var row=rows[start+i];
                    if(PrepareRow(ref row,x,samples,dt,a,b))rows[start+kept++]=row;
                }
                result.count=kept;
                results[index]=result;
            }
        }
        static PairResult Find(NativeArray<Vector3> x,NativeArray<PlantContact.Feature> features,
            NativeArray<FeatureGeometry> geometry,NativeArray<Row> candidates,int start,int first,int second,bool safeguard)
        {
            var a=features[first];var b=features[second];
            float sq=float.PositiveInfinity;int count=0,nearest=0;
            void Consider(Row row)
            {
                Vector4 w=row.weights;
                Vector3 delta=w.x*x[row.a]+w.y*x[row.b]+w.z*x[row.c]+w.w*x[row.d];
                float d=delta.sqrMagnitude;
                if(d<sq){sq=d;nearest=count;}
                row.normal=delta;candidates[start+count++]=row;
            }
            void Project(int v,PlantContact.Feature f,int id)
            {
                var geom=geometry[id];
                Vector3 ab=geom.first.axis,ac=-geom.third.axis,p=x[v]-x[f.a],n=geom.normal;
                float nn=geom.normalSquared;if(nn<1e-18f)return;
                float u=Vector3.Dot(Vector3.Cross(p,ac),n)/nn,w=Vector3.Dot(Vector3.Cross(ab,p),n)/nn;
                if(u<0 || w<0 || u+w>1)return;
                Consider(new Row{a=v,b=f.a,c=f.b,d=f.c,weights=new Vector4(1,u+w-1,-u,-w)});
            }
            int na=a.c<0?2:3,nb=b.c<0?2:3;
            if(nb==3)for(int i=0;i<na;i++)Project(Node(a,i),b,second);
            if(na==3)for(int i=0;i<nb;i++)Project(Node(b,i),a,first);
            for(int i=0;i<(na==2?1:3);i++)
            for(int j=0;j<(nb==2?1:3);j++)
            {
                var left=geometry[first].GetEdge(i);var right=geometry[second].GetEdge(j);
                int p=left.start,q=left.end,r=right.start,s=right.end;
                Vector3 e=left.axis,f=right.axis,d=x[p]-x[r];
                float ee=left.squared,ff=right.squared,ef=Vector3.Dot(e,f);
                float ed=Vector3.Dot(e,d),fd=Vector3.Dot(f,d),den=ee*ff-ef*ef;
                float u=den>1e-8f*ee*ff?Mathf.Clamp01((ef*fd-ff*ed)/den):0;
                float v=Mathf.Clamp01((fd+ef*u)/Mathf.Max(ff,1e-16f));
                u=Mathf.Clamp01((ef*v-ed)/Mathf.Max(ee,1e-16f));
                Consider(new Row{a=p,b=q,c=r,d=s,weights=new Vector4(1-u,u,v-1,-v)});
            }
            float band=Mathf.Sqrt(sq)+Envelope;
            Vector3 axis=candidates[start+nearest].normal,plane=default;
            float low=0,high=0;
            if(safeguard && sq>1e-16f)
            {
                // Orient the closest-feature axis from B to A, including
                // vertex/face witnesses generated in the reverse direction.
                var row=candidates[start+nearest];
                var n=axis/Mathf.Sqrt(sq);
                if(!Contains(a,row.a))n=-n;
                void Support(Vector3 direction,out float lo,out float hi)
                {
                    lo=float.PositiveInfinity;hi=float.NegativeInfinity;
                    for(int i=0;i<na;i++)lo=Mathf.Min(lo,Vector3.Dot(direction,x[Node(a,i)]));
                    for(int i=0;i<nb;i++)hi=Mathf.Max(hi,Vector3.Dot(direction,x[Node(b,i)]));
                }
                Support(n,out low,out high);
                // A near-parallel edge tie can produce an inaccurate witness
                // direction. Only install a plane verified on the whole hull.
                if(low-high<Mathf.Sqrt(sq)*.99f)
                {
                    for(int j=0;j<count;j++)
                    {
                        Vector3 candidate=candidates[start+j].normal;
                        float len=candidate.magnitude;if(len<1e-9f)continue;
                        candidate*= (Contains(a,candidates[start+j].a)?1f:-1f)/len;
                        Support(candidate,out float lo,out float hi);
                        if(lo-hi>low-high){low=lo;high=hi;n=candidate;}
                    }
                }
                plane=n;
            }
            int kept=0;
            for(int i=0;i<count;i++)
            {
                Vector3 n=candidates[start+i].normal;float length2=n.sqrMagnitude;
                float alignment=Vector3.Dot(n,axis);
                if(length2<=sq+1e-10f || (length2<=band*band && alignment>0 && alignment*alignment>.99f*length2*sq))
                    candidates[start+kept++]=candidates[start+i];
            }
            return new PairResult{squared=sq,axis=plane,low=low,high=high,count=kept};
        }
        void ProcessPairs(float dt,int count)
        {
            var job=new ClosestPairs{x=nativePositions,features=nativeFeatures,geometry=geometry,
                pairs=pairs,results=results,rows=candidates,samples=nativeSamples,planes=pairPlanes,
                dt=dt,safeguard=safeguarding,useTree=useTree,uniformTravel=useTree?0:uniformTravel};
            const int batchSize=32;
            if(count<batchSize)
            {
                for(int i=0;i<count;i++)job.Execute(i);
            }
            else job.Schedule(count,batchSize).Complete();
            // Apply results in sweep order so shared witnesses keep their original owner.
            for(int i=0;i<count;i++)
            {
                var pair=pairs[i];
                var result=results[i];
                if(freshSeams.Count>0)
                {
                    var seam=(Math.Min(pair.x,pair.y),Math.Max(pair.x,pair.y));
                    if(freshSeams.Contains(seam))
                    {
                        if(Mathf.Sqrt(result.squared)<=features[pair.x].skin+features[pair.y].skin+Envelope)
                        {
                            result.planeCount=0;
                            results[i]=result;
                            continue;
                        }
                        freshSeams.Remove(seam);
                    }
                }
                if(result.count==0)continue;
                var key=(Math.Min(pair.x,pair.y),Math.Max(pair.x,pair.y));
                var kind=ContactPair.Other;
                if(featureParts[key.Item1]!=featureParts[key.Item2])
                {
                    if(key.Item2<rodCount)kind=ContactPair.Branches;
                    else if(key.Item1>=rodCount)kind=ContactPair.Blades;
                }
                if(featurePlants[pair.x]!=featurePlants[pair.y])kind|=ContactPair.BetweenPlants;
                for(int j=0;j<result.count;j++)Register(candidates[i*RowsPerPair+j],kind);
            }
            if(safeguarding)new GatherPlanes {source=pairPlanes,results=results,destination=gatheredPlanes,
                written=gatheredCount,count=count}.Run();
        }

        [BurstCompile]
        struct GatherPlanes : IJob
        {
            [ReadOnly] public NativeArray<NodePlane> source;
            [ReadOnly] public NativeArray<PairResult> results;
            public NativeArray<NodePlane> destination;
            public NativeArray<int> written;
            public int count;
            public void Execute()
            {
                int next=written[0];
                for(int i=0;i<count;i++)
                    for(int j=0;j<results[i].planeCount;j++)destination[next++]=source[6*i+j];
                written[0]=next;
            }
        }

        [BurstCompile]
        struct PackPlanes : IJob
        {
            [ReadOnly] public NativeArray<NodePlane> source;
            public NativeArray<Vector4> packed;
            public NativeArray<int> offsets,cursors;
            public int count;
            public void Execute()
            {
                for(int v=0;v<offsets.Length;v++)offsets[v]=0;
                for(int i=0;i<count;i++)offsets[source[i].node+1]++;
                for(int v=0;v<cursors.Length;v++)
                {
                    cursors[v]=offsets[v];
                    offsets[v+1]+=offsets[v];
                }
                for(int i=0;i<count;i++)
                {
                    var entry=source[i];
                    int cursor=cursors[entry.node];
                    packed[cursor]=entry.plane;
                    cursors[entry.node]=cursor+1;
                }
            }
        }
        static int WritePlanes(NativeArray<Vector3> x,NativeArray<VbdSheet.Sample> samples,NativeArray<NodePlane> planes,int start,
            PlantContact.Feature a,PlantContact.Feature b,PairResult result,float dt,float uniformTravel)
        {
            int written=0;
            float low=result.low,high=result.high;
            Vector3 n=result.axis;
            int na=a.c<0?2:3,nb=b.c<0?2:3;
            if(low>high)
            {
                // Reserve both skins when initially clear. In an existing
                // skin overlap keep the center features apart during recovery.
                float skins=a.skin+b.skin;
                float skinScale=low-high>=skins?1:(low-high)*.99f/skins;
                float middle=.5f*(low-a.skin*skinScale+high+b.skin*skinScale);
                void AddPlane(int v,Vector3 inward,float slack)
                {
                    if(slack<1.733f*(uniformTravel>0?uniformTravel:Mathf.Min(.15f,Search+dt*samples[v].velocity.magnitude)))
                        planes[start+written++]=new NodePlane{node=v,plane=new Vector4(inward.x,inward.y,inward.z,Mathf.Max(0,slack)*.999f)};
                }
                for(int i=0;i<na;i++)
                {
                    int v=Node(a,i);AddPlane(v,-n,Vector3.Dot(n,x[v])-middle-a.skin*skinScale);
                }
                for(int i=0;i<nb;i++)
                {
                    int v=Node(b,i);AddPlane(v,n,middle-b.skin*skinScale-Vector3.Dot(n,x[v]));
                }
            }
            return written;
        }
        static bool PrepareRow(ref Row best,NativeArray<Vector3> x,NativeArray<VbdSheet.Sample> samples,
            float dt,PlantContact.Feature a,PlantContact.Feature b)
        {
            float dist=best.normal.magnitude;
            best.distance=a.skin+b.skin+Envelope;
            Vector3 velocity=Vector3.zero;
            for(int i=0;i<4;i++)velocity+=samples[best.Node(i)].velocity*best.weights[i];
            if(dist>best.distance+Search+velocity.magnitude*dt)return false;
            if(dist>1e-7f)best.normal/=dist;
            else if(velocity.sqrMagnitude>1e-12f)best.normal=-velocity.normalized;
            else
            {
                var tri=a.c>=0?a:b;
                Vector3 n=tri.c>=0?Vector3.Cross(x[tri.b]-x[tri.a],x[tri.c]-x[tri.a]):
                    Vector3.Cross(x[a.b]-x[a.a],x[b.b]-x[b.a]);
                best.normal=n.sqrMagnitude>1e-16f?n.normalized:Vector3.up;
            }
            return true;
        }
        void Register(Row best,ContactPair kind)
        {
            // A shared witness can be returned by several incident faces.
            // Opposite signed groups identify the same pair in either order.
            int p0=0,p1=0,n0=0,n1=0,n2=0;
            for(int i=0;i<4;i++)
            {
                int id=best.Node(i)+1;
                if(best.weights[i]>0){if(p0==0)p0=id;else p1=id;}
                if(best.weights[i]<0){if(n0==0)n0=id;else if(n1==0)n1=id;else n2=id;}
            }
            if(p1>0 && p0>p1)(p0,p1)=(p1,p0);
            if(n1>0 && n0>n1)(n0,n1)=(n1,n0);
            if(n2>0 && n1>n2)(n1,n2)=(n2,n1);
            if(n1>0 && n0>n1)(n0,n1)=(n1,n0);
            if(n2==0 && p0>n0){(p0,n0)=(n0,p0);(p1,n1)=(n1,p1);}
            var key=n2>0?(p0,-n0,-n1,-n2):(p0,p1,-n0,-n1);
            if(witnesses.TryGetValue(key,out int existing))
            {
                if(best.distance>rows[existing].distance){rows[existing]=best;pairKinds[existing]=kind;}
            }
            else {witnesses.Add(key,rows.Count);rows.Add(best);pairKinds.Add(kind);}
        }
        internal void Measure(Vector3[] x)
        {
            ClearMeasurements();
            for(int k=0;k<rows.Count;k++)
            {
                var row=rows[k];
                float gap=Vector3.Dot(row.Delta(x),row.normal)-row.distance;
                if(gap>=0)continue;
                if((pairKinds[k]&ContactPair.BetweenPlants)!=0)
                {
                    betweenPlantContacts++;
                    betweenPlantPenetration=Mathf.Max(betweenPlantPenetration,-gap-Envelope);
                }
                if((pairKinds[k]&ContactPair.Branches)!=0)branchContacts++;
                if((pairKinds[k]&ContactPair.Blades)!=0)bladeContacts++;
                active++;penetration=Mathf.Max(penetration,-gap-Envelope);
                Vector3 point=Vector3.zero;
                for(int i=0;i<4;i++)if(row.weights[i]>0)point+=x[row.Node(i)]*row.weights[i];
                points.Add(point);
            }
        }
        public void Dispose()
        {
            if(boxes.IsCreated)boxes.Dispose();
            if(order.IsCreated)order.Dispose();
            if(tree.IsCreated)tree.Dispose();
            if(clusterStarts.IsCreated)clusterStarts.Dispose();
            if(overlaps.IsCreated)overlaps.Dispose();
            if(overlapCount.IsCreated)overlapCount.Dispose();
            if(flatPlanes.IsCreated)flatPlanes.Dispose();
            if(gatheredPlanes.IsCreated)gatheredPlanes.Dispose();
            if(planeOffsets.IsCreated)planeOffsets.Dispose();
            if(planeCursors.IsCreated)planeCursors.Dispose();
            if(gatheredCount.IsCreated)gatheredCount.Dispose();
            if(geometry.IsCreated)geometry.Dispose();
            if(nativeFeatures.IsCreated)nativeFeatures.Dispose();
            if(nativePositions.IsCreated)nativePositions.Dispose();
            if(pairs.IsCreated)pairs.Dispose();
            if(candidates.IsCreated)candidates.Dispose();
            if(results.IsCreated)results.Dispose();
            if(nativeSamples.IsCreated)nativeSamples.Dispose();
            if(pairPlanes.IsCreated)pairPlanes.Dispose();
            rowBuffer?.Release();indexBuffer?.Release();offsetBuffer?.Release();planeBuffer?.Release();planeOffsetBuffer?.Release();
        }
    }
}
