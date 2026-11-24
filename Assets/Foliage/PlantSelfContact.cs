using System;
using System.Collections.Generic;
using UnityEngine;

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
        struct Box { public Vector3 min,max; }
        readonly PlantContact.Feature[] features;
        readonly Box[] boxes;
        readonly HashSet<(int,int)> freshSeams=new();
        readonly List<(int,int)> clearedSeams=new();
        readonly int[] order,offsets,cursors;
        readonly Comparison<int> compare;
        readonly List<Row> rows=new();
        readonly List<bool> branchRows=new();
        readonly int[] rodParts;
        public int branchContacts {get;private set;}
        readonly Dictionary<(int,int,int,int),int> witnesses=new();
        readonly Row[] candidates=new Row[15]; // six vertex/face and nine edge/edge queries
        readonly ComputeShader gpu;
        readonly int relax,measure;
        readonly int predict;
        readonly List<Vector4>[] nodePlanes;
        readonly List<Vector4> flatPlanes=new();
        readonly int[] planeOffsets;
        ComputeBuffer planeBuffer,planeOffsetBuffer;
        float travelBudget;
        public bool dat=true;
        public bool safeguarding=>enabled && dat && features.Length>0;
        ComputeBuffer rowBuffer,indexBuffer,offsetBuffer;
        int[] indices=Array.Empty<int>();
        public bool enabled=true;
        public int active {get;private set;}
        public float penetration {get;private set;}
        public readonly List<Vector3> points=new();
        const float Envelope=.002f, Search=.012f;

        internal PlantSelfContact(PlantContact contact,int nodes,ComputeShader gpu,int predict,int relax,int measure)
        {
            this.gpu=gpu;this.relax=relax;this.measure=measure;
            this.predict=predict;
            features=contact?.features??Array.Empty<PlantContact.Feature>();
            rodParts=contact?.rodParts??Array.Empty<int>();
            if(contact!=null)
                foreach(var twin in contact.cutTwins)
                for(int a=0;a<features.Length;a++)
                {
                    if(!Contains(features[a],twin.x))continue;
                    for(int b=0;b<features.Length;b++)if(a!=b && Contains(features[b],twin.y))
                        freshSeams.Add((Math.Min(a,b),Math.Max(a,b)));
                }
            boxes=new Box[features.Length];order=new int[features.Length];
            for(int i=0;i<order.Length;i++)order[i]=i;
            compare=(a,b)=>boxes[a].min.y.CompareTo(boxes[b].min.y);
            offsets=new int[nodes+1];cursors=new int[nodes];
            nodePlanes=new List<Vector4>[nodes];planeOffsets=new int[nodes+1];
            for(int i=0;i<nodes;i++)nodePlanes[i]=new List<Vector4>();
            BindPlanes(ref planeBuffer,"DatPlanes",1,16);
            BindPlanes(ref planeOffsetBuffer,"DatOffsets",nodes+1,4);
            Bind(ref rowBuffer,"SelfRows",1,48);Bind(ref indexBuffer,"SelfIndices",1,4);
            Bind(ref offsetBuffer,"SelfOffsets",offsets.Length,4);
        }
        void BindPlanes(ref ComputeBuffer buffer,string name,int count,int stride)
        {
            if(buffer!=null && buffer.count>=count)return;
            buffer?.Release();buffer=new ComputeBuffer(Mathf.NextPowerOfTwo(count),stride);
            gpu.SetBuffer(predict,name,buffer);gpu.SetBuffer(relax,name,buffer);
        }
        void Bind(ref ComputeBuffer buffer,string name,int count,int stride)
        {
            if(buffer!=null && buffer.count>=count)return;
            buffer?.Release();
            buffer=new ComputeBuffer(Mathf.NextPowerOfTwo(count),stride);
            gpu.SetBuffer(relax,name,buffer);gpu.SetBuffer(measure,name,buffer);
        }
        static int Node(PlantContact.Feature f,int i)=>i==0?f.a:i==1?f.b:f.c;
        static bool Contains(PlantContact.Feature f,int v)=>v>=0 && (f.a==v || f.b==v || f.c==v);
        public void Clear(){active=0;branchContacts=0;penetration=0;points.Clear();rows.Clear();branchRows.Clear();}
        internal bool Prepare(Vector3[] x,VbdSheet.Sample[] samples,float dt)
        {
            Clear();witnesses.Clear();
            gpu.SetInt("UseDat",safeguarding?1:0);
            if(safeguarding)
            {
                float speed2=0;
                foreach(var sample in samples)speed2=Mathf.Max(speed2,sample.velocity.sqrMagnitude);
                travelBudget=Mathf.Min(.15f,Search+dt*Mathf.Sqrt(speed2));
                foreach(var list in nodePlanes)list.Clear();
                gpu.SetFloat("DatTravel",travelBudget);
            }
            if(enabled)
            {
                for(int id=0;id<features.Length;id++)
                {
                    var f=features[id];Vector3 lo=x[f.a],hi=lo;
                    for(int i=0;i<(f.c<0?2:3);i++)
                    {
                        int v=Node(f,i);Vector3 end=x[v]+samples[v].velocity*dt;
                        lo=Vector3.Min(lo,Vector3.Min(x[v],end));hi=Vector3.Max(hi,Vector3.Max(x[v],end));
                    }
                    var pad=Vector3.one*(f.skin+(safeguarding?travelBudget:Search*.5f));
                    boxes[id]=new Box{min=lo-pad,max=hi+pad};
                }
                Array.Sort(order,compare);
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
                for(int i=0;i<order.Length;i++)
                {
                    int id=order[i];var box=boxes[id];var a=features[id];
                    for(int j=i+1;j<order.Length;j++)
                    {
                        int other=order[j];var b=boxes[other];
                        if(b.min.y>box.max.y)break;
                        if(b.min.x>box.max.x || box.min.x>b.max.x || b.min.z>box.max.z || box.min.z>b.max.z)continue;
                        var f=features[other];
                        if(Contains(a,f.a)||Contains(a,f.b)||Contains(a,f.c))continue;
                        Closest(x,samples,dt,a,f,(Math.Min(id,other),Math.Max(id,other)));
                    }
                }
            }
            gpu.SetInt("HasSelfContact",rows.Count>0?1:0);
            if(safeguarding)
            {
                flatPlanes.Clear();
                for(int v=0;v<nodePlanes.Length;v++)
                {
                    planeOffsets[v]=flatPlanes.Count;flatPlanes.AddRange(nodePlanes[v]);
                }
                planeOffsets[nodePlanes.Length]=flatPlanes.Count;
                planeOffsetBuffer.SetData(planeOffsets);
                if(flatPlanes.Count>0)
                {
                    BindPlanes(ref planeBuffer,"DatPlanes",flatPlanes.Count,16);
                    planeBuffer.SetData(flatPlanes);
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
        void Closest(Vector3[] x,VbdSheet.Sample[] samples,float dt,PlantContact.Feature a,PlantContact.Feature b,(int,int) pair)
        {
            float sq=float.PositiveInfinity;int count=0,nearest=0;
            void Consider(Row row)
            {
                Vector3 delta=row.Delta(x);
                float d=delta.sqrMagnitude;
                if(d<sq){sq=d;nearest=count;}
                row.normal=delta;candidates[count++]=row;
            }
            void Project(int v,PlantContact.Feature f)
            {
                Vector3 ab=x[f.b]-x[f.a],ac=x[f.c]-x[f.a],p=x[v]-x[f.a],n=Vector3.Cross(ab,ac);
                float nn=n.sqrMagnitude;if(nn<1e-18f)return;
                float u=Vector3.Dot(Vector3.Cross(p,ac),n)/nn,w=Vector3.Dot(Vector3.Cross(ab,p),n)/nn;
                if(u<0 || w<0 || u+w>1)return;
                Consider(new Row{a=v,b=f.a,c=f.b,d=f.c,weights=new Vector4(1,u+w-1,-u,-w)});
            }
            int na=a.c<0?2:3,nb=b.c<0?2:3;
            if(nb==3)for(int i=0;i<na;i++)Project(Node(a,i),b);
            if(na==3)for(int i=0;i<nb;i++)Project(Node(b,i),a);
            for(int i=0;i<(na==2?1:3);i++)
            for(int j=0;j<(nb==2?1:3);j++)
            {
                int p=Node(a,i),q=Node(a,(i+1)%na),r=Node(b,j),s=Node(b,(j+1)%nb);
                Vector3 e=x[q]-x[p],f=x[s]-x[r],d=x[p]-x[r];
                float ee=e.sqrMagnitude,ff=f.sqrMagnitude,ef=Vector3.Dot(e,f);
                float ed=Vector3.Dot(e,d),fd=Vector3.Dot(f,d),den=ee*ff-ef*ef;
                float u=den>1e-8f*ee*ff?Mathf.Clamp01((ef*fd-ff*ed)/den):0;
                float v=Mathf.Clamp01((fd+ef*u)/Mathf.Max(ff,1e-16f));
                u=Mathf.Clamp01((ef*v-ed)/Mathf.Max(ee,1e-16f));
                Consider(new Row{a=p,b=q,c=r,d=s,weights=new Vector4(1-u,u,v-1,-v)});
            }
            // Parallel features can have a line or patch of equally close
            // witnesses. Keeping them distributes the load across the patch.
            float band=Mathf.Sqrt(sq)+Envelope;
            if(freshSeams.Contains(pair))
            {
                if(Mathf.Sqrt(sq)<=a.skin+b.skin+Envelope)return;
                freshSeams.Remove(pair);
            }
            Vector3 axis=candidates[nearest].normal;
            if(safeguarding && sq>1e-16f)
            {
                // Orient the closest-feature axis from B to A, including
                // vertex/face witnesses generated in the reverse direction.
                var row=candidates[nearest];
                var n=axis/Mathf.Sqrt(sq);
                if(!Contains(a,row.a))n=-n;
                void Support(Vector3 direction,out float lo,out float hi)
                {
                    lo=float.PositiveInfinity;hi=float.NegativeInfinity;
                    for(int i=0;i<na;i++)lo=Mathf.Min(lo,Vector3.Dot(direction,x[Node(a,i)]));
                    for(int i=0;i<nb;i++)hi=Mathf.Max(hi,Vector3.Dot(direction,x[Node(b,i)]));
                }
                Support(n,out float low,out float high);
                // A near-parallel edge tie can produce an inaccurate witness
                // direction. Only install a plane verified on the whole hull.
                if(low-high<Mathf.Sqrt(sq)*.99f)
                {
                    for(int j=0;j<count;j++)
                    {
                        Vector3 candidate=candidates[j].normal;
                        float len=candidate.magnitude;if(len<1e-9f)continue;
                        candidate*= (Contains(a,candidates[j].a)?1f:-1f)/len;
                        Support(candidate,out float lo,out float hi);
                        if(lo-hi>low-high){low=lo;high=hi;n=candidate;}
                    }
                }
                if(low>high)
                {
                    // Reserve both skins when initially clear. In an existing
                    // skin overlap keep the center features apart during recovery.
                    float skins=a.skin+b.skin;
                    float skinScale=low-high>=skins?1:(low-high)*.99f/skins;
                    float middle=.5f*(low-a.skin*skinScale+high+b.skin*skinScale);
                    void AddPlane(int v,Vector3 inward,float slack)
                    {
                        if(slack<1.733f*travelBudget)
                            nodePlanes[v].Add(new Vector4(inward.x,inward.y,inward.z,Mathf.Max(0,slack)*.999f));
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
            }
            for(int i=0;i<count;i++)
            {
                Vector3 n=candidates[i].normal;float length2=n.sqrMagnitude;
                float alignment=Vector3.Dot(n,axis);
                if(length2<=sq+1e-10f || (length2<=band*band && alignment>0 && alignment*alignment>.99f*length2*sq))
                    Register(candidates[i],x,samples,dt,a,b,pair.Item2<rodParts.Length && rodParts[pair.Item1]!=rodParts[pair.Item2]);
            }
        }
        void Register(Row best,Vector3[] x,VbdSheet.Sample[] samples,float dt,PlantContact.Feature a,PlantContact.Feature b,bool branchPair)
        {
            float dist=best.normal.magnitude;
            best.distance=a.skin+b.skin+Envelope;
            Vector3 velocity=Vector3.zero;
            for(int i=0;i<4;i++)velocity+=samples[best.Node(i)].velocity*best.weights[i];
            if(dist>best.distance+Search+velocity.magnitude*dt)return;
            if(dist>1e-7f)best.normal/=dist;
            else if(velocity.sqrMagnitude>1e-12f)best.normal=-velocity.normalized;
            else
            {
                var tri=a.c>=0?a:b;
                Vector3 n=tri.c>=0?Vector3.Cross(x[tri.b]-x[tri.a],x[tri.c]-x[tri.a]):
                    Vector3.Cross(x[a.b]-x[a.a],x[b.b]-x[b.a]);
                best.normal=n.sqrMagnitude>1e-16f?n.normalized:Vector3.up;
            }
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
                if(best.distance>rows[existing].distance){rows[existing]=best;branchRows[existing]=branchPair;}
            }
            else {witnesses.Add(key,rows.Count);rows.Add(best);branchRows.Add(branchPair);}
        }
        internal void Measure(Vector3[] x)
        {
            for(int k=0;k<rows.Count;k++)
            {
                var row=rows[k];
                float gap=Vector3.Dot(row.Delta(x),row.normal)-row.distance;
                if(gap>=0)continue;
                if(branchRows[k])branchContacts++;
                active++;penetration=Mathf.Max(penetration,-gap-Envelope);
                Vector3 point=Vector3.zero;
                for(int i=0;i<4;i++)if(row.weights[i]>0)point+=x[row.Node(i)]*row.weights[i];
                points.Add(point);
            }
        }
        public void Dispose(){rowBuffer?.Release();indexBuffer?.Release();offsetBuffer?.Release();planeBuffer?.Release();planeOffsetBuffer?.Release();}
    }
}
