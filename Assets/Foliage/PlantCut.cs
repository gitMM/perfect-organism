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
        int changedTube=-1;
        readonly VbdSheet old;
        readonly List<Color> tint=new();
        readonly PlantGeometry src;
        internal PlantCut(VbdSheet old)
        {
            this.old=old;src=old.plant.geometry;
            g.nodes.AddRange(src.nodes);g.thicknessRatio.AddRange(src.thicknessRatio);
            g.rods.AddRange(src.rods);g.faces.AddRange(src.faces);g.joints.AddRange(src.joints);
            g.bladeRows.AddRange(src.bladeRows);g.extraMidrib.UnionWith(src.extraMidrib);
            g.severedCollars.UnionWith(src.severedCollars);g.cutTwins.AddRange(src.cutTwins);
            for(int i=0;i<src.nodes.Count;i++)
            {
                positions.Add(old.positions[i]);velocities.Add(old.samples[i].velocity);tint.Add(Color.green);
            }
            for(int i=0;i<src.rods.Count;i++)rodSource.Add(i);
            foreach(var t in src.surface)if(t.face>=0)
            {
                var f=src.faces[t.face];tint[f.a]=src.colors[t.a];tint[f.b]=src.colors[t.b];tint[f.c]=src.colors[t.c];
            }
        }
        int NewNode(int a,int b,float t)
        {
            int id=g.nodes.Count;
            g.nodes.Add(Vector3.Lerp(g.nodes[a],g.nodes[b],t));
            g.thicknessRatio.Add(Mathf.Lerp(g.thicknessRatio[a],g.thicknessRatio[b],t));
            tint.Add(Color.Lerp(tint[a],tint[b],t));
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
            if(id<0 || id>=g.rods.Count || !float.IsFinite(t))return false;
            var r=g.rods[id];
            changedTube=r.part;
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
            int part=src.faces[picked].part;
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
            foreach(var f in src.faces)
            {
                if(f.part!=part || !component.Contains(f.a)){g.faces.Add(f);continue;}
                int[] ids=Corners(f);
                if(signed[f.a]==0 && signed[f.b]==0 && signed[f.c]==0){g.faces.Add(f);continue;}
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
                        g.faces.Add(new PlantGeometry.Face{a=poly[0],b=poly[j],c=poly[j+1],part=part});
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
            int Vertex(Vector3 p,Color color){int i=g.skin.Count;g.skin.Add(p);g.colors.Add(color);return i;}
            void Tri(int a,int b,int c,int rod,int face)=>g.surface.Add(new PlantGeometry.SkinFace{a=a,b=b,c=c,rod=rod,face=face});
            var bladeVertices=new Dictionary<int,int>();
            int BladeVertex(int v)
            {
                if(!bladeVertices.TryGetValue(v,out int i))bladeVertices.Add(v,i=Vertex(g.nodes[v],tint[v]));
                return i;
            }
            for(int i=0;i<g.faces.Count;i++)
            {
                var f=g.faces[i];Tri(BladeVertex(f.a),BladeVertex(f.b),BladeVertex(f.c),-1,i);
            }
            var incoming=new Dictionary<(int,int),int>();var outgoing=new Dictionary<(int,int),int>();
            for(int i=0;i<g.rods.Count;i++){var r=g.rods[i];incoming[(r.part,r.b)]=i;outgoing[(r.part,r.a)]=i;}
            var rings=new Dictionary<(int,int),int>();Color green=new Color(.35f,.54f,.21f);
            var oldToNew=new Dictionary<int,int>();
            for(int i=0;i<g.rods.Count;i++)if(g.rods[i].part!=changedTube)oldToNew[rodSource[i]]=i;
            var savedVertices=new Dictionary<int,int>();
            int CopyVertex(int v)
            {
                if(!savedVertices.TryGetValue(v,out int i))savedVertices.Add(v,i=Vertex(src.skin[v],src.colors[v]));
                return i;
            }
            foreach(var f in src.surface)
                if(f.rod>=0 && oldToNew.TryGetValue(f.rod,out int r))
                    Tri(CopyVertex(f.a),CopyVertex(f.b),CopyVertex(f.c),r,-1);
            const int sides=8;
            int Ring(int part,int v,ref Vector3 side)
            {
                if(rings.TryGetValue((part,v),out int ring))return ring;
                Vector3 tangent=Vector3.zero;float radius=0;
                if(outgoing.TryGetValue((part,v),out int next)){var r=g.rods[next];tangent=g.nodes[r.b]-g.nodes[v];radius=r.radius;}
                if(incoming.TryGetValue((part,v),out int prev)){var r=g.rods[prev];tangent+=g.nodes[v]-g.nodes[r.a];radius=r.radius;}
                tangent.Normalize();side=Vector3.ProjectOnPlane(side,tangent).normalized;
                if(side.sqrMagnitude<.5f)side=Vector3.Cross(tangent,Vector3.forward).normalized;
                Vector3 up=Vector3.Cross(tangent,side);
                ring=g.skin.Count;rings[(part,v)]=ring;
                for(int j=0;j<sides;j++){float angle=j*2*Mathf.PI/sides;Vertex(g.nodes[v]+radius*(Mathf.Cos(angle)*side+Mathf.Sin(angle)*up),green);}
                return ring;
            }
            var basis=new Dictionary<(int,int),Vector3>();
            for(int i=0;i<g.rods.Count;i++)
            {
                var r=g.rods[i];if(r.part!=changedTube)continue;
                if(!basis.TryGetValue((r.part,r.a),out var side))side=Vector3.right;
                int a=Ring(r.part,r.a,ref side),b=Ring(r.part,r.b,ref side);basis[(r.part,r.b)]=side;
                int ca=incoming.ContainsKey((r.part,r.a))?-1:Vertex(g.nodes[r.a],green);
                int cb=outgoing.ContainsKey((r.part,r.b))?-1:Vertex(g.nodes[r.b],green);
                for(int j=0;j<sides;j++)
                {
                    int k=(j+1)%sides;Tri(a+j,a+k,b+k,i,-1);Tri(a+j,b+k,b+j,i,-1);
                    if(ca>=0)Tri(ca,a+k,a+j,i,-1);if(cb>=0)Tri(cb,b+j,b+k,i,-1);
                }
            }
        }
    }
}
