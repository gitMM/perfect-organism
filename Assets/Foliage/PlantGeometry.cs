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
        public bool forked,tenPlants,adaptivePhysics;
        public int plantCount;
        public int PlantCount=>forked?(plantCount==100?100:tenPlants?10:1):1;
        internal const int LeavesPerShoot=10;
        public int GridColumns=>PlantCount==100?10:5;
        internal Vector3 PlantOrigin(int index)
        {
            if(PlantCount==1)return Vector3.zero;
            int columns=GridColumns,row=index/columns;
            return 1.28f*new Vector3(index%columns-(columns-1)*.5f+(row%2-.5f)*.35f,0,
                row-(PlantCount/columns-1)*.5f);
        }
        internal Vector3 OrbitPivot
        {
            get
            {
                int middle=(PlantCount/GridColumns/2-1)*GridColumns+(GridColumns-1)/2;
                // The larger circle passes between fixed trunk bases at ground level.
                return PlantCount==100?.5f*(PlantOrigin(middle)+PlantOrigin(middle+1)):
                    .5f*(PlantOrigin(middle)+PlantOrigin(middle+GridColumns+1));
            }
        }
        public static PlantSettings BranchTest=>new PlantSettings
        {
            seed=17,leaves=24,resolution=6,height=3.2f,radius=.044f,
            width=.18f,curvature=.32f,forked=true
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
        public struct Rod { public int a, b, part; public float radius,collisionRadius,woodRigidityGain; }
        public struct Face { public int a, b, c, part; }
        public struct SkinFace { public int a, b, c, rod, face; }
        public readonly List<Vector3> nodes = new();
        // A generated spray owns contiguous particles and rods. Its attachment stays on the skeleton.
        internal struct TwigCluster
        {
            public int anchor,firstNode,endNode,firstRod,endRod;
        }
        internal readonly List<TwigCluster> twigClusters=new();
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
        public readonly List<Vector2> bladeUV=new();
        public readonly List<SkinFace> surface = new();
        public readonly List<string> issues = new();
        public readonly HashSet<int> badNodes = new();
        public int islands { get; private set; }
        internal int[] component;
        internal readonly List<int> rootNodes=new();
        internal bool distributedStemSupport=true;
        internal readonly List<int> groundPins=new();
        internal int fixedCollarVertices;
        internal int leavesPerPlant;
        internal int coarsePlants;
        internal int PlantOfPart(int part)=>leavesPerPlant==0?0:part<=0?-part:(part-1)/(2*leavesPerPlant);
        public int ComponentOf(int node)=>component[node];
        public float minQuality { get; private set; }
        public static string Part(int id) => id <= 0 ? (id==0?"Stem":"Stem "+(1-id)) :
            ((id % 2 == 1 ? "Stalk " : "Leaf ") + ((id + 1) / 2));

        internal int Node(Vector3 p) { nodes.Add(p); thicknessRatio.Add(1f); return nodes.Count - 1; }
        int Vertex(Vector3 p, Color color,Vector2 uv=default)
        {
            skin.Add(p);
            colors.Add(color);bladeUV.Add(uv);
            return skin.Count - 1;
        }
        void Triangle(int a, int b, int c, int rod, int face)
        {
            surface.Add(new SkinFace { a = a, b = b, c = c, rod = rod, face = face });
        }

        // Continuous rings at joints along each curved stem/stalk.
        internal void Tube(List<int> path, int part, float radius, float taper,int sides=8)
        {
            if(reducedTubes)sides=part<=0?6:4;
            int start = skin.Count;
            Vector3 side = Vector3.right;
            Color color=part<=0?new Color(.31f,.255f,.15f):new Color(.37f,.45f,.18f);
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

        bool reducedTubes;
        float bladeTone;

        internal void Blade(int root, Vector3 direction, float length, PlantSettings s, int part,BladeSpawnClearance placement)
        {
            int firstFace=faces.Count,firstNode=nodes.Count,firstVertex=skin.Count;
            Vector3 side = Quaternion.AngleAxis(18f*Mathf.Sin(9*nodes[root].y+7*direction.z),direction)*Vector3.Cross(Vector3.up, direction).normalized;
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
                    Color margin=Color.Lerp(new Color(.095f,.29f,.13f),new Color(.35f,.54f,.16f),bladeTone);
                    Color rib=Color.Lerp(margin,new Color(.51f,.65f,.25f),.46f);
                    Color color=Color.Lerp(rib,margin,Mathf.Abs(u));
                    color*=Mathf.Lerp(.84f,1.06f,t);color.a=1;
                    vertexMap[node] = Vertex(nodes[node],color,new Vector2(u,2+t));
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
            // Adjust the shoot tip before its tube and the rod/collar rest frames are built.
            float lift=placement.Place(nodes,faces,firstFace);
            if(lift!=0)
            {
                Vector3 delta=Vector3.up*lift;
                nodes[root]+=delta;
                for(int i=firstNode;i<nodes.Count;i++)nodes[i]+=delta;
                for(int i=firstVertex;i<skin.Count;i++)skin[i]+=delta;
            }
        }

        public static PlantGeometry Generate(PlantSettings s)
        {
            var g = new PlantGeometry();
            int count=s.PlantCount;
            g.leavesPerPlant=s.leaves*PlantSettings.LeavesPerShoot;
            var detailed=new HashSet<int>();
            bool adaptive=s.adaptivePhysics && count>1;

            if(adaptive)
            {
                var order=new int[count];
                var distances=new float[count];
                for(int i=0;i<count;i++)
                {
                    var source=new PlantGeometry();
                    source.AppendPlant(s,i,s.PlantOrigin(i),Clearance(),false);
                    distances[i]=source.BladeDistanceToOrbit(s.OrbitPivot,PlantContact.OrbitRadius(count));
                    order[i]=i;
                }
                Array.Sort(distances,order);
                // Spend the shell budget on plants with blades closest to the swept capsule axis.
                int detailedCount=count==100?2:4;
                for(int i=0;i<detailedCount;i++)detailed.Add(order[i]);
            }
            for(int i=0;i<count;i++)
            {
                if(adaptive && !detailed.Contains(i))
                {
                    var source=new PlantGeometry{reducedTubes=true};
                    source.AppendPlant(s,i,s.PlantOrigin(i),Clearance());
                    g.AppendCoarsePlant(source,i,s);
                }
                else g.AppendPlant(s,i,s.PlantOrigin(i),Clearance());
            }
            BladeSpawnClearance Clearance()=>new BladeSpawnClearance(s.height/Mathf.Max(8,2*s.resolution));
            g.Validate();
            return g;
        }

        float BladeDistanceToOrbit(Vector3 pivot,float radius)
        {
            float closest=float.PositiveInfinity;
            foreach(var face in faces)
            {
                var bounds=new Bounds(nodes[face.a]-pivot,Vector3.zero);
                bounds.Encapsulate(nodes[face.b]-pivot);
                bounds.Encapsulate(nodes[face.c]-pivot);
                var nearest=bounds.ClosestPoint(Vector3.zero);
                float farX=Mathf.Abs(bounds.center.x)+bounds.extents.x;
                float farZ=Mathf.Abs(bounds.center.z)+bounds.extents.z;
                float radial=Mathf.Max(0,Mathf.Sqrt(nearest.x*nearest.x+nearest.z*nearest.z)-radius,
                    radius-Mathf.Sqrt(farX*farX+farZ*farZ));
                float vertical=Mathf.Max(0,bounds.min.y-1.5f,.3f-bounds.max.y);
                closest=Mathf.Min(closest,radial*radial+vertical*vertical);
            }
            return closest;
        }

        // Each distant shoot and blade share an elastic beam. Loads reach the
        // branch junction and the pinned trunk.
        // Render the original rings and blades using the reduced rod material frames.
        void AppendCoarsePlant(PlantGeometry src,int plant,PlantSettings settings)
        {
            coarsePlants++;
            int firstLeaf=bladeRows.Count;
            int trunkSegments=0;
            while(trunkSegments<src.rods.Count && src.rods[trunkSegments].part<=0)trunkSegments++;
            var anchors=new SortedSet<int>{0,1,trunkSegments};
            for(int leaf=0;leaf<settings.leaves;leaf+=3)anchors.Add(src.joints[2*leaf]);
            var nodesBySource=new Dictionary<int,int>();
            int CopyNode(int id)
            {
                if(!nodesBySource.TryGetValue(id,out int copy))
                {
                    copy=Node(src.nodes[id]);
                    nodesBySource.Add(id,copy);
                }
                return copy;
            }
            int Beam(int a,int b,int part,float radius,float skin)
            {
                int id=rods.Count;
                rods.Add(new Rod{a=CopyNode(a),b=CopyNode(b),part=part,radius=radius,collisionRadius=skin});
                return id;
            }
            rootNodes.Add(CopyNode(0));
            var trunkMap=new int[trunkSegments];
            int previous=0;
            foreach(int next in anchors)
            {
                if(next==previous)continue;
                int id=Beam(previous,next,-plant,src.rods[next-1].radius,src.rods[previous].radius);
                for(int j=previous;j<next;j++)trunkMap[j]=id;
                previous=next;
            }
            var shoots=new int[src.bladeRows.Count];
            var forks=new int[(settings.leaves+2)/3];
            for(int leaf=0;leaf<settings.leaves;leaf+=3)
            {
                int baseNode=src.joints[2*leaf],fork=leaf+1<settings.leaves?src.joints[2*(leaf+1)]:src.rods.Find(r=>r.part==2*leaf+1).b;
                forks[leaf/3]=Beam(baseNode,fork,2*(firstLeaf+leaf)+1,settings.radius*.36f,0);
                for(int j=leaf;j<Mathf.Min(leaf+3,settings.leaves);j++)
                {
                    var rows=src.bladeRows[j];
                    int tip=rows[rows.Count-1][0];
                    var middle=rows[rows.Count/2];
                    float halfWidth=Vector3.Distance(src.nodes[middle[0]],src.nodes[middle[middle.Length-1]])*.5f;
                    int part=2*(firstLeaf+j)+1;
                    shoots[j]=Beam(fork,tip,part,settings.radius*(j==leaf?.36f:.23f),halfWidth);
                    int a=CopyNode(fork),b=CopyNode(tip);
                    bladeRows.Add(new List<int[]>{new[]{a},new[]{b}});
                    joints.Add(a);joints.Add(b);
                }
            }
            for(int j=settings.leaves;j<src.bladeRows.Count;j++)
            {
                int beam=shoots[(j-settings.leaves)/(PlantSettings.LeavesPerShoot-1)];
                shoots[j]=beam;
                var r=rods[beam];
                bladeRows.Add(new List<int[]>{new[]{r.a},new[]{r.b}});
                joints.Add(r.a);joints.Add(r.b);
            }
            int vertex=skin.Count;
            skin.AddRange(src.skin);colors.AddRange(src.colors);bladeUV.AddRange(src.bladeUV);
            foreach(var f in src.surface)
            {
                int beam;
                if(f.face>=0)beam=shoots[src.faces[f.face].part/2-1];
                else if(f.rod<trunkSegments)beam=trunkMap[f.rod];
                else
                {
                    var rod=src.rods[f.rod];int shoot=(rod.part-1)/2;
                    beam=shoot<settings.leaves && shoot%3==0 && rod.a==src.joints[shoot*2]?forks[shoot/3]:shoots[shoot];
                }
                Triangle(vertex+f.a,vertex+f.b,vertex+f.c,beam,-1);
            }
        }
        void AppendPlant(PlantSettings s,int plant,Vector3 origin,BladeSpawnClearance placement,bool addSideLeaves=true)
        {
            int firstShoot=bladeRows.Count,firstRod=rods.Count;
            var rng = new System.Random(unchecked(s.seed+plant));
            float phase = (float)rng.NextDouble()*2*Mathf.PI;
            bladeTone=.2f+.45f*(float)rng.NextDouble();
            int segments = Math.Max(s.leaves+2,s.resolution*2);
            var stem = new List<int>();
            for (int i=0;i<=segments;i++)
            {
                float t=(float)i/segments;
                stem.Add(Node(origin+s.height*new Vector3(.13f*s.curvature*t*Mathf.Sin(phase+3*t),
                    t,.22f*s.curvature*t*t*Mathf.Cos(phase+2*t))));
            }
            rootNodes.Add(stem[0]);
            Tube(stem,-plant,s.radius,.12f);
            if(s.forked)
            {
                ForkedBranches(stem,s,phase,placement);
            }
            else for (int i=0;i<s.leaves;i++)
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
                    stalk.Add(Node(nodes[root]+length*(.36f*d*u+Vector3.up*.22f*u*(1.4f-.4f*u))));
                }
                int end=stalk[count];
                Blade(end,d,length,s,2*i+2,placement);
                Tube(stalk,2*i+1,s.radius*.46f,.55f);
                joints.Add(root);
                joints.Add(end);
            }
            if(addSideLeaves)AddSideLeaves(s,firstShoot,firstRod,rng,placement);
        }

        void AddSideLeaves(PlantSettings settings,int firstShoot,int firstRod,System.Random rng,BladeSpawnClearance placement)
        {
            int endRod=rods.Count;
            int cursor=firstRod;
            float Random(float low,float high)=>Mathf.Lerp(low,high,(float)rng.NextDouble());
            for(int shoot=0;shoot<settings.leaves;shoot++)
            {
                int part=2*(firstShoot+shoot)+1;
                while(cursor<endRod && rods[cursor].part!=part)cursor++;
                int first=cursor;
                while(cursor<endRod && rods[cursor].part==part)cursor++;
                int segments=cursor-first;
                float phase=Random(-.35f,.35f);
                var terminal=bladeRows[firstShoot+shoot];
                float terminalLength=Vector3.Distance(nodes[terminal[0][0]],nodes[terminal[terminal.Count-1][0]]);
                for(int i=0;i<PlantSettings.LeavesPerShoot-1;i++)
                {
                    float t=(i+.6f)/(PlantSettings.LeavesPerShoot-1);
                    int support=first+Mathf.Min(segments-1,(int)(t*segments));
                    var segment=rods[support];
                    Vector3 axis=(nodes[segment.b]-nodes[segment.a]).normalized;
                    Vector3 side=Vector3.Cross(Vector3.up,axis).normalized;
                    float sign=i%2==0?1:-1;
                    float tilt=phase+Random(-.28f,.28f);
                    Vector3 direction=(axis*Random(.28f,.72f)+sign*side+
                        Vector3.up*(tilt+.35f*t-.20f)).normalized;
                    float length=terminalLength*Random(.42f,.78f)*(1-.25f*t);
                    int anchor=segment.a;
                    Vector3 insertion=Vector3.Lerp(nodes[anchor],nodes[segment.b],t*segments%1);
                    int firstFace=faces.Count,firstTriangle=surface.Count;
                    int baseNode=Node(insertion+direction*length*.18f);
                    int leafPart=2*bladeRows.Count+1;
                    var blade=settings;
                    blade.resolution=3;
                    blade.width=Random(.15f,.24f);
                    blade.curvature=Random(.18f,.47f);
                    bladeTone=Mathf.Clamp01(.18f+.4f*t+Random(-.14f,.3f));
                    Blade(baseNode,direction,length,blade,leafPart+1,placement);
                    // A tapered petiole is only a few millimetres across.
                    float radius=settings.radius*.075f;
                    Vector3 rim=Vector3.Cross(Vector3.up,nodes[baseNode]-nodes[anchor]).normalized*radius;
                    var bark=new Color(.32f,.42f,.16f);
                    int stem=rods.Count;
                    rods.Add(new Rod{a=anchor,b=baseNode,part=leafPart,radius=radius});
                    int v=Vertex(nodes[anchor]-rim,bark);
                    Vertex(nodes[anchor]+rim,bark);
                    Vertex(nodes[baseNode]-rim*.56f,bark);
                    Vertex(nodes[baseNode]+rim*.56f,bark);
                    Triangle(v,v+2,v+1,stem,-1);Triangle(v+1,v+2,v+3,stem,-1);
                    var crownOffset=nodes[baseNode]-settings.OrbitPivot;
                    float radial=Mathf.Sqrt(crownOffset.x*crownOffset.x+crownOffset.z*crownOffset.z);
                    float reach=length+.30f+.20f;
                    bool nearPlayer=Mathf.Abs(radial-PlantContact.OrbitRadius(settings.PlantCount))<=reach
                        && crownOffset.y<=1.50f+reach && crownOffset.y>=.30f-reach;
                    if(settings.adaptivePhysics && (!nearPlayer || i!=4))
                    {
                        // Bake the same blade mesh onto an independent elastic collision beam.
                        // The placement pass has already recorded its rest triangles.
                        var rows=bladeRows[bladeRows.Count-1];
                        Vector3 tip=nodes[rows[rows.Count-1][0]];
                        var mid=rows[1];
                        float halfWidth=.5f*Vector3.Distance(nodes[mid[0]],nodes[mid[mid.Length-1]]);
                        faces.RemoveRange(firstFace,faces.Count-firstFace);
                        nodes.RemoveRange(baseNode,nodes.Count-baseNode);
                        thicknessRatio.RemoveRange(baseNode,thicknessRatio.Count-baseNode);
                        int end,rodId;
                        if(nearPlayer)
                        {
                            end=Node(tip);rodId=rods.Count-1;
                            var beam=rods[rodId];beam.b=end;beam.collisionRadius=halfWidth;
                            beam.radius=settings.radius*.13f;rods[rodId]=beam;
                        }
                        else
                        {
                            rods.RemoveAt(rods.Count-1);
                            rodId=support;end=segment.b;
                        }
                        for(int tIndex=firstTriangle;tIndex<surface.Count;tIndex++)
                        {
                            var f=surface[tIndex];f.rod=rodId;f.face=-1;surface[tIndex]=f;
                        }
                        bladeRows[bladeRows.Count-1]=new List<int[]>{new[]{anchor},new[]{end}};
                        baseNode=end;
                    }
                    joints.Add(anchor);joints.Add(baseNode);
                }
            }
        }

        void ForkedBranches(List<int> stem,PlantSettings s,float phase,BladeSpawnClearance placement)
        {
            int leafLimit=bladeRows.Count+s.leaves;
            int tiers=(s.leaves+2)/3;
            for(int tier=0;tier<tiers;tier++)
            {
                float level=(tier+.5f)/tiers;
                bool upper=tier%2!=0;
                float branchHeight=Mathf.Lerp(.24f,.99f,level);
                int root=stem[Mathf.RoundToInt(branchHeight*(stem.Count-1))];
                float angle=phase+tier*2.39996f+.20f*Mathf.Sin(phase+tier);
                Vector3 outward=new Vector3(Mathf.Cos(angle),0,Mathf.Sin(angle));
                Vector3 side=Vector3.Cross(Vector3.up,outward);
                float reach=s.height*(.19f+.14f*Mathf.Sin(Mathf.PI*level)-.065f*level);
                Vector3 Center(float t)=>nodes[root]+reach*(t*outward+Vector3.up*((upper?.35f:.36f)*t+.15f*t*t));
                var primary=new List<int>{root};
                for(int j=1;j<=4;j++)primary.Add(Node(Center(j/4f)));
                void AddShoot(List<int> path,float radius,Vector3 direction)
                {
                    int part=2*bladeRows.Count+1,end=path[path.Count-1];
                    Blade(end,direction,reach*.48f,s,part+1,placement);
                    Tube(path,part,radius,.68f);joints.Add(path[0]);joints.Add(end);
                }
                AddShoot(primary,s.radius*.36f,outward);
                // Forks bow away from the parent, then approach it near the tips.
                // The three blades fan outward to leave the contact rods accessible.
                for(int sign=-1;sign<=1 && bladeRows.Count<leafLimit;sign+=2)
                {
                    var fork=new List<int>{primary[1]};
                    for(int j=1;j<=4;j++)
                    {
                        float u=j/4f,t=Mathf.Lerp(.25f,.94f,u);
                        float offset=reach*(.13f*Mathf.Sin(Mathf.PI*u)+.38f*u*u);
                        fork.Add(Node(Center(t)+sign*side*offset+Vector3.up*(s.radius*.2f*u)));
                    }
                    AddShoot(fork,s.radius*.23f,(outward*.52f+sign*side).normalized);
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
            var anchored=new HashSet<int>();
            foreach(int root in rootNodes)anchored.Add(Find(root));
            if(anchored.Count==0 && nodes.Count>0)anchored.Add(Find(0));
            component=new int[nodes.Count];
            for(int i=0;i<nodes.Count;i++)
            {
                component[i]=Find(i);roots.Add(component[i]);
                if(cutTwins.Count==0 && !anchored.Contains(component[i])) badNodes.Add(i);
            }
            islands=roots.Count;
            if(islands!=Math.Max(1,rootNodes.Count) && cutTwins.Count==0) issues.Add($"{islands} connected components");
        }
    }
}
