using UnityEngine;

namespace Foliage
{
    public partial class FoliageLab
    {
        public VbdSheet Sheet {get; private set;}
        int inspectedFace=-1, sheetTab;
        Vector3 inspectedWeights;
        Color[] sheetColors;
        float[] strains;
        int leafIndex, leafMap;
        bool showRest,showVein=true;
        static readonly string[] leafMaps={"Surface","Strain","Bending","Motion"};
        bool showEdges=true,showPins=true,showGroups,showVelocity,showForce,showParticles,showEnergy;
        bool sphereWasActive,expandBranchSettings;
        public void SetSheetMode(bool enabled)
        {
            if(enabled && Sheet!=null && Sheet.leaf==null && Sheet.plant==null || !enabled && Sheet==null) return;
            if(enabled)
            {
                RestoreGeometry();
                SetSurface(new VbdSheet());
                pivot=Vector3.zero; distance=.43f; yaw=-15; pitch=6;
                PlaceCamera();
            }
            else
            {
                RestoreGeometry();
                ClearObstacle();
                Sheet.Dispose(); Sheet=null;crossSheet=graphSheet=null; UpdateBranchFramePacing();
                testSphere.gameObject.SetActive(sphereWasActive);
                Rebuild(); Frame();
            }
        }
        void SetSurface(VbdSheet surface)
        {
            ClearObstacle();
            if(Sheet==null) sphereWasActive=testSphere.gameObject.activeSelf;
            Sheet?.Dispose(); crossSheet=graphSheet=null;Sheet=surface; UpdateBranchFramePacing(); testSphere.gameObject.SetActive(false);
            sheetColors=new Color[Sheet.Count]; strains=new float[Sheet.Count];
            mesh.Clear();
            if(Sheet.plant!=null)
            {
                mesh.SetVertices(Geometry.skin); mesh.triangles=Sheet.plant.skinTriangles; mesh.SetColors(Geometry.colors);mesh.SetUVs(0,Geometry.bladeUV);
                if(settings.PlantCount>1)Sheet.BindRenderMesh(mesh);
            }
            else { mesh.vertices=Sheet.positions; mesh.triangles=Sheet.triangles; }
            ResetState();
        }
        public void OpenLeaf(int index)
        {
            RestoreGeometry();
            leafIndex=Mathf.Clamp(index,0,Geometry.bladeRows.Count-1);
            var leafGeometry=Geometry;
            int localIndex=leafIndex;
            if(Geometry.bladeRows[leafIndex][1].Length<3)
            {
                var full=settings;
                int plant=leafIndex/Geometry.leavesPerPlant;
                localIndex=leafIndex%Geometry.leavesPerPlant;
                full.seed=unchecked(full.seed+plant);
                full.tenPlants=false;full.plantCount=1;full.adaptivePhysics=false;
                leafGeometry=PlantGeometry.Generate(full);
            }
            var blade=new LeafSurface(leafGeometry,localIndex);
            SetSurface(new VbdSheet(blade));
            Vector3 axis=blade.rest[blade.rest.Length-1]-blade.rest[0];
            pivot=blade.rest[0]+axis*.48f-Vector3.up*blade.length*.08f;
            yaw=Mathf.Atan2(-axis.z,axis.x)*Mathf.Rad2Deg;
            pitch=38; distance=blade.length*1.75f;
            PlaceCamera();
        }
        void RefreshSheetMesh()
        {
            if(Sheet.plant!=null)
            {
                if(obstacleObject!=null) obstacleObject.transform.position=Sheet.contact.center;
                if(Sheet.DrawSkinOnGpu())
                {
                    if(Sheet.Steps>0)mesh.bounds=Sheet.SkinBounds;
                    if(sheetTab!=0 || (debugRendering && (directors || rodMap!=0 || collarErrors)))
                        Sheet.plant.UpdateSkin(Sheet.positions);
                }
                else
                {
                    Sheet.plant.UpdateSkin(Sheet.positions);
                    mesh.vertices=Sheet.plant.skin;mesh.RecalculateNormals();mesh.RecalculateBounds();
                }
                return;
            }
            mesh.vertices=Sheet.positions;
            if(debugRendering && Sheet.leaf!=null && leafMap==1)
            {
                System.Array.Clear(strains,0,strains.Length);
                foreach(var edge in Sheet.edges)
                {
                    float strain=Mathf.Abs(Vector3.Distance(Sheet.positions[edge.x],Sheet.positions[edge.y])/
                        Vector3.Distance(Sheet.rest[edge.x],Sheet.rest[edge.y])-1);
                    strains[edge.x]=Mathf.Max(strains[edge.x],strain); strains[edge.y]=Mathf.Max(strains[edge.y],strain);
                }
            }
            for(int i=0;i<sheetColors.Length;i++)
            {
                Color c=Sheet.leaf==null?new Color(.38f,.72f,.64f):
                    Sheet.leaf.midrib[i]?new Color(.44f,.62f,.22f):new Color(.17f,.44f,.20f);
                if(debugRendering && showGroups && Sheet.colors[i]>=0)
                    c=Color.HSVToRGB((float)Sheet.colors[i]/Sheet.colorCount,.6f,.95f);
                if(debugRendering && showEnergy)
                {
                    var e=Sheet.samples[i].energy;
                    c=Color.Lerp(new Color(.12f,.23f,.48f),Color.yellow,Mathf.Clamp01((e.x+e.y)/.00001f));
                }
                if(debugRendering && Sheet.leaf!=null && leafMap!=0)
                {
                    float fraction=leafMap==1?strains[i]/.05f:leafMap==2?Sheet.samples[i].energy.y/.00025f:
                        Vector3.Distance(Sheet.rest[i],Sheet.positions[i])/(Sheet.leaf.length*.5f);
                    c=Color.Lerp(new Color(.10f,.23f,.52f),Color.yellow,Mathf.Clamp01(fraction));
                }
                sheetColors[i]=c;
            }
            mesh.colors=sheetColors; mesh.RecalculateNormals(); mesh.RecalculateBounds();
        }
        public bool BeginSheetPick(Ray ray)
        {
            if(Sheet.plant!=null) return BeginPlantGrab(ray);
            inspectedFace=-1; grabbing=false; Sheet.grabFace=-1;
            float nearest=float.PositiveInfinity;
            int[] indices=Sheet.triangles;
            for(int i=0;i<indices.Length;i+=3)
            {
                if(!Intersect(ray,Sheet.positions[indices[i]],Sheet.positions[indices[i+1]],
                    Sheet.positions[indices[i+2]],out float t,out var w) || t>=nearest) continue;
                nearest=t; inspectedFace=i/3; inspectedWeights=w;
            }
            if(inspectedFace<0) return false;
            Sheet.grabFace=inspectedFace; Sheet.weights=inspectedWeights;
            Sheet.target=Sheet.Point(inspectedFace,inspectedWeights);
            depth=view.WorldToScreenPoint(Sheet.target).z; grabbing=true;
            return true;
        }
        public void MoveSheetTarget(Vector2 mouse,float wheel)
        {
            depth=Mathf.Clamp(depth*Mathf.Exp(wheel*.08f),.035f,40);
            Sheet.target=view.ScreenToWorldPoint(new Vector3(mouse.x,mouse.y,depth));
        }
        public void EndDrag()
        {
            grabbing=false; movingSphere=false;
            if(Sheet!=null) Sheet.grabFace=Sheet.grabRod=-1;
        }
        void DrawSheetPanel()
        {
            sheetTab=GUILayout.Toolbar(sheetTab,new[]{"Solver","Inspect"});
            scrollPos=GUILayout.BeginScrollView(scrollPos);
            bool blade=Sheet.leaf!=null || Sheet.plant!=null;
            if(Sheet.leaf!=null)
            {
                GUILayout.BeginHorizontal();
                if(GUILayout.Button("‹")) OpenLeaf((leafIndex+Geometry.bladeRows.Count-1)%Geometry.bladeRows.Count);
                GUILayout.Label($"Leaf {leafIndex+1} / {Geometry.bladeRows.Count}");
                if(GUILayout.Button("›")) OpenLeaf((leafIndex+1)%Geometry.bladeRows.Count);
                GUILayout.EndHorizontal();
            }
            GUILayout.Label($"{Sheet.Count} nodes   ·   {Sheet.triangles.Length/3} faces\nTick {Sheet.Steps}    |    1/60 s");
            GUILayout.BeginHorizontal();
            if(GUILayout.Button(paused?"Resume":"Pause")) paused=!paused;
            if(GUILayout.Button("Step")) { paused=true; Advance(); }
            if(GUILayout.Button("Reset")) ResetState();
            GUILayout.EndHorizontal();
            if(Sheet.badVertex>=0) GUILayout.Label($"Non-finite solver value at node {Sheet.badVertex}");
            if(Sheet.plant!=null)
            {
                int preset=settings.PlantCount==100?3:settings.tenPlants?2:settings.forked?1:0;
                int next=GUILayout.Toolbar(preset,new[]{"Single stem","Branch test","10 plants","100 + capsule"});
                if(next!=preset)
                {
                    if(next==3)StartPlayerCapsuleTest(100);
                    else SetBranchTest(next!=0,next==2);
                }
                if(settings.forked)GUILayout.Label($"Branch contacts   {Sheet.selfContact.branchContacts}    Blade contacts   {Sheet.selfContact.bladeContacts}\nPull an outer fork toward its middle branch.");
                DrawCutPanel();
            }
            if(Sheet.contact!=null)DrawObstaclePanel(sheetTab!=0);
            if(sheetTab==0 && Sheet.plant!=null && settings.forked)
            {
                if(GUILayout.Button(expandBranchSettings?"Hide solver settings":"Show solver settings"))
                    expandBranchSettings=!expandBranchSettings;
                if(!expandBranchSettings)
                {
                    GUILayout.Label("Left drag to pull. Scroll while holding to move in depth. Release to detach.");
                    GUILayout.EndScrollView();
                    return;
                }
            }
            if(sheetTab==0)
            {
                speed=Slider("Clock speed",speed,.1f,4);
                if(blade) Sheet.gravityScale=Slider("Gravity scale",Sheet.gravityScale,0,1);
                Sheet.iterations=Mathf.RoundToInt(Slider("Sweeps",Sheet.iterations,1,VbdSheet.MaxSweeps,"0"));
                Sheet.stretch=Slider("Edge stiffness (N/m)",Sheet.stretch,.1f,blade?250:30);
                Sheet.bend=Slider("Hinge stiffness (N)",Sheet.bend,0,blade?.3f:.0005f,"0.00000");
                if(blade) Sheet.veinRatio=Slider("Midrib stiffness multiplier",Sheet.veinRatio,1,16,"0.0");
                if(Sheet.plant!=null) Sheet.rodStiffness=Slider("Rod stiffness multiplier",Sheet.rodStiffness,.25f,4);
                Sheet.dampingTime=Slider("Material damping (s)",Sheet.dampingTime,0,.06f,"0.000");
                Sheet.grabK=Slider("Grab stiffness (N/m)",Sheet.grabK,.1f,20);
                Sheet.grabD=Slider("Grab damping (N s/m)",Sheet.grabD,0,.1f,"0.000");
                Sheet.grabLimit=Slider("Grab force limit (N)",Sheet.grabLimit,.005f,blade?2:.3f,"0.000");
                GUILayout.Label("Left drag to pull. Scroll while holding to move in depth. Release to detach.");
            }
            else
            {
                if(Sheet.plant!=null) DrawRodInspector();
                if(Sheet.leaf!=null)
                {
                    showRest=GUILayout.Toggle(showRest,"Rest shape");
                    showVein=GUILayout.Toggle(showVein,$"Midrib stiffness   ×{Sheet.veinRatio:F1}");
                    int next=GUILayout.SelectionGrid(leafMap,leafMaps,2);
                    if(next!=leafMap) { leafMap=next; RefreshSheetMesh(); }
                    if(leafMap>0) GUILayout.Label(leafMap==1?"Blue 0 → yellow 5% edge strain":leafMap==2?
                        "Blue 0 → yellow 250 µJ bending/node":$"Blue 0 → yellow {Sheet.leaf.length*.5f:F3} m from rest");
                }
                showEdges=GUILayout.Toggle(showEdges,"Triangle edges"); showPins=GUILayout.Toggle(showPins,"Pinned edge");
                showParticles=GUILayout.Toggle(showParticles,"Node positions");
                if(Sheet.plant==null)
                {
                    bool groups=GUILayout.Toggle(showGroups,$"Update groups ({Sheet.colorCount} colors)");
                    bool energy=GUILayout.Toggle(showEnergy,"Energy   blue 0 → yellow 10 µJ/node");
                    if(groups!=showGroups || energy!=showEnergy){showGroups=groups;showEnergy=energy;RefreshSheetMesh();}
                }
                showVelocity=GUILayout.Toggle(showVelocity,"Velocity vectors");
                showForce=GUILayout.Toggle(showForce,"Applied force vectors");
                interaction=GUILayout.Toggle(interaction,"Grab, target and force");
                debugScale=Slider("Vector scale",debugScale,.25f,3);
                GUILayout.Label($"Elastic energy (µJ)\nStretch {Sheet.elasticEnergy.x*1e6f:G4}    Bend {Sheet.elasticEnergy.y*1e6f:G4}");
                if(inspectedFace>=0)
                {
                    int v=Sheet.triangles[3*inspectedFace+(inspectedWeights.y>inspectedWeights.x && inspectedWeights.y>inspectedWeights.z?1:inspectedWeights.z>inspectedWeights.x?2:0)];
                    var s=Sheet.samples[v];
                    GUILayout.Label($"Face {inspectedFace}    weights {inspectedWeights:F2}\nNode {v}    group {Sheet.colors[v]}\nx {s.position:F4} m\nv {s.velocity:F4} m/s\nF {s.force:F4} N");
                }
                if(Sheet.grabFace>=0) GUILayout.Label($"Grab {Sheet.appliedGrab.magnitude:F4} N    error {Vector3.Distance(Sheet.target,Sheet.Point(Sheet.grabFace,Sheet.weights)):F4} m");
                GUILayout.Label("Peak free-node residual (N) by sweep");
                for(int i=0;i<Sheet.iterations;i++) GUILayout.Label($"{i+1,2}     {Sheet.residual[i]:G4}");
            }
            GUILayout.EndScrollView();
        }
        Material debugCrosses;
        ComputeBuffer crossPoints,pinPoints;
        readonly System.Collections.Generic.List<Vector3> pinnedPositions=new();
        int contactTick=-1,pinTick=-1;
        VbdSheet crossSheet;
        void DrawCrosses(System.Collections.Generic.List<Vector3> points,ref ComputeBuffer buffer,ref int uploadedTick,float radius,Color tint)
        {
            if(points.Count==0)return;
            if(!debugCrosses)debugCrosses=new Material(Resources.Load<Shader>("PlantDebugCrosses"));
            if(buffer==null || buffer.count<points.Count)
            {
                buffer?.Release();buffer=new ComputeBuffer(Mathf.NextPowerOfTwo(points.Count),12);
                uploadedTick=-1;
            }
            if(uploadedTick!=Sheet.Steps)
            {
                buffer.SetData(points);uploadedTick=Sheet.Steps;
            }
            debugCrosses.SetBuffer("Points",buffer);
            debugCrosses.SetFloat("Radius",radius*debugScale);
            debugCrosses.SetColor("Tint",tint);
            debugCrosses.SetPass(0);
            Graphics.DrawProceduralNow(MeshTopology.Lines,6,points.Count);
        }
        void DrawPlantCrosses()
        {
            if(crossSheet!=Sheet)
            {
                crossSheet=Sheet;pinTick=contactTick=-1;
            }
            if(showContacts && Sheet.selfContact.enabled)
                DrawCrosses(Sheet.selfContact.points,ref crossPoints,ref contactTick,.006f,Color.magenta);
            if(showPins)
            {
                if(pinTick!=Sheet.Steps)
                {
                    pinnedPositions.Clear();
                    foreach(int v in Sheet.pins)pinnedPositions.Add(Sheet.positions[v]);
                }
                DrawCrosses(pinnedPositions,ref pinPoints,ref pinTick,.002f,Color.yellow);
            }
        }

