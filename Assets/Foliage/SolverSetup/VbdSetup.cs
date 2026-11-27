using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Burst;
using UnityEngine;

[assembly: InternalsVisibleTo("Assembly-CSharp")]

namespace Foliage
{
    // Matches the Elements buffer in SheetVbd.compute.
    internal struct VbdElement
    {
        public int a,b,c,d;
        public float rest,angle,vein,materialScale;
        public int extra,kind;
        public Vector4 reference;
    }

    [BurstCompile]
    internal static unsafe class VbdSetup
    {
        struct EdgeLink { public int hi,opposite,next; }

        // A topology pattern has local indices only. It can be reused across boughs
        // and seeds. Rest angles, lengths and masses always use the new tree.
        sealed class LeafPattern
        {
            public readonly int[] indices;
            public readonly Vector4IntLayout[] edges;
            public LeafPattern(int[] triangles,int count){
                indices=new int[count];
                edges=new Vector4IntLayout[count];
                var opposite=new Dictionary<(int,int),int>();
                for(int t=0;t<count;t++)indices[t]=triangles[t]-triangles[0];
                for(int t=0;t<count;t+=3){
                    for(int j=0;j<3;j++){
                        int a=indices[t+j],b=indices[t+(j+1)%3],c=indices[t+(j+2)%3];
                        var key=(Math.Min(a,b),Math.Max(a,b));
                        if(opposite.TryGetValue(key,out int other))edges[t+j]=new Vector4IntLayout(other,c,a,b);
                        else {opposite.Add(key,c);edges[t+j]=new Vector4IntLayout(a,b,-1,-1);}
                    }
                }
            }
            public bool Matches(int[] triangles,int count){
                if(count!=indices.Length)return false;
                for(int i=0;i<count;i++)if(triangles[i]-triangles[0]!=indices[i])return false;
                return true;
            }
        }
        struct Vector4IntLayout
        {
            public int a,b,c,d;
            public Vector4IntLayout(int a,int b,int c,int d){this.a=a;this.b=b;this.c=c;this.d=d;}
        }
        static volatile LeafPattern lastPattern;

        public static void BuildFaces(int[] triangles,int leafIndices,Vector3[] rest,float[] thickness,bool[] ribs,
            float[] mass,VbdElement[] elements,List<Vector2Int> edges,float sheetFaceMass)
        {
            LeafPattern pattern=null;
            if(leafIndices>0 && triangles.Length%leafIndices==0){
                pattern=lastPattern;
                if(pattern==null || !pattern.Matches(triangles,leafIndices))
                    lastPattern=pattern=new LeafPattern(triangles,leafIndices);
            }
            fixed(int* tri=triangles,relative=pattern?.indices)
            fixed(Vector4IntLayout* template=pattern?.edges)
            fixed(Vector3* p=rest)
            fixed(float* thick=thickness,m=mass)
            fixed(bool* rib=ribs)
            fixed(VbdElement* output=elements)
            fixed(Vector2Int* edgeList=new Vector2Int[triangles.Length]){
                int limit=thickness==null?rest.Length:Math.Min(rest.Length,Math.Min(thickness.Length,ribs.Length));
                if(!ValidNodes(tri,triangles.Length,limit))throw new ArgumentException("Face refers to a missing node.");
                bool repeated=pattern!=null && MatchesLeaves(tri,triangles.Length,relative,leafIndices);
                int count;
                fixed(int* head=repeated?null:new int[rest.Length])
                fixed(EdgeLink* links=repeated?null:new EdgeLink[triangles.Length])
                    count=Faces(tri,triangles.Length,p,thick,rib,m,output,edgeList,head,links,repeated?template:null,leafIndices,sheetFaceMass);
                edges.Capacity=Math.Max(edges.Capacity,count);
                for(int i=0;i<count;i++)edges.Add(edgeList[i]);
            }
        }

        [BurstCompile]
        static bool ValidNodes(int* indices,int count,int limit){
            for(int i=0;i<count;i++)if((uint)indices[i]>=(uint)limit)return false;
            return true;
        }

