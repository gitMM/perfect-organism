using System;
using System.Collections.Generic;
using UnityEngine;

namespace Foliage
{
    [Serializable]
    public struct PlantSettings
    {
        public int seed, leaves, resolution;
        public float height, radius, width, curvature;
        public bool forked;
        public static PlantSettings BranchTest=>new PlantSettings
        {
            seed=17,leaves=18,resolution=6,height=3.2f,radius=.044f,
            width=.16f,curvature=.32f,forked=true
        };
        public static PlantSettings Default => new PlantSettings
        {
            seed = 17, leaves = 7, resolution = 8, height = 2.2f,
            radius = .028f, width = .25f, curvature = .44f
        };
    }

    // Rods and shell faces index the same nodes. A leaf base is its stalk's end node.
    public sealed class PlantGeometry
    {
        public struct Rod { public int a, b, part; public float radius; }
        public struct Face { public int a, b, c, part; }
        public struct SkinFace { public int a, b, c, rod, face; }
        public readonly List<Vector3> nodes = new();
        public readonly List<Rod> rods = new();
        public readonly List<Face> faces = new();
        public readonly List<int> joints = new();
        public readonly List<List<int[]>> bladeRows = new();
        public readonly List<float> thicknessRatio = new();
        internal readonly List<Vector2Int> cutTwins=new();
        internal readonly HashSet<int> severedCollars=new();
        internal readonly HashSet<int> extraMidrib=new();
        public readonly List<Vector3> skin = new();
        public readonly List<Color> colors = new();
        public readonly List<SkinFace> surface = new();
        public readonly List<string> issues = new();
        public readonly HashSet<int> badNodes = new();
        public int islands { get; private set; }
        internal int[] component;
        public int ComponentOf(int node)=>component[node];
        public float minQuality { get; private set; }
        public static string Part(int id) => id == 0 ? "Stem" :
            ((id % 2 == 1 ? "Stalk " : "Leaf ") + ((id + 1) / 2));

        int Node(Vector3 p) { nodes.Add(p); thicknessRatio.Add(1f); return nodes.Count - 1; }
        int Vertex(Vector3 p, Color color)
        {
            skin.Add(p);
            colors.Add(color);
            return skin.Count - 1;
        }
        void Triangle(int a, int b, int c, int rod, int face)
        {
            surface.Add(new SkinFace { a = a, b = b, c = c, rod = rod, face = face });
        }

        // Continuous rings at joints along each curved stem/stalk.
        void Tube(List<int> path, int part, float radius, float taper)
        {
            const int sides = 8;
            int start = skin.Count;
            Vector3 side = Vector3.right;
            Color color = new Color(.35f, .54f, .21f);
            for (int i = 0; i < path.Count; i++)
            {
                Vector3 tangent = (nodes[path[Math.Min(i+1,path.Count-1)]] -
                    nodes[path[Math.Max(i-1,0)]]).normalized;
                side = Vector3.ProjectOnPlane(side, tangent).normalized;
                if (side.sqrMagnitude < .5f) side = Vector3.Cross(tangent, Vector3.forward).normalized;
                Vector3 up = Vector3.Cross(tangent, side);
                float r = radius * Mathf.Lerp(1, taper, (float)i / (path.Count-1));
                for (int j = 0; j < sides; j++)
                {
                    float angle = j * (2*Mathf.PI/sides);
                    Vertex(nodes[path[i]] + r*(Mathf.Cos(angle)*side + Mathf.Sin(angle)*up), color);
                }
                if (i == 0) continue;
                int id = rods.Count;
                rods.Add(new Rod { a = path[i-1], b = path[i], part = part, radius = r });
                for (int j = 0; j < sides; j++)
                {
                    int a = start + (i-1)*sides+j, b = start+(i-1)*sides+(j+1)%sides;
                    Triangle(a,b,b+sides,id,-1);
                    Triangle(a,b+sides,a+sides,id,-1);
                }
            }
            for (int end = 0; end < 2; end++)
            {
                int i = end*(path.Count-1), ring = start+i*sides;
                int center = Vertex(nodes[path[i]], color);
                int rod = end == 1 ? rods.Count-1 : rods.Count-(path.Count-1);
                for (int j = 0; j < sides; j++)
                {
                    int a = ring+j, b = ring+(j+1)%sides;
                    Triangle(center,end==1?a:b,end==1?b:a,rod,-1);
                }
            }
        }