        Mesh graphLines;
        VbdSheet graphSheet;
        int graphTick=-1;
        bool DrawGraphLines()
        {
            if(settings.PlantCount<=1 || Sheet.plant==null)return false;
            if(!showEdges && (!centers || rodMap!=0))return true;
            if(graphSheet!=Sheet)
            {
                if(!graphLines)graphLines=new Mesh{name="Plant graph lines",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
                graphLines.Clear();
                graphLines.MarkDynamic();
                graphLines.vertices=Sheet.positions;
                graphLines.subMeshCount=2;
                var edgeIndices=new int[2*Sheet.edges.Count];
                for(int i=0;i<Sheet.edges.Count;i++){edgeIndices[2*i]=Sheet.edges[i].x;edgeIndices[2*i+1]=Sheet.edges[i].y;}
                var rodIndices=new int[2*Geometry.rods.Count];
                for(int i=0;i<Geometry.rods.Count;i++){rodIndices[2*i]=Geometry.rods[i].a;rodIndices[2*i+1]=Geometry.rods[i].b;}
                graphLines.SetIndices(edgeIndices,MeshTopology.Lines,0,false);
                graphLines.SetIndices(rodIndices,MeshTopology.Lines,1,false);
                graphSheet=Sheet;graphTick=Sheet.Steps;
            }
            if(graphTick!=Sheet.Steps)
            {
                graphLines.SetVertices(Sheet.positions,0,Sheet.Count,UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds);
                graphTick=Sheet.Steps;
            }
            void Draw(int submesh,Color color)
            {
                lines.SetColor("_Color",color);
                lines.SetPass(0);
                Graphics.DrawMeshNow(graphLines,Matrix4x4.identity,submesh);
            }
            if(centers && rodMap==0)Draw(1,Color.cyan);
            if(showEdges)Draw(0,new Color(.18f,.35f,.35f));
            lines.SetColor("_Color",Color.white);
            return true;
        }
        void DrawSheetOverlay()
        {
            lines.SetInt("_ZTest",(int)UnityEngine.Rendering.CompareFunction.Always);
            bool batched=DrawGraphLines();
            if(batched)DrawPlantCrosses();
            lines.SetPass(0); GL.PushMatrix(); GL.Begin(GL.LINES);
            if(Sheet.plant!=null)DrawRodOverlay(batched);
            if(Sheet.contact!=null) DrawContactOverlay(batched);
            if(showEdges && !batched)
            {
                GL.Color(new Color(.18f,.35f,.35f));
                foreach(var e in Sheet.edges) Line(Sheet.positions[e.x],Sheet.positions[e.y]);
            }
            if(Sheet.leaf!=null)
            {
                foreach(var edge in Sheet.edges)
                {
                    if(showRest) { GL.Color(new Color(.55f,.66f,.9f)); Line(Sheet.rest[edge.x],Sheet.rest[edge.y]); }
                    if(showVein && Sheet.leaf.midrib[edge.x] && Sheet.leaf.midrib[edge.y])
                    { GL.Color(new Color(1,.8f,.24f)); Line(Sheet.positions[edge.x],Sheet.positions[edge.y]); }
                }
            }
            if(showParticles || showVelocity || showForce || (!batched && showPins))
            for(int v=0;v<Sheet.Count;v++)
            {
                var s=Sheet.samples[v]; var p=s.position;
                if(showParticles){ GL.Color(Color.white); Cross(p,.00065f*debugScale); }
                if(!batched && showPins && Sheet.colors[v]<0){ GL.Color(Color.yellow); Cross(p,.002f*debugScale); }
                if(showVelocity){ GL.Color(Color.cyan); Line(p,p+s.velocity*.05f*debugScale); }
                if(showForce){ GL.Color(Color.magenta); Line(p,p+s.force*debugScale); }
            }
            if(interaction && inspectedFace>=0)
            {
                var p=Sheet.Point(inspectedFace,inspectedWeights);
                GL.Color(Color.yellow); Cross(p,.002f);
                if(Sheet.grabFace>=0)
                {
                    GL.Color(Color.white); Line(p,Sheet.target); Cross(Sheet.target,.002f);
                    GL.Color(Color.red); Line(p,p+Sheet.appliedGrab*debugScale);
                }
            }
            if(Sheet.plant!=null)DrawCutOverlay();
            GL.End(); GL.PopMatrix(); lines.SetInt("_ZTest",(int)UnityEngine.Rendering.CompareFunction.LessEqual);
        }
    }
}