        [BurstCompile]
        static bool MatchesLeaves(int* tri,int count,int* pattern,int size){
            int last=0;
            for(int j=0;j<size;j++){
                if(pattern[j]<0)return false;
                last=Math.Max(last,pattern[j]);
            }
            for(int t=0;t<count;t+=size){
                if(t>0 && tri[t]<=tri[t-size]+last)return false;
                for(int j=0;j<size;j++)if(tri[t+j]-tri[t]!=pattern[j])return false;
            }
            return true;
        }

        [BurstCompile(FloatPrecision.High,FloatMode.Strict)]
        static int Faces(int* tri,int count,Vector3* p,float* thickness,bool* ribs,float* mass,VbdElement* output,
            Vector2Int* edges,int* heads,EdgeLink* links,Vector4IntLayout* template,int period,float sheetMass)
        {
            int unique=0,block=0;
            for(int t=0;t<count;t+=3){
                if(template!=null && t-block==period)block=t;
                int x=tri[t],y=tri[t+1],z=tri[t+2];
                float m=thickness==null?sheetMass:.075f*Vector3.Cross(p[y]-p[x],p[z]-p[x]).magnitude*
                    (thickness[x]+thickness[y]+thickness[z])/18;
                mass[x]+=m;mass[y]+=m;mass[z]+=m;
                for(int j=0;j<3;j++){
                    int a=tri[t+j],b=tri[t+(j+1)%3],c=-1,d=-1;
                    float length=Vector3.Distance(p[a],p[b]);
                    int lo=Math.Min(a,b),hi=Math.Max(a,b);
                    if(template!=null){
                        var e=template[t+j-block];
                        if(e.c>=0){int origin=tri[block];a=origin+e.a;b=origin+e.b;c=origin+e.c;d=origin+e.d;}
                    }else{
                        int link=heads[lo];
                        while(link!=0 && links[link-1].hi!=hi)link=links[link-1].next;
                        int opposite=tri[t+(j+2)%3];
                        if(link==0){
                            links[unique]=new EdgeLink{hi=hi,opposite=opposite,next=heads[lo]};
                            heads[lo]=unique+1;
                        }else{c=a;d=b;a=links[link-1].opposite;b=opposite;}
                    }
                    if(c<0)edges[unique++]=new Vector2Int(lo,hi);
                    float angle=0,vein=0,scale=1;
                    if(thickness!=null){
                        if(c<0){
                            vein=ribs[a] && ribs[b]?1:0;
                            scale=(thickness[a]+thickness[b])*.5f;
                        }else{
                            var axis=(p[d]-p[c]).normalized;
                            var n=Vector3.Cross(p[c]-p[a],p[d]-p[a]).normalized;
                            var normal=Vector3.Cross(p[d]-p[b],p[c]-p[b]).normalized;
                            angle=Mathf.Atan2(Vector3.Dot(Vector3.Cross(n,normal),axis),Vector3.Dot(n,normal));
                            vein=ribs[c]!=ribs[d] && (ribs[a] || ribs[b])?1:0;
                            scale=Mathf.Pow((thickness[c]+thickness[d])*.5f,3);
                        }
                    }
                    output[t+j]=new VbdElement{a=a,b=b,c=c,d=d,extra=-1,rest=length,angle=angle,vein=vein,materialScale=scale};
                }
            }
            return unique;
        }

        public static int Pack(VbdElement[] elements,int count,int[] offsets,int[] rods,int[] colors,bool sortStencils,
            out int[] incident,out int[] groupOffsets,out int[] groupNodes)
        {
            int n=colors.Length;
            fixed(VbdElement* e=elements)
            fixed(int* row=offsets,rod=rods,color=colors){
                if(!CountRows(e,count,row,rod,n))throw new ArgumentException("Constraint refers to a missing node.");
                incident=new int[offsets[n]];
                var keys=sortStencils?new long[n]:null;
                fixed(int* ids=incident,centers=new int[n],others=new int[n],used=new int[n+1])
                fixed(long* order=keys){
                    int groups=ColorRows(e,count,row,rod,ids,centers,others,color,used,order,n);
                    groupOffsets=new int[groups+1];
                    for(int i=0;i<n;i++)if(colors[i]>=0)groupOffsets[colors[i]+1]++;
                    for(int g=0;g<groups;g++)groupOffsets[g+1]=groupOffsets[g]+((groupOffsets[g+1]+63)/64)*64;
                    groupNodes=new int[groupOffsets[groups]];
                    Array.Fill(groupNodes,-1);
                    var next=(int[])groupOffsets.Clone();
                    for(int i=0;i<n;i++){
                        int node=keys==null?i:unchecked((int)keys[i]);
                        if(colors[node]>=0)groupNodes[next[colors[node]]++]=node;
                    }
                    return groups;
                }
            }
        }

