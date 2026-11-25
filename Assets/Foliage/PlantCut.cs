using System;
using System.Collections.Generic;
using UnityEngine;

namespace Foliage
{
    // Build a replacement topology off-line. Cut points share material
    // coordinates and velocity, but each side owns its own solver node.
    internal sealed class PlantCut
    {
        internal readonly PlantGeometry g=new();
        internal readonly List<Vector3> positions=new(),velocities=new();
        internal readonly List<int> rodSource=new();
        internal readonly List<Vector2Int> seam=new();
        internal int replacedFaces;
        internal readonly List<int> faceSource=new();
        int cutRod=-1,cutPart=-1;
        internal int ChangedRod=>cutRod;
        float splitFraction;
        internal readonly List<int> changedTriangles=new();
        readonly VbdSheet old;
        readonly List<Color> tint=new();
        readonly List<Vector2> bladeUV=new();
        readonly PlantGeometry src;
        internal PlantCut(VbdSheet old)
        {
            this.old=old;src=old.plant.geometry;
            positions.Capacity=velocities.Capacity=src.nodes.Count+16;
            for(int i=0;i<src.nodes.Count;i++){positions.Add(old.positions[i]);velocities.Add(old.samples[i].velocity);}
        }
        void CopyTopology(bool blade)
        {
            g.rootNodes.AddRange(src.rootNodes);g.leavesPerPlant=src.leavesPerPlant;g.coarsePlants=src.coarsePlants;
            g.nodes.AddRange(src.nodes);g.thicknessRatio.AddRange(src.thicknessRatio);
            g.rods.AddRange(src.rods);g.faces.AddRange(src.faces);g.joints.AddRange(src.joints);
            g.bladeRows.AddRange(src.bladeRows);g.extraMidrib.UnionWith(src.extraMidrib);
            g.severedCollars.UnionWith(src.severedCollars);g.cutTwins.AddRange(src.cutTwins);
            for(int i=0;i<src.nodes.Count;i++)
            {
                tint.Add(Color.green);bladeUV.Add(default);
            }
            for(int i=0;i<src.rods.Count;i++)rodSource.Add(i);
            if(!blade)return;
            foreach(var t in src.surface)if(t.face>=0)
            {
                var f=src.faces[t.face];tint[f.a]=src.colors[t.a];tint[f.b]=src.colors[t.b];tint[f.c]=src.colors[t.c];
                bladeUV[f.a]=src.bladeUV[t.a];bladeUV[f.b]=src.bladeUV[t.b];bladeUV[f.c]=src.bladeUV[t.c];
            }
        }
        int NewNode(int a,int b,float t)
        {
            int id=g.nodes.Count;
            g.nodes.Add(Vector3.Lerp(g.nodes[a],g.nodes[b],t));
            g.thicknessRatio.Add(Mathf.Lerp(g.thicknessRatio[a],g.thicknessRatio[b],t));
            tint.Add(Color.Lerp(tint[a],tint[b],t));bladeUV.Add(Vector2.Lerp(bladeUV[a],bladeUV[b],t));
            positions.Add(Vector3.Lerp(positions[a],positions[b],t));velocities.Add(Vector3.Lerp(velocities[a],velocities[b],t));
            bool Rib(int v)=>g.extraMidrib.Contains(v)||(v<src.nodes.Count && old.plant.midrib[v]);
            if(Rib(a)&&Rib(b))g.extraMidrib.Add(id);
            return id;
        }
        int Twin(int node)
        {
            int id=NewNode(node,node,0);var pair=new Vector2Int(node,id);
            g.cutTwins.Add(pair);seam.Add(pair);return id;
        }
        internal bool Rod(int id,float t)
        {
            CopyTopology(false);
            if(id<0 || id>=g.rods.Count || !float.IsFinite(t))return false;
            var r=g.rods[id];
            cutRod=id;splitFraction=t;
            if(t<=.04f)
            {
                if(old.plant.parent[id]<0)return false;
                r.a=Twin(r.a);g.rods[id]=r;
            }
            else if(t>=.96f)
            {
                if(!g.rods.Exists(n=>n.a==r.b) && !g.faces.Exists(f=>f.a==r.b||f.b==r.b||f.c==r.b))return false;
                r.b=Twin(r.b);g.rods[id]=r;
            }
            else
            {
                int left=NewNode(r.a,r.b,t);var distal=r;distal.a=Twin(left);r.b=left;
                g.rods[id]=r;g.rods.Insert(id+1,distal);rodSource.Insert(id+1,id);
            }
            return true;
        }
        static int[] Corners(PlantGeometry.Face f)=>new[]{f.a,f.b,f.c};
        internal bool Blade(int picked,Plane plane)
        {
            if(picked<0 || picked>=src.faces.Count)return false;
            CopyTopology(true);
            int part=cutPart=src.faces[picked].part;
            var component=new HashSet<int>(Corners(src.faces[picked]));
            Expand(component,src.faces,part);
            var signed=new float[src.nodes.Count];var sides=new Dictionary<int,int>();
            int root=src.bladeRows[part/2-1][0][0];
            float orientation=plane.GetDistanceToPoint(positions[root])<0?-1:1;
            foreach(int v in component)
            {
                float d=orientation*plane.GetDistanceToPoint(positions[v]);signed[v]=Mathf.Abs(d)<.0001f?0:d;
            }
            foreach(var f in src.faces)if(f.part==part && component.Contains(f.a))
            {
                var ids=Corners(f);int mask=0;
                foreach(int v in ids)mask|=signed[v]>0?1:signed[v]<0?2:0;
                foreach(int v in ids)if(signed[v]==0){sides.TryGetValue(v,out int m);sides[v]=m|mask;}
            }
            var onPlane=new Dictionary<int,int>();
            var splits=new Dictionary<Vector2Int,Vector2Int>();
            int Crossing(int a,int b,int side)
            {
                var key=new Vector2Int(Math.Min(a,b),Math.Max(a,b));
                if(!splits.TryGetValue(key,out var pair))
                {
                    int id=NewNode(a,b,signed[a]/(signed[a]-signed[b]));pair=new Vector2Int(id,Twin(id));splits.Add(key,pair);
                }
                return side>0?pair.x:pair.y;
            }
            g.faces.Clear();
            for(int source=0;source<src.faces.Count;source++)
            {
                var f=src.faces[source];
                if(f.part!=part || !component.Contains(f.a)){g.faces.Add(f);faceSource.Add(source);continue;}
                int[] ids=Corners(f);
                if(signed[f.a]==0 && signed[f.b]==0 && signed[f.c]==0){g.faces.Add(f);faceSource.Add(source);continue;}
                int before=g.faces.Count;
                foreach(int side in new[]{1,-1})
                {
                    var poly=new List<int>(4);
                    for(int i=0;i<3;i++)
                    {
                        int a=ids[i],b=ids[(i+1)%3];float da=signed[a],db=signed[b];
                        if(side*da>=0)
                        {
                            int v=a;
                            if(da==0 && side<0 && sides[a]==3)
                            {
                                if(!onPlane.TryGetValue(a,out v)){v=Twin(a);onPlane.Add(a,v);}
                            }
                            poly.Add(v);
                        }
                        if(da*db<0)poly.Add(Crossing(a,b,side));
                    }
                    for(int j=1;j+1<poly.Count;j++)
                    {
                        g.faces.Add(new PlantGeometry.Face{a=poly[0],b=poly[j],c=poly[j+1],part=part});
                        faceSource.Add(source);
                    }
                }
                if(g.faces.Count>before+1)replacedFaces++;
            }
            if(seam.Count<2)return false;
            var attached=new HashSet<int>{root};Expand(attached,g.faces,part);
            foreach(int v in src.bladeRows[part/2-1][1])if(!attached.Contains(v))g.severedCollars.Add(v);
            return true;
        }
        static void Expand(HashSet<int> nodes,List<PlantGeometry.Face> faces,int part)
        {
            int previous;
            do
            {
                previous=nodes.Count;
                foreach(var f in faces)if(f.part==part && (nodes.Contains(f.a)||nodes.Contains(f.b)||nodes.Contains(f.c)))
                {nodes.Add(f.a);nodes.Add(f.b);nodes.Add(f.c);}
            }while(previous!=nodes.Count);
        }
        internal void BuildSkin()
        {
            int Vertex(Vector3 p,Color color,Vector2 uv=default){int i=g.skin.Count;g.skin.Add(p);g.colors.Add(color);g.bladeUV.Add(uv);return i;}
            g.skin.AddRange(src.skin);g.colors.AddRange(src.colors);g.bladeUV.AddRange(src.bladeUV);
            g.surface.AddRange(src.surface);
            if(cutRod<0)
            {
                var first=new int[src.faces.Count];
                for(int i=faceSource.Count-1;i>=0;i--)first[faceSource[i]]=i;
                var vertices=new Dictionary<int,int>();
                int BladeVertex(int v)
                {
                    if(!vertices.TryGetValue(v,out int i))vertices.Add(v,i=Vertex(g.nodes[v],tint[v],bladeUV[v]));
                    return i;
                }
                for(int i=0;i<src.surface.Count;i++)
                {
                    var f=src.surface[i];if(f.face<0)continue;
                    int source=f.face;
                    f.face=first[source];g.surface[i]=f;
                    if(src.faces[source].part!=cutPart)continue;
                    for(int j=f.face;j<faceSource.Count && faceSource[j]==source;j++)
                    {
                        var shell=g.faces[j];
                        var next=new PlantGeometry.SkinFace{a=BladeVertex(shell.a),b=BladeVertex(shell.b),
                            c=BladeVertex(shell.c),rod=-1,face=j};
                        if(j==f.face){g.surface[i]=next;changedTriangles.Add(i);}
                        else {changedTriangles.Add(g.surface.Count);g.surface.Add(next);}
                    }
                }
                return;
            }

            bool interior=g.rods.Count!=src.rods.Count;
            var rod=src.rods[cutRod];
            var axis=(src.nodes[rod.b]-src.nodes[rod.a]).normalized;
            var center=Vector3.Lerp(src.nodes[rod.a],src.nodes[rod.b],splitFraction);
            var copies=new Dictionary<(int,int),int>();
            var crossings=new Dictionary<(int,int,int),int>();
            int Copy(int v,int half)
            {
                if(!copies.TryGetValue((v,half),out int id))
                    copies.Add((v,half),id=Vertex(src.skin[v],src.colors[v],src.bladeUV[v]));
                return id;
            }
            int Crossing(int a,int b,int half,float da,float db)
            {
                if(da==0)return Copy(a,half);
                if(db==0)return Copy(b,half);
                var key=(Math.Min(a,b),Math.Max(a,b),half);
                if(!crossings.TryGetValue(key,out int v))
                {
                    float t=da/(da-db);
                    v=Vertex(Vector3.Lerp(src.skin[a],src.skin[b],t),Color.Lerp(src.colors[a],src.colors[b],t),
                        Vector2.Lerp(src.bladeUV[a],src.bladeUV[b],t));
                    crossings.Add(key,v);
                }
                return v;
            }
            int[] ids=new int[3],poly=new int[4],capCenter={-1,-1};
            float[] distance=new float[3];
            for(int i=0;i<src.surface.Count;i++)
            {
                var f=src.surface[i];
                if(f.rod!=cutRod)
                {
                    if(interior && f.rod>cutRod){f.rod++;g.surface[i]=f;}
                    continue;
                }
                ids[0]=f.a;ids[1]=f.b;ids[2]=f.c;
                for(int j=0;j<3;j++)distance[j]=Vector3.Dot(src.skin[ids[j]]-center,axis);
                bool replaced=false;
                void Emit(int a,int b,int c,int half)
                {
                    var next=new PlantGeometry.SkinFace{a=a,b=b,c=c,rod=cutRod+half,face=-1};
                    if(!replaced){g.surface[i]=next;changedTriangles.Add(i);replaced=true;}
                    else{changedTriangles.Add(g.surface.Count);g.surface.Add(next);}
                }
                for(int half=0;half<(interior?2:1);half++)
                {
                    int n=0,left=-1,right=-1;
                    for(int j=0;j<3;j++)
                    {
                        int k=(j+1)%3;
                        float d=distance[j],e=distance[k];
                        if(!interior || (half==0?d<=0:d>=0))poly[n++]=Copy(ids[j],half);
                        if(!interior)continue;
                        int edge=-1;
                        if(d*e<0){edge=Crossing(ids[j],ids[k],half,d,e);poly[n++]=edge;}
                        else if(d==0)edge=Copy(ids[j],half);
                        if(edge>=0){if(left<0)left=edge;else right=edge;}
                    }
                    for(int j=1;j+1<n;j++)Emit(poly[0],poly[j],poly[j+1],half);
                    if(n>=3 && left>=0 && right>=0 && src.bladeUV[f.a].y<2)
                    {
                        if(capCenter[half]<0)capCenter[half]=Vertex(center,src.colors[f.a]);
                        if(Vector3.Dot(Vector3.Cross(g.skin[left]-center,g.skin[right]-center),axis)*(half==0?1:-1)<0)
                            (left,right)=(right,left);
                        Emit(capCenter[half],left,right,half);
                    }
                }
            }
        }
    }
}
