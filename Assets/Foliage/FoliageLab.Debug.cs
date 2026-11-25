using UnityEngine;

namespace Foliage
{
    public partial class FoliageLab
    {
        const float PanelWidth=254;
        float UiScale => Mathf.Clamp(Mathf.Min(Screen.height/590f,Screen.width/680f),.48f,1.25f);
        Vector2 scrollPos;
        int tab;
        static readonly string[] tabs={"Plant","Inspect","Clock"};
        static readonly string[] picks={"Element","Node","Part","Plant"};
        bool wire,vertices,normalView,winding,connectivity,attachments,ids,invalid=true,interaction=true;
        bool debugRendering=true;
        void SetDebugRendering(bool enabled)
        {
            if(debugRendering==enabled)return;
            debugRendering=enabled;
            if(Sheet!=null && Sheet.plant==null)RefreshSheetMesh();
        }
        const string TenCapsuleTest="10 plants + circling player";
        const string HundredCapsuleTest="100 plants + circling player";
        void OpenCapsulePreset(int count)
        {
            sheetTab=0;
            expandBranchSettings=false;
            StartPlayerCapsuleTest(count);
        }
        float debugScale=1;
        string seedText;
        static float Slider(string name,float value,float min,float max,string format="0.00")
        {
            GUILayout.Label(name+"   "+value.ToString(format));
            return GUILayout.HorizontalSlider(value,min,max);
        }
        void OnGUI()
        {
            if(Geometry==null || CompactBranchPanel) return;
            float scale=UiScale;
            GUI.matrix=Matrix4x4.Scale(new Vector3(scale,scale,1));
            // The camera only clears its viewport; explicitly clear the inspector on each repaint.
            GUI.color=new Color(.10f,.12f,.13f);
            GUI.DrawTexture(new Rect(0,0,PanelWidth,Screen.height/scale),Texture2D.whiteTexture);
            GUI.color=Color.white;
            GUILayout.BeginArea(new Rect(0,0,PanelWidth,Screen.height/scale),GUI.skin.box);
            GUILayout.Label("<b>FOLIAGE  /  TEST SCENE</b>");
            if(jungleFig)
            {
                GUILayout.Label("Jungle fig   /   Seed " + jungleFig.seed);
                if(GUILayout.Button(jungleFig.simulationPaused?"Resume physics":"Pause physics"))
                    jungleFig.simulationPaused=!jungleFig.simulationPaused;
                jungleFig.SetDebugVisible(GUILayout.Toggle(jungleFig.DebugVisible,"Tree solver debug view"));
                if(jungleFig.DebugVisible)GUILayout.Label(FigBranchInstance.DebugLegend);
                if(GUILayout.Button("Frame tree"))FrameFig();
                if(GUILayout.Button("Back to lab"))CloseJungleFig();
                GUILayout.Label("Left drag a leaf or branch to pull.\nRight drag to orbit. Scroll to zoom.\nMiddle drag to pan.");
                GUILayout.EndArea();GUI.matrix=Matrix4x4.identity;return;
            }
            if(jungleFigPrefab && GUILayout.Button("Jungle fig")){OpenJungleFig();GUIUtility.ExitGUI();}
            int current=Sheet==null?0:Sheet.plant!=null?3:Sheet.leaf==null?1:2;
            int workspace=GUILayout.Toolbar(current,new[]{"Plant","Sheet","Leaf","Stems"});
            if(workspace!=current)
            {
                if(workspace==3) OpenStems();
                else if(workspace==2) OpenLeaf(selected.valid && selected.part>=2 && selected.part%2==0 ? selected.part/2-1 : leafIndex);
                else SetSheetMode(workspace==1);
            }
            SetDebugRendering(GUILayout.Toggle(debugRendering,"Debug rendering"));
            if(GUILayout.Button(TenCapsuleTest)) { OpenCapsulePreset(10);GUIUtility.ExitGUI(); }
            if(GUILayout.Button(HundredCapsuleTest)) { OpenCapsulePreset(100);GUIUtility.ExitGUI(); }
            if(Sheet!=null)
            {
                DrawSheetPanel();
                GUILayout.EndArea(); GUI.matrix=Matrix4x4.identity;
                return;
            }
            tab=GUILayout.Toolbar(tab,tabs);
            scrollPos=GUILayout.BeginScrollView(scrollPos);
            if(tab==0)
            {
                PlantSettings before=settings;
                GUILayout.Label("Generator seed");
                seedText=GUILayout.TextField(seedText??settings.seed.ToString());
                if(int.TryParse(seedText,out int seed)) settings.seed=seed;
                settings.height=Slider("Height (m)",settings.height,.6f,4);
                settings.radius=Slider("Stem radius (m)",settings.radius,.012f,.065f,"0.000");
                settings.leaves=Mathf.RoundToInt(Slider("Leafy shoots",settings.leaves,1,settings.forked?24:16,"0"));
                settings.width=Slider("Blade width / length",settings.width,.08f,.48f);
                settings.curvature=Slider("Curvature",settings.curvature,0,1);
                settings.resolution=Mathf.RoundToInt(Slider("Resolution",settings.resolution,4,20,"0"));
                if(!settings.Equals(before)) Rebuild();
                GUILayout.BeginHorizontal();
                if(GUILayout.Button("Regenerate")) Rebuild();
                if(GUILayout.Button("Next seed")) { settings.seed=unchecked(settings.seed+1); seedText=settings.seed.ToString(); Rebuild(); }
                GUILayout.EndHorizontal();
                if(GUILayout.Button("Frame plant")) Frame();
            }
            if(tab==1)
            {
                DrawSelection();
                debugScale=Slider("Overlay scale",debugScale,.25f,3);
                wire=GUILayout.Toggle(wire,"Render mesh edges");
                vertices=GUILayout.Toggle(vertices,"Topology nodes");
                normalView=GUILayout.Toggle(normalView,"Render vertex normals");
                winding=GUILayout.Toggle(winding,"Triangle orientation (front / back)");
                connectivity=GUILayout.Toggle(connectivity,"Rod and shell connectivity");
                ids=GUILayout.Toggle(ids,"Botanical component IDs");
                attachments=GUILayout.Toggle(attachments,"Shared attachment nodes");
                interaction=GUILayout.Toggle(interaction,"Pick and drag target");
                invalid=GUILayout.Toggle(invalid,"Highlight invalid geometry");
                GUILayout.Label($"Connected components   {Geometry.islands}\nMinimum triangle quality   {Geometry.minQuality:F3} / 1");
                foreach(var issue in Geometry.issues) GUILayout.Label(issue);
            }
            if(tab==2)
            {
                GUILayout.Label($"Simulation tick   {Tick}\nFixed step   1/60 s");
                GUILayout.BeginHorizontal();
                if(GUILayout.Button(paused?"Resume":"Pause")) paused=!paused;
                if(GUILayout.Button("Single step")) { paused=true; Advance(); }
                GUILayout.EndHorizontal();
                speed=Slider("Simulation speed",speed,.1f,4);
                if(GUILayout.Button("Reset simulation")) ResetState();
                bool sphere=GUILayout.Toggle(testSphere.gameObject.activeSelf,"Movable test sphere");
                testSphere.gameObject.SetActive(sphere);
                GUILayout.Label("Grab settings for the physics stage");
                grabStiffness=Slider("Stiffness",grabStiffness,0,500,"0");
                grabDamping=Slider("Damping",grabDamping,0,50);
                grabMaxForce=Slider("Force limit (N)",grabMaxForce,0,100);
            }
            GUILayout.Space(8);
            GUILayout.Label($"{Geometry.nodes.Count} nodes   {Geometry.rods.Count} rods   {Geometry.faces.Count} faces");
            if(tab!=1) DrawSelection();
            GUILayout.Space(6);
            GUILayout.Label("Left drag  ·  preview grab target\nWheel  ·  zoom / target depth\nRight drag  ·  orbit     Middle  ·  pan\nWASD  ·  fly     Q / E  ·  down / up");
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            if(debugRendering && ids)
            {
                LabelAt(Geometry.nodes[0],"0  Stem");
                for(int i=0;i<Geometry.joints.Count;i++)
                {
                    Vector3 p=Geometry.nodes[Geometry.joints[i]];
                    if(i%2==0) p=Vector3.Lerp(p,Geometry.nodes[Geometry.joints[i+1]],.5f);
                    LabelAt(p,$"{i+1}  {PlantGeometry.Part(i+1)}");
                }
            }
            GUI.matrix=Matrix4x4.identity;
        }
        void LabelAt(Vector3 p,string label)
        {
            var screen=view.WorldToScreenPoint(p);
            if(screen.z>0 && view.pixelRect.Contains(screen))
                GUI.Label(new Rect(screen.x/UiScale,(Screen.height-screen.y)/UiScale,110,22),label);
        }
        void DrawSelection()
        {
            var next=(PickMode)GUILayout.Toolbar((int)mode,picks);
            if(next!=mode) { mode=next; selected=default; grabbing=false; }
            if(!selected.valid) { GUILayout.Label("Click the plant to inspect."); return; }
            var h=selected;
            string name=mode==PickMode.Plant?"Plant 0":PlantGeometry.Part(h.part)+$"   ID {h.part}";
            if(h.node>=0) name+=$"\nNode {h.node}";
            else if(mode==PickMode.Element && h.rod>=0)
            {
                var r=Geometry.rods[h.rod];
                name+=$"\nRod {h.rod}   nodes {r.a}, {r.b}\nt = {h.along:F3}   length {(Geometry.nodes[r.a]-Geometry.nodes[r.b]).magnitude:F3} m";
            }
            else if(mode==PickMode.Element)
            {
                var f=Geometry.faces[h.face];
                name+=$"\nFace {h.face}   nodes {f.a}, {f.b}, {f.c}\nWeights {h.weights:F3}";
            }
            GUILayout.Label(name+$"\nPosition {h.Point(Geometry):F3} m");
            if(grabbing) GUILayout.Label($"Target offset   {Vector3.Distance(target,h.Point(Geometry)):F3} m");
        }
        static void Line(Vector3 a, Vector3 b) { GL.Vertex(a); GL.Vertex(b); }
        static void Cross(Vector3 p,float r)
        {
            Line(p-Vector3.right*r,p+Vector3.right*r);
            Line(p-Vector3.up*r,p+Vector3.up*r);
            Line(p-Vector3.forward*r,p+Vector3.forward*r);
        }
        static void Outline(Vector3 a,Vector3 b,Vector3 c) { Line(a,b); Line(b,c); Line(c,a); }
        void OnRenderObject()
        {
            if(jungleFig || !debugRendering || Camera.current!=view || Geometry==null) return;
            if(Sheet!=null) { DrawSheetOverlay(); return; }
            var g=Geometry;
            float r=.009f*debugScale;
            lines.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            if(wire || winding)
                foreach(var f in g.surface)
                {
                    Vector3 a=g.skin[f.a],b=g.skin[f.b],c=g.skin[f.c];
                    var n=Vector3.Cross(b-a,c-a).normalized;
                    bool front=Vector3.Dot(n,view.transform.position-a)>0;
                    GL.Color(winding?(front?Color.cyan:Color.magenta):new Color(.6f,.8f,.62f));
                    if(wire) Outline(a,b,c);
                    if(winding) Line((a+b+c)/3,(a+b+c)/3+n*3*r);
                }
            if(normalView)
            {
                GL.Color(Color.blue);
                for(int i=0;i<g.skin.Count;i++) Line(g.skin[i],g.skin[i]+normals[i]*3*r);
            }
            // Draw topology through the render skin so centre lines and shared nodes are visible.
            GL.End();
            lines.SetInt("_ZTest",(int)UnityEngine.Rendering.CompareFunction.Always);
            lines.SetPass(0); GL.Begin(GL.LINES);
            bool whole=selected.valid && mode==PickMode.Plant;
            bool part=selected.valid && mode==PickMode.Part;
            for(int i=0;i<g.rods.Count;i++)
            {
                var v=g.rods[i];
                bool highlight=whole || (part && selected.part==v.part) || (selected.valid && selected.rod==i && mode==PickMode.Element);
                if(!connectivity && !highlight) continue;
                GL.Color(highlight?Color.yellow:Color.cyan); Line(g.nodes[v.a],g.nodes[v.b]);
            }
            for(int i=0;i<g.faces.Count;i++)
            {
                var f=g.faces[i];
                bool highlight=whole || (part && selected.part==f.part) || (selected.valid && selected.face==i && mode==PickMode.Element);
                if(!connectivity && !highlight) continue;
                GL.Color(highlight?Color.yellow:new Color(.35f,.75f,.65f));
                Outline(g.nodes[f.a],g.nodes[f.b],g.nodes[f.c]);
            }
            if(vertices) { GL.Color(Color.white); foreach(var p in g.nodes) Cross(p,r*.5f); }
            if(attachments) { GL.Color(new Color(1,.5f,.15f)); foreach(int n in g.joints) Cross(g.nodes[n],r*2); }
            if(invalid) { GL.Color(Color.red); foreach(int n in g.badNodes) Cross(g.nodes[n],r*3); }
            if(interaction && selected.valid)
            {
                Vector3 p=selected.Point(g);
                GL.Color(Color.yellow); Cross(p,r*2);
                if(grabbing) { GL.Color(Color.white); Cross(target,r*2); Line(p,target); }
            }
            GL.End(); GL.PopMatrix();
            lines.SetInt("_ZTest",(int)UnityEngine.Rendering.CompareFunction.LessEqual);
        }
    }
}