        void Blade(int root, Vector3 direction, float length, PlantSettings s, int part)
        {
            Vector3 side = Vector3.Cross(Vector3.up, direction).normalized;
            // Each row spans the blade. Tips collapse to one vertex without degenerate faces.
            int across = 2*Math.Max(1,s.resolution/4);
            var vertexMap = new Dictionary<int,int>();
            var rows = new List<int[]>();
            bladeRows.Add(rows);
            int[] previous = null;
            for (int i=0; i<=s.resolution; i++)
            {
                float t = (float)i/s.resolution;
                // Material thickness relative to the distal blade, measured along the generated rows.
                float thickness=Mathf.Lerp(2.4f,1f,Mathf.SmoothStep(0,1,t/.65f));
                float envelope = Mathf.Pow(Mathf.Max(0,Mathf.Sin(Mathf.PI*t)), .78f)*(1.18f-.36f*t);
                Vector3 center = nodes[root] + length*(direction*t +
                    Vector3.up*s.curvature*(.48f*Mathf.Sin(Mathf.PI*t)-.24f*t*t));
                bool tip = i==0 || i==s.resolution;
                int[] row = new int[tip ? 1 : across+1];
                for (int j=0; j<row.Length; j++)
                {
                    float u = tip ? 0 : 2f*j/across-1;
                    Vector3 p = center + length*envelope*(side*u*s.width -
                        Vector3.up*.1f*s.curvature*Mathf.Abs(u));
                    int node = i==0 ? root : Node(p);
                    row[j] = node;
                    thicknessRatio[node]=thickness;
                    Color color = Color.Lerp(new Color(.38f,.62f,.21f),new Color(.09f,.36f,.19f),
                        Mathf.Clamp01(.29f + Mathf.Abs(u)*.68f - t*.15f));
                    vertexMap[node] = Vertex(nodes[node],color);
                }
                void Add(int a,int b,int c)
                {
                    int id = faces.Count;
                    faces.Add(new Face { a=a,b=c,c=b,part=part });
                    Triangle(vertexMap[a],vertexMap[c],vertexMap[b],-1,id);
                }
                if (i>0)
                    for (int j=0;j<across;j++)
                    {
                        if (previous.Length==1) Add(previous[0],row[j+1],row[j]);
                        else if (tip) Add(previous[j],previous[j+1],row[0]);
                        else
                        {
                            Add(previous[j],previous[j+1],row[j+1]);
                            Add(previous[j],row[j+1],row[j]);
                        }
                    }
                rows.Add(row);
                previous=row;
            }
        }

        public static PlantGeometry Generate(PlantSettings s)
        {
            var g = new PlantGeometry();
            var rng = new System.Random(s.seed);
            float phase = (float)rng.NextDouble()*2*Mathf.PI;
            int segments = Math.Max(s.leaves+2,s.resolution*2);
            var stem = new List<int>();
            for (int i=0;i<=segments;i++)
            {
                float t=(float)i/segments;
                stem.Add(g.Node(s.height*new Vector3(.13f*s.curvature*t*Mathf.Sin(phase+3*t),
                    t,.22f*s.curvature*t*t*Mathf.Cos(phase+2*t))));
            }
            g.Tube(stem,0,s.radius,.34f);
            if(s.forked)
            {
                g.ForkedBranches(stem,s,phase);
                g.Validate();
                return g;
            }
            for (int i=0;i<s.leaves;i++)
            {
                float t=(i+1f)/(s.leaves+1);
                int root=stem[Mathf.RoundToInt(Mathf.Lerp(1,segments,t))];
                float angle=phase+i*2.39996f+(.25f*(float)rng.NextDouble());
                Vector3 d=new Vector3(Mathf.Cos(angle),0,Mathf.Sin(angle));
                float length=s.height*Mathf.Lerp(.45f,.21f,t)*(.94f+.12f*(float)rng.NextDouble());
                var stalk=new List<int>{root};
                int count=Math.Max(2,s.resolution/3);
                for (int j=1;j<=count;j++)
                {
                    float u=(float)j/count;
                    stalk.Add(g.Node(g.nodes[root]+length*(.36f*d*u+Vector3.up*.22f*u*(1.4f-.4f*u))));
                }
                int end=stalk[count];
                g.Tube(stalk,2*i+1,s.radius*.46f,.55f);
                g.joints.Add(root);
                g.joints.Add(end);
                g.Blade(end,d,length,s,2*i+2);
            }
            g.Validate();
            return g;
        }