        [BurstCompile]
        static bool CountRows(VbdElement* elements,int count,int* offsets,int* rods,int nodes){
            for(int i=0;i<count;i++){
                var e=elements[i];
                if((uint)e.a>=(uint)nodes || (uint)e.b>=(uint)nodes || e.c>=nodes || e.d>=nodes || e.extra>=nodes)
                    return false;
                offsets[e.a+1]++;offsets[e.b+1]++;
                if(e.c>=0)offsets[e.c+1]++;
                if(e.d>=0)offsets[e.d+1]++;
                if(e.extra>=0)offsets[e.extra+1]++;
                if(e.kind==1 && e.materialScale>0){rods[e.a]++;rods[e.b]++;}
            }
            for(int i=0;i<nodes;i++)offsets[i+1]+=offsets[i];
            return true;
        }

        [BurstCompile]
        static int ColorRows(VbdElement* elements,int count,int* offsets,int* rods,int* flat,int* centers,
            int* others,int* colors,int* used,long* order,int nodes)
        {
            for(int i=0;i<nodes;i++){centers[i]=offsets[i];others[i]=offsets[i]+rods[i];}
            for(int i=0;i<count;i++){
                var e=elements[i];
                var next=e.kind==1 && e.materialScale>0?centers:others;
                flat[next[e.a]++]=i;flat[next[e.b]++]=i;
                if(e.c>=0)flat[next[e.c]++]=i;
                if(e.d>=0)flat[next[e.d]++]=i;
                if(e.extra>=0)flat[next[e.extra]++]=i;
            }
            // Negative input marks a pin. Unvisited neighbors use the sentinel color -1.
            for(int i=0;i<nodes;i++){centers[i]=colors[i];colors[i]=-1;}
            int groups=0;
            for(int v=0;v<nodes;v++){
                int mark=v+1,frames=0,shellTerms=0;
                for(int j=offsets[v];j<offsets[v+1];j++){
                    var e=elements[flat[j]];
                    used[colors[e.a]+1]=mark;used[colors[e.b]+1]=mark;
                    if(e.c>=0)used[colors[e.c]+1]=mark;
                    if(e.d>=0)used[colors[e.d]+1]=mark;
                    if(e.extra>=0)used[colors[e.extra]+1]=mark;
                    if(e.kind==2 || e.kind==3)frames++;
                    if(e.kind==0)shellTerms++;
                }
                if(order!=null)order[v]=((long)frames<<48)+((long)shellTerms<<32)+v;
                if(centers[v]<0)continue;
                int c=0;while(used[c+1]==mark)c++;
                colors[v]=c;groups=Math.Max(groups,c+1);
            }
            if(order!=null){
                // Stable radix passes over the two stencil counts keep equal
                // stencils in node order, without comparing every pair of keys.
                int* bins=stackalloc int[256];
                for(int v=0;v<nodes;v++)centers[v]=v;
                for(int shift=32;shift<64;shift+=8){
                    for(int i=0;i<256;i++)bins[i]=0;
                    for(int i=0;i<nodes;i++)bins[(int)(order[i]>>shift)&255]++;
                    int start=0;
                    for(int i=0;i<256;i++){int size=bins[i];bins[i]=start;start+=size;}
                    for(int i=0;i<nodes;i++){int v=centers[i];others[bins[(int)(order[v]>>shift)&255]++]=v;}
                    var swap=centers;centers=others;others=swap;
                }
                for(int i=0;i<nodes;i++)order[i]=centers[i];
            }
            return groups;
        }
    }
}
