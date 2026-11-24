using System;
using System.Collections.Generic;
using UnityEngine;

namespace Foliage
{
    // Ownership resolves shared boundary ties. The full one-ring defines a vertex's normal cone.
    internal sealed class OgcTopology
    {
        internal struct Region
        {
            public Vector3Int vertexOwner,edgeOwner,across;
        }
        public readonly Region[] regions;
        public readonly int[] offsets,neighbors;
        public OgcTopology(PlantGeometry plant)
        {
            int first=plant.rods.Count;
            regions=new Region[first+plant.faces.Count];
            var owner=new int[plant.nodes.Count]; Array.Fill(owner,-1);
            var rings=new SortedSet<int>[owner.Length];
            var incidence=new Dictionary<Vector2Int,(int face,int side,int opposite)>();
            for(int i=0;i<plant.faces.Count;i++)
            {
                int id=first+i; var face=plant.faces[i];
                var nodes=new Vector3Int(face.a,face.b,face.c);
                for(int j=0;j<3;j++)
                {
                    int a=nodes[j],b=nodes[(j+1)%3],opposite=nodes[(j+2)%3];
                    if(owner[a]<0)owner[a]=id;
                    regions[id].vertexOwner[j]=owner[a];
                    (rings[a]??=new SortedSet<int>()).Add(b);
                    (rings[b]??=new SortedSet<int>()).Add(a);
                    var key=new Vector2Int(Math.Min(a,b),Math.Max(a,b));
                    if(incidence.TryGetValue(key,out var other))
                    {
                        regions[id].edgeOwner[j]=other.face;
                        regions[id].across[j]=other.opposite;
                        regions[other.face].across[other.side]=opposite;
                    }
                    else
                    {
                        incidence.Add(key,(id,j,opposite));
                        regions[id].edgeOwner[j]=id;regions[id].across[j]=-1;
                    }
                }
            }
            var flat=new List<int>();offsets=new int[owner.Length+1];
            for(int i=0;i<owner.Length;i++)
            {
                offsets[i]=flat.Count;
                if(rings[i]!=null)flat.AddRange(rings[i]);
            }
            offsets[owner.Length]=flat.Count;neighbors=flat.ToArray();
        }
    }
}
