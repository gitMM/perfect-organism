using System;
using System.Collections.Generic;
using UnityEngine;

namespace Foliage
{
    // A segment has two centreline nodes and one transverse director particle.
    // The director gives a circular cross section a material orientation even on a straight rod.
    public sealed class RodPlant
    {
        public readonly PlantGeometry geometry;
        public readonly Vector3[] rest,skin;
        public readonly Quaternion[] restFrames,frames;
        public readonly int[] parent,skinTriangles;
        public readonly bool[] midrib;
        internal readonly int[] skinNode,skinRod,renderSlot;
        internal readonly Vector3[] skinOffset;
        public readonly int[] leafRod;
        const int PickBatch=64;
        readonly int[][] pickAnchors;
        readonly Dictionary<int,int> sharedRingNeighbor;
        internal readonly List<int> shells;
        internal readonly List<int> rebound=new();
        readonly float[] pickRadii;
        public int Ghost(int rod) => geometry.nodes.Count+rod;
        public RodPlant(PlantGeometry g):this(g,null,null){}
        internal RodPlant(PlantGeometry g,RodPlant previous,PlantCut cut)
        {
            geometry=g;
            rest=new Vector3[g.nodes.Count+g.rods.Count]; g.nodes.CopyTo(rest);
            parent=new int[g.rods.Count]; frames=new Quaternion[parent.Length]; restFrames=new Quaternion[parent.Length];
            renderSlot=new int[parent.Length];
            for(int i=0;i<renderSlot.Length;i++)
                renderSlot[i]=previous==null?i:i>0 && cut.rodSource[i]==cut.rodSource[i-1]?
                    previous.parent.Length:previous.renderSlot[cut.rodSource[i]];
            var incoming=new int[g.nodes.Count]; Array.Fill(incoming,-1);
            for(int i=0;i<parent.Length;i++)
            {
                var r=g.rods[i];
                parent[i]=incoming[r.a]; incoming[r.b]=i;
                Vector3 axis=rest[r.b]-rest[r.a];
                var side=Vector3.ProjectOnPlane(Mathf.Abs(axis.normalized.x)<.9f?Vector3.right:Vector3.forward,axis.normalized).normalized;
                rest[Ghost(i)]=(rest[r.a]+rest[r.b])*.5f+side*(.5f*axis.magnitude);
                restFrames[i]=frames[i]=Frame(rest,i);
            }
            midrib=new bool[rest.Length]; leafRod=new int[g.bladeRows.Count];
            for(int i=0;i<leafRod.Length;i++)
            {
                var rows=g.bladeRows[i]; leafRod[i]=rows[1].Length<3?-1:incoming[rows[0][0]];
                foreach(var row in rows) midrib[row[row.Length/2]]=true;
            }
            skin=new Vector3[g.skin.Count]; skinNode=new int[skin.Length]; skinRod=new int[skin.Length];
            foreach(int v in g.extraMidrib)midrib[v]=true;
            skinTriangles=new int[3*g.surface.Count];
            skinOffset=new Vector3[skin.Length];
            int retained=previous?.skin.Length??0;
            if(previous!=null)
            {
                Array.Copy(previous.skinNode,skinNode,retained);
                Array.Copy(previous.skinOffset,skinOffset,retained);
                Array.Copy(previous.skinTriangles,skinTriangles,previous.skinTriangles.Length);
                int[] remap=new int[previous.parent.Length];
                for(int i=0;i<cut.rodSource.Count;i++)remap[cut.rodSource[i]]=i;
                for(int v=0;v<retained;v++)
                {
                    int r=previous.skinRod[v];
                    skinRod[v]=r<0?-1:remap[r];
                }
            }
            void BindTriangle(int i)
            {
                var f=g.surface[i];
                for(int j=0;j<3;j++)
                {
                    int v=j==0?f.a:j==1?f.b:f.c;
                    skinTriangles[3*i+j]=v;skinRod[v]=f.rod;
                    if(f.face>=0)
                    {
                        var shell=g.faces[f.face];skinNode[v]=j==0?shell.a:j==1?shell.b:shell.c;
                    }
                    else
                    {
                        var rod=g.rods[f.rod];
                        skinNode[v]=(g.skin[v]-rest[rod.a]).sqrMagnitude<(g.skin[v]-rest[rod.b]).sqrMagnitude?rod.a:rod.b;
                    }
                }
            }
            sharedRingNeighbor=previous==null?new Dictionary<int,int>():new Dictionary<int,int>(previous.sharedRingNeighbor);
            shells=previous==null?new List<int>():new List<int>(previous.shells);
            var dirtyBatches=new HashSet<int>();
            if(previous==null)
            {
                for(int i=0;i<g.surface.Count;i++)
                {
                    BindTriangle(i);
                    if(g.surface[i].face>=0)shells.Add(i);
                }
                for(int i=0;i<g.surface.Count;i++)
                {
                    var f=g.surface[i];
                    if(f.rod<0)continue;
                    if(skinRod[f.a]!=f.rod)sharedRingNeighbor[f.a]=i;
                    if(skinRod[f.b]!=f.rod)sharedRingNeighbor[f.b]=i;
                    if(skinRod[f.c]!=f.rod)sharedRingNeighbor[f.c]=i;
                }
            }
            else
            {
                if(cut.ChangedRod>=0)foreach(var pair in previous.sharedRingNeighbor)
                {
                    int v=pair.Key;
                    if(previous.skinRod[v]!=cut.ChangedRod)
                    {
                        if(previous.geometry.surface[pair.Value].rod==cut.ChangedRod)sharedRingNeighbor.Remove(v);
                        continue;
                    }
                    int r=g.surface[pair.Value].rod;
                    var rod=g.rods[r];skinRod[v]=r;
                    skinNode[v]=(g.skin[v]-rest[rod.a]).sqrMagnitude<(g.skin[v]-rest[rod.b]).sqrMagnitude?rod.a:rod.b;
                    skinOffset[v]=Quaternion.Inverse(restFrames[r])*(g.skin[v]-rest[skinNode[v]]);
                    rebound.Add(v);sharedRingNeighbor.Remove(v);dirtyBatches.Add(pair.Value/PickBatch);
                }
                foreach(int i in cut.changedTriangles)
                {
                    BindTriangle(i);dirtyBatches.Add(i/PickBatch);
                    if(i>=previous.geometry.surface.Count && g.surface[i].face>=0)shells.Add(i);
                }
            }
            for(int i=0;i<g.fixedCollarVertices;i++){
                skinRod[i]=0;skinNode[i]=g.rods[0].b;
            }
            for(int i=retained;i<skin.Length;i++)
                if(skinRod[i]>=0)skinOffset[i]=Quaternion.Inverse(restFrames[skinRod[i]])*(g.skin[i]-rest[skinNode[i]]);
            int batches=(g.surface.Count+PickBatch-1)/PickBatch;
            pickAnchors=new int[batches][];pickRadii=new float[batches];
            if(previous!=null)
            {
                Array.Copy(previous.pickAnchors,pickAnchors,previous.pickAnchors.Length);
                Array.Copy(previous.pickRadii,pickRadii,previous.pickRadii.Length);
            }
            var anchors=new List<int>();var seen=new int[rest.Length];
            for(int batch=0;batch<batches;batch++)
            {
                if(previous!=null && !dirtyBatches.Contains(batch))continue;
                anchors.Clear();
                int end=Math.Min((batch+1)*PickBatch*3,skinTriangles.Length);
                float radius2=0;
                for(int j=batch*PickBatch*3;j<end;j++)
                {
                    int v=skinTriangles[j],node=skinNode[v];
                    if(seen[node]!=batch+1){seen[node]=batch+1;anchors.Add(node);}
                    radius2=Mathf.Max(radius2,skinOffset[v].sqrMagnitude);
                }
                pickRadii[batch]=Mathf.Sqrt(radius2);pickAnchors[batch]=anchors.ToArray();
            }

        }
        Quaternion Frame(Vector3[] positions,int i)
        {
            var r=geometry.rods[i];
            var axis=(positions[r.b]-positions[r.a]).normalized;
            var radial=positions[Ghost(i)]-(positions[r.b]+positions[r.a])*.5f;
            var up=Vector3.Cross(axis,radial);
            return up.sqrMagnitude>1e-15f?Quaternion.LookRotation(axis,up):frames[i];
        }
        public void UpdateSkin(Vector3[] positions)
        {
            for(int i=0;i<frames.Length;i++) frames[i]=Frame(positions,i);
            for(int i=0;i<skin.Length;i++)
            {
                int v=skinNode[i], r=skinRod[i];
                skin[i]=positions[v];
                if(r>=0) skin[i]+=frames[r]*skinOffset[i];
            }
        }
        public int PickSurface(Ray ray,Vector3[] positions,ref float nearest,out Vector3 weights)
        {
            for(int i=0;i<frames.Length;i++)frames[i]=Frame(positions,i);
            Vector3 Vertex(int v)
            {
                var p=positions[skinNode[v]];
                return skinRod[v]<0?p:p+frames[skinRod[v]]*skinOffset[v];
            }
            int hit=-1;weights=default;
            for(int batch=0;batch<pickRadii.Length;batch++)
            {
                var anchors=pickAnchors[batch];
                var bounds=new Bounds(positions[anchors[0]],Vector3.zero);
                for(int j=1;j<anchors.Length;j++)bounds.Encapsulate(positions[anchors[j]]);
                bounds.Expand(2*pickRadii[batch]);
                if(!bounds.IntersectRay(ray,out float entry) || entry>nearest)continue;
                int end=Math.Min((batch+1)*PickBatch,geometry.surface.Count);
                for(int i=batch*PickBatch;i<end;i++)
                {
                    var f=geometry.surface[i];
                    if(FoliageLab.Intersect(ray,Vertex(f.a),Vertex(f.b),Vertex(f.c),out float t,out var w) && t<nearest)
                    {hit=i;nearest=t;weights=w;}
                }
            }
            return hit;
        }
        public void Strain(int rod,Vector3[] positions,out float stretch,out float bend,out float twist)
        {
            var r=geometry.rods[rod];
            stretch=Vector3.Distance(positions[r.a],positions[r.b])/Vector3.Distance(rest[r.a],rest[r.b])-1;
            bend=0; twist=0;
            if(parent[rod]<0) return;
            Quaternion reference=frames[parent[rod]]*Quaternion.Inverse(restFrames[parent[rod]])*restFrames[rod];
            Quaternion delta=Quaternion.Inverse(reference)*frames[rod];
            bend=Vector3.Angle(Vector3.forward,delta*Vector3.forward);
            twist=Mathf.DeltaAngle(0,2*Mathf.Atan2(delta.z,delta.w)*Mathf.Rad2Deg);
        }
        public Vector3 CollarTarget(int rod,int node,Vector3[] positions)
        {
            int b=geometry.rods[rod].b;
            return positions[b]+frames[rod]*(Quaternion.Inverse(restFrames[rod])*(rest[node]-rest[b]));
        }
    }
}