        void ForkedBranches(List<int> stem,PlantSettings s,float phase)
        {
            int tiers=(s.leaves+2)/3;
            for(int tier=0;tier<tiers;tier++)
            {
                float level=(tier+.5f)/tiers;
                int root=stem[Mathf.RoundToInt(Mathf.Lerp(.25f,.91f,level)*(stem.Count-1))];
                float angle=phase+tier*2.39996f;
                Vector3 outward=new Vector3(Mathf.Cos(angle),0,Mathf.Sin(angle));
                Vector3 side=Vector3.Cross(Vector3.up,outward);
                float reach=s.height*Mathf.Lerp(.32f,.20f,level);
                Vector3 Center(float t)=>nodes[root]+reach*(t*outward+Vector3.up*(.36f*t+.15f*t*t));
                var primary=new List<int>{root};
                for(int j=1;j<=4;j++)primary.Add(Node(Center(j/4f)));
                void AddShoot(List<int> path,float radius,Vector3 direction)
                {
                    int part=2*bladeRows.Count+1,end=path[path.Count-1];
                    Tube(path,part,radius,.68f);joints.Add(path[0]);joints.Add(end);
                    Blade(end,direction,reach*.48f,s,part+1);
                }
                AddShoot(primary,s.radius*.36f,outward);
                // Forks bow away from the parent, then approach it near the tips.
                // The three blades fan outward to leave the contact rods accessible.
                for(int sign=-1;sign<=1 && bladeRows.Count<s.leaves;sign+=2)
                {
                    var fork=new List<int>{primary[1]};
                    for(int j=1;j<=4;j++)
                    {
                        float u=j/4f,t=Mathf.Lerp(.25f,.94f,u);
                        float offset=reach*.13f*Mathf.Sin(Mathf.PI*u)+s.radius*.85f*u;
                        fork.Add(Node(Center(t)+sign*side*offset+Vector3.up*(s.radius*.2f*u)));
                    }
                    AddShoot(fork,s.radius*.23f,(outward*.35f+sign*side).normalized);
                }
            }
        }

        public void Validate()
        {
            issues.Clear(); badNodes.Clear();
            var parent = new int[nodes.Count];
            for (int i=0;i<parent.Length;i++)
            {
                parent[i]=i;
                if (!float.IsFinite(nodes[i].sqrMagnitude)) { issues.Add($"Non-finite node {i}"); badNodes.Add(i); }
            }
            int Find(int i) { while(parent[i]!=i) { parent[i]=parent[parent[i]]; i=parent[i]; } return i; }
            bool Valid(int i) => i>=0 && i<nodes.Count;
            for(int i=0;i<rods.Count;i++)
            {
                var r=rods[i];
                if (!Valid(r.a)||!Valid(r.b)) { issues.Add($"Invalid indices on rod {i}"); continue; }
                if ((nodes[r.a]-nodes[r.b]).sqrMagnitude<1e-14f) { issues.Add($"Zero length rod {i}"); badNodes.Add(r.a); }
                parent[Find(r.b)]=Find(r.a);
            }
            minQuality=1;
            for(int i=0;i<faces.Count;i++)
            {
                var f=faces[i];
                if (!Valid(f.a)||!Valid(f.b)||!Valid(f.c)) { issues.Add($"Invalid indices on face {i}"); continue; }
                Vector3 ab=nodes[f.b]-nodes[f.a], ac=nodes[f.c]-nodes[f.a];
                float q=2*Mathf.Sqrt(3)*Vector3.Cross(ab,ac).magnitude/
                    Mathf.Max(1e-20f,ab.sqrMagnitude+ac.sqrMagnitude+(ac-ab).sqrMagnitude);
                minQuality=Mathf.Min(minQuality,q);
                if (!(q>1e-5f)) { issues.Add($"Degenerate face {i}"); badNodes.Add(f.a); badNodes.Add(f.b); badNodes.Add(f.c); }
                parent[Find(f.b)]=Find(f.a); parent[Find(f.c)]=Find(f.a);
            }
            var roots=new HashSet<int>();
            component=new int[nodes.Count];
            for(int i=0;i<nodes.Count;i++)
            {
                component[i]=Find(i);roots.Add(component[i]);
                if(cutTwins.Count==0 && Find(i)!=Find(0)) badNodes.Add(i);
            }
            islands=roots.Count;
            if(islands!=1 && cutTwins.Count==0) issues.Add($"{islands} connected components");
        }
    }
}
