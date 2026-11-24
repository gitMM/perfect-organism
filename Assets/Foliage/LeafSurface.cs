using System.Collections.Generic;
using UnityEngine;

namespace Foliage
{
    // Local indices identify shell particles; sourceNodes maps each one back to the plant.
    public sealed class LeafSurface
    {
        public readonly Vector3[] rest;
        public readonly int[] sourceNodes, triangles;
        public readonly bool[] clamped, midrib;
        public readonly float[] thicknessRatio;
        public readonly float length;
        public LeafSurface(PlantGeometry plant,int leaf)
        {
            var rows=plant.bladeRows[leaf];
            var nodes=new List<int>();
            var map=new Dictionary<int,int>();
            foreach(var row in rows)
                foreach(int node in row) {map.Add(node,nodes.Count); nodes.Add(node);}
            sourceNodes=nodes.ToArray();
            rest=new Vector3[nodes.Count]; clamped=new bool[nodes.Count]; midrib=new bool[nodes.Count];
            thicknessRatio=new float[nodes.Count];
            for(int i=0;i<nodes.Count;i++)
            {
                rest[i]=plant.nodes[nodes[i]];
                thicknessRatio[i]=plant.thicknessRatio[nodes[i]];
            }
            foreach(var row in rows) midrib[map[row[row.Length/2]]]=true;
            // A short clamped collar supplies the stalk's orientation before rods are simulated.
            clamped[0]=true;
            int center=rows[1].Length/2;
            for(int j=center-1;j<=center+1;j++) clamped[map[rows[1][j]]]=true;
            var indices=new List<int>();
            foreach(var f in plant.faces)
                if(f.part==2*(leaf+1)) { indices.Add(map[f.a]); indices.Add(map[f.b]); indices.Add(map[f.c]); }
            triangles=indices.ToArray();
            length=Vector3.Distance(rest[0],rest[rest.Length-1]);
        }
    }
}
