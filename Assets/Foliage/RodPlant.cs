using System;
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
        readonly int[] skinNode,skinRod;
        public readonly int[] leafRod;
        public int Ghost(int rod) => geometry.nodes.Count+rod;
        public RodPlant(PlantGeometry g)
        {
            geometry=g;
            rest=new Vector3[g.nodes.Count+g.rods.Count]; g.nodes.CopyTo(rest);
            parent=new int[g.rods.Count]; frames=new Quaternion[parent.Length]; restFrames=new Quaternion[parent.Length];
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
                var rows=g.bladeRows[i]; leafRod[i]=incoming[rows[0][0]];
                foreach(var row in rows) midrib[row[row.Length/2]]=true;
            }
            skin=new Vector3[g.skin.Count]; skinNode=new int[skin.Length]; skinRod=new int[skin.Length];
            foreach(int v in g.extraMidrib)midrib[v]=true;
            skinTriangles=new int[3*g.surface.Count];
            for(int i=0;i<g.surface.Count;i++)
            {
                var f=g.surface[i];
                for(int j=0;j<3;j++)
                {
                    int vertex=j==0?f.a:j==1?f.b:f.c;
                    skinTriangles[3*i+j]=vertex;
                    skinRod[vertex]=f.rod;
                    if(f.face>=0)
                    {
                        var shell=g.faces[f.face]; skinNode[vertex]=j==0?shell.a:j==1?shell.b:shell.c;
                    }
                    else
                    {
                        var rod=g.rods[f.rod];
                        skinNode[vertex]=(g.skin[vertex]-rest[rod.a]).sqrMagnitude<(g.skin[vertex]-rest[rod.b]).sqrMagnitude?rod.a:rod.b;
                    }
                }
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
                if(r>=0) skin[i]+=frames[r]*(Quaternion.Inverse(restFrames[r])*(geometry.skin[i]-rest[v]));
            }
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
