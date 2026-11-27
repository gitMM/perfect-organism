using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Foliage.Editor
{
    public sealed class FigBranchWindow : EditorWindow
    {
        [SerializeField] int seed=2;
        [SerializeField] FigSettings settings=new();
        readonly List<FigBranchInstance> branches=new();
        PreviewRenderUtility stage;
        Mesh wood;
        Material leaves,bark,soil;
        Mesh groundPlane;
        Vector3[] woodVertices;
        int[] woodTriangles;
        int focus;
        Vector3 pivot;
        float distance,yaw,pitch,grabDepth;
        FigBranchInstance grabbed;
        bool running=true,solverDebug;
        double clock,accumulator;

        [MenuItem("Tools/Foliage/Interactive Fig Branch")]
        public static void OpenSeedTwo()=>Open(2,new FigSettings());
        public static void Open(int seed,FigSettings settings){
            var window=GetWindow<FigBranchWindow>("Fig branch");
            window.seed=seed;window.settings=JsonUtility.FromJson<FigSettings>(JsonUtility.ToJson(settings));
            window.Build();window.Show();
        }
        void OnEnable(){
            EditorApplication.update+=UpdateStep;
            EditorApplication.playModeStateChanged+=ModeChanged;
        }
        void ModeChanged(PlayModeStateChange state){
            if(state==PlayModeStateChange.ExitingEditMode)Clear();
            if(state==PlayModeStateChange.EnteredEditMode)Repaint();
        }
        void OnDisable(){EditorApplication.update-=UpdateStep;EditorApplication.playModeStateChanged-=ModeChanged;Clear();}
        void Clear(){
            Release();
            foreach(var branch in branches){branch.Sleep();DestroyImmediate(branch.mesh);}
            branches.Clear();
            stage?.Cleanup();stage=null;
            if(wood)DestroyImmediate(wood);
            if(leaves)DestroyImmediate(leaves);
            if(soil)DestroyImmediate(soil);
        }
        void Build(){
            Clear();
            wood=FigBranchInstance.BuildTree(seed,settings,branches,validate:true);
            woodVertices=wood.vertices;woodTriangles=wood.triangles;
            bark=AssetDatabase.LoadAssetAtPath<Material>("Assets/Foliage/Fig Bark.mat");
            leaves=new Material(Resources.Load<Shader>("FigLeaf"));
            groundPlane=Resources.GetBuiltinResource<Mesh>("Plane.fbx");
            soil=new Material(Shader.Find("Standard")){color=new Color(.24f,.22f,.18f)};
            soil.SetFloat("_Glossiness",.08f);
            stage=new PreviewRenderUtility();
            stage.lights[0].color=Color.white;stage.lights[1].color=Color.white;
            stage.camera.clearFlags=CameraClearFlags.SolidColor;
            stage.camera.backgroundColor=new Color(.26f,.29f,.27f);
            stage.camera.fieldOfView=45;stage.camera.nearClipPlane=.025f;stage.camera.farClipPlane=200;
            stage.ambientColor=Color.gray;
            stage.lights[0].intensity=1.15f;stage.lights[0].transform.rotation=Quaternion.Euler(42,-35,0);
            stage.lights[1].intensity=.65f;stage.lights[1].transform.rotation=Quaternion.Euler(20,125,0);
            branches.Sort((a,b)=>a.Center.z.CompareTo(b.Center.z));
            focus=branches.FindIndex(b=>b.shoot.geometry.bladeRows.Count>5);
            FocusBranch(Mathf.Max(0,focus));
            FrameCrown();
            clock=EditorApplication.timeSinceStartup;
        }
        void FrameCrown(){
            var bounds=wood.bounds;
            foreach(var b in branches){
                var local=b.mesh.bounds;local.center+=b.shoot.origin;bounds.Encapsulate(local);
            }
            pivot=bounds.center;
            yaw=-16;pitch=7;
            float aspect=Mathf.Clamp(position.width/Mathf.Max(1,position.height-85),.4f,2);
            distance=bounds.extents.z+1.18f*Mathf.Max(bounds.extents.y,bounds.extents.x/aspect)/Mathf.Tan(22.5f*Mathf.Deg2Rad);
        }
        void FocusBranch(int index,bool leafDetail=false){
            if(branches.Count==0){pivot=wood.bounds.center;distance=40;return;}
            Release();focus=(index+branches.Count)%branches.Count;
            var b=branches[focus];
            var g=b.shoot.geometry;
            var cluster=new Bounds(g.nodes[g.bladeRows[0][0][0]],Vector3.zero);
            for(int i=0;i<Math.Min(5,g.bladeRows.Count);i++)
                foreach(var row in g.bladeRows[i])foreach(int node in row)cluster.Encapsulate(g.nodes[node]);
            if(!leafDetail){
                cluster=new Bounds(g.nodes[g.rods[1].a],Vector3.zero);
                for(int rod=1;rod<=6;rod++)cluster.Encapsulate(g.nodes[g.rods[rod].b]);
            }
            pivot=b.shoot.origin+cluster.center;
            var radial=new Vector3(pivot.x,0,pivot.z).normalized;
            yaw=Quaternion.LookRotation(radial==Vector3.zero?Vector3.forward:-radial).eulerAngles.y;
            pitch=18;distance=Mathf.Max(1,cluster.size.magnitude*1.70f/Mathf.Clamp(position.width/Mathf.Max(1,position.height-80),.5f,1));

        }
        void UpdateStep(){
            double now=EditorApplication.timeSinceStartup;
            accumulator+=Math.Min(.05,Math.Max(0,now-clock));clock=now;
            if(stage==null || !running){accumulator=0;return;}
            if(accumulator<1.0/60)return;
            accumulator%=1.0/60;
            bool changed=false;
            try{foreach(var branch in branches)changed|=branch.RuntimeTick();}
            catch{running=false;Release();throw;}
            if(changed)Repaint();
        }
        void Release(){grabbed?.ReleaseGrab();grabbed=null;}
        void OnLostFocus()=>Release();
        void OnGUI(){
            if(EditorApplication.isPlayingOrWillChangePlaymode){EditorGUILayout.HelpBox("Use the interactive fig prefab in Play Mode.",MessageType.Info);return;}
            if(stage==null)Build();
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label($"Seed {seed} � {branches.Count} physical boughs");
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if(GUILayout.Button("Previous",EditorStyles.toolbarButton))FocusBranch(focus-1);
            if(GUILayout.Button("Next branch",EditorStyles.toolbarButton))FocusBranch(focus+1);
            if(GUILayout.Button("Leaf detail",EditorStyles.toolbarButton))FocusBranch(focus,true);
            if(GUILayout.Button("Whole tree",EditorStyles.toolbarButton))FrameCrown();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            running=GUILayout.Toggle(running,"Simulate",EditorStyles.toolbarButton);
            solverDebug=GUILayout.Toggle(solverDebug,"Tree solver debug view",EditorStyles.toolbarButton);
            EditorGUILayout.EndHorizontal();
            if(solverDebug)GUILayout.Label(FigBranchInstance.DebugLegend);
            EditorGUILayout.HelpBox(grabbed==null?"Drag a bough, twig, or leaf. Right-drag to orbit. Scroll to zoom.":"Pulling crown � release to let the twig recover.",MessageType.None);
            Rect rect=GUILayoutUtility.GetRect(100,100,GUILayout.ExpandWidth(true),GUILayout.ExpandHeight(true));
            var q=Quaternion.Euler(pitch,yaw,0);
            var camera=stage.camera;
            camera.transform.SetPositionAndRotation(pivot-q*Vector3.forward*distance,q);
            camera.aspect=rect.width/Mathf.Max(1,rect.height);
            var e=Event.current;
            bool inside=rect.Contains(e.mousePosition);
            Vector3 uv=new((e.mousePosition.x-rect.x)/Mathf.Max(1,rect.width),1-(e.mousePosition.y-rect.y)/Mathf.Max(1,rect.height),0);
            var ray=camera.ViewportPointToRay(uv);
            if(e.type==EventType.MouseDown && inside && e.button==0){
                Grab(ray);e.Use();
            }else if(e.rawType==EventType.MouseUp){Release();}
            else if(e.type==EventType.MouseDrag && grabbed!=null){
                uv.z=grabDepth;grabbed.solver.target=camera.ViewportToWorldPoint(uv)-grabbed.shoot.origin;e.Use();
            }else if(inside && e.type==EventType.MouseDrag && e.button==1){
                yaw+=e.delta.x*.5f;pitch=Mathf.Clamp(pitch+e.delta.y*.4f,-80,80);e.Use();Repaint();
            }else if(inside && e.type==EventType.ScrollWheel){
                distance=Mathf.Clamp(distance*Mathf.Exp(e.delta.y*.08f),.15f,120);e.Use();Repaint();
            }
            if(e.type!=EventType.Repaint)return;
            stage.BeginPreview(rect,GUIStyle.none);
            stage.DrawMesh(groundPlane,Matrix4x4.TRS(Vector3.zero,Quaternion.FromToRotation(Vector3.up,(Vector3.up-settings.GroundGradient).normalized),new Vector3(16,1,16)),soil,0);
            stage.DrawMesh(wood,Matrix4x4.identity,bark,0);
            foreach(var b in branches){
                var matrix=Matrix4x4.Translate(b.shoot.origin);
                var debug=solverDebug?b.DebugMaterial(true):null;
                stage.DrawMesh(b.mesh,matrix,debug?debug:bark,0);
                stage.DrawMesh(b.mesh,matrix,debug?debug:leaves,1);
            }
            stage.camera.Render();GUI.DrawTexture(rect,stage.EndPreview());
        }

        // Exercise the same visible-face picker, solver target and skin upload as a mouse drag.
        [MenuItem("Tools/Foliage/Check Fig Leaf Pull")]
        public static void CheckSeedTwo(){
            OpenSeedTwo();
            var window=GetWindow<FigBranchWindow>();
            window.running=false;
            try{window.FocusBranch(window.focus,true);Debug.Log(window.MeasurePull());}
            finally{window.Release();window.FocusBranch(window.focus);
                foreach(var branch in window.branches)branch.solver.Reset();
                window.FrameCrown();window.running=true;window.Repaint();}
        }
        string MeasurePull(){
            var woodNormals=wood.normals;
            var woodGrain=new List<Vector3>();
            wood.GetUVs(0,woodGrain);
            foreach(var candidate in branches){
                var points=candidate.mesh.vertices;var normals=candidate.mesh.normals;
                var grain=new List<Vector3>();candidate.mesh.GetUVs(0,grain);
                var ring=candidate.shoot.FixedRingVertices;
                for(int k=0;k<ring.Count;k++){
                    int src=ring[k];
                    if((candidate.shoot.origin+points[k]-woodVertices[src]).sqrMagnitude>1e-10f ||
                        (normals[k]-woodNormals[src]).sqrMagnitude>1e-8f ||
                        (grain[k]-woodGrain[src]).sqrMagnitude>1e-10f)
                        throw new InvalidOperationException("Fig junction position, normal or grain mismatch.");
                }
            }
            var branch=branches[focus];
            var solver=branch.solver;
            var g=branch.shoot.geometry;
            var camera=stage.camera;
            Quaternion rotation=Quaternion.Euler(pitch,yaw,0);
            camera.transform.SetPositionAndRotation(pivot-rotation*Vector3.forward*distance,rotation);
            int leaf=-1;
            for(int f=0;f<g.faces.Count;f++){
                var point=solver.Point(f,Vector3.one/3)+branch.shoot.origin;
                var uv=camera.WorldToViewportPoint(point);
                if(uv.z<=0 || uv.x<0 || uv.x>1 || uv.y<0 || uv.y>1)continue;
                if(Grab(camera.ViewportPointToRay(uv)) && grabbed==branch && solver.grabFace==f){
                    leaf=g.faces[f].part/2-1;break;
                }
            }
            if(leaf<0)throw new InvalidOperationException("No exposed blade could be picked.");
            int collar=branch.rig.leafRod[leaf], twig=branch.rig.parent[collar];
            var rows=g.bladeRows[branch.shoot.neighboringLeaf[leaf]];
            int neighbor=rows[rows.Count-1][0],node=g.rods[twig].b;
            int root=g.rods[0].a,rootEnd=g.rods[0].b;
            float stemPeak=0,otherPeak=0,twistPeak=0,pinDrift=0;
            float maxGap=0;
            var initial=solver.target;
            for(int i=1;i<=72;i++){
                solver.target=initial+Mathf.Min(1,i/30f)*(.17f*camera.transform.right+.12f*camera.transform.up);
                branch.Step();
                for(int k=0;k<branch.shoot.FixedRingVertices.Count;k++)
                    maxGap=Mathf.Max(maxGap,Vector3.Distance(branch.shoot.origin+branch.rig.skin[k],
                        woodVertices[branch.shoot.FixedRingVertices[k]]));
                stemPeak=Mathf.Max(stemPeak,Vector3.Distance(solver.positions[node],solver.rest[node]));
                otherPeak=Mathf.Max(otherPeak,Vector3.Distance(solver.positions[neighbor],solver.rest[neighbor]));
                branch.rig.Strain(twig,solver.positions,out _,out _,out float twist);
                twistPeak=Mathf.Max(twistPeak,Mathf.Abs(twist));
                pinDrift=Mathf.Max(pinDrift,Vector3.Distance(solver.positions[root],solver.rest[root]),
                    Vector3.Distance(solver.positions[rootEnd],solver.rest[rootEnd]));
            }
            Release();
            for(int i=0;i<180;i++)branch.Step();
            float released=Vector3.Distance(solver.positions[node],solver.rest[node]);
            string result=$"Seed {seed}, {branches.Count} physical boughs, {branches.Sum(b=>b.shoot.LeafyTwigCount)} fine branches, {branches.Sum(b=>b.shoot.geometry.bladeRows.Count)} leaves. "
                +$"Twig {stemPeak*1000:F2} mm, neighboring leaf {otherPeak*1000:F2} mm, twist {twistPeak:F3} deg, "
                +$"pin drift {pinDrift*1000:F4} mm, twig after release {released*1000:F2} mm.";
            result+=$" Maximum pulled junction gap {maxGap*1000:F4} mm.";
            if(maxGap>.00001f || stemPeak<.0001f || otherPeak<.0001f || twistPeak<.0001f || released>=stemPeak || pinDrift>.00001f)
                throw new InvalidOperationException(result);
            // Load an actual primary rod, well beyond the embedded clamp.
            solver.Reset();
            int rodIndex=4;
            var rod=g.rods[rodIndex];
            int triangle=g.surface.FindIndex(f=>f.rod==rodIndex);
            Vector3 start=.5f*(solver.rest[rod.a]+solver.rest[rod.b]);
            var pull=Vector3.Cross((solver.rest[rod.b]-solver.rest[rod.a]).normalized,Vector3.right).normalized;
            branch.BeginGrab(triangle,Vector3.zero,start);
            float majorPeak=0,groundDrift=0,tipPeak=0;
            for(int i=1;i<=90;i++){
                solver.target=start+pull*(.25f*Mathf.Min(1,i/30f));
                foreach(var b in branches){
                    b.Step();
                    foreach(int pin in b.shoot.GroundPins)
                        groundDrift=Mathf.Max(groundDrift,Vector3.Distance(b.solver.positions[pin],b.solver.rest[pin]));
                }
                majorPeak=Mathf.Max(majorPeak,Vector3.Distance(solver.positions[rod.b],solver.rest[rod.b]));
                tipPeak=Mathf.Max(tipPeak,Vector3.Distance(solver.positions[neighbor],solver.rest[neighbor]));
            }
            branch.ReleaseGrab();
            for(int i=0;i<180;i++)branch.Step();
            float residual=Vector3.Distance(solver.positions[rod.b],solver.rest[rod.b]);
            result+=$" Primary bough diameter {2*rod.radius:F2} m, deflection {1000*majorPeak:F2} mm, "
                +$"distal leaf travel {1000*tipPeak:F2} mm, released bough {1000*residual:F2} mm, "
                +$"{branches.Sum(b=>b.shoot.GroundPins.Count)} prop anchors, ground drift {1000*groundDrift:F4} mm.";
            if(majorPeak<.0001f || tipPeak<.0001f || groundDrift>.00001f || residual>=majorPeak)
                throw new InvalidOperationException(result);
            return result;
        }

        bool Grab(Ray ray){
            Release();
            float nearest=float.PositiveInfinity;
            FigBranchInstance selected=null;
            int triangle=-1;
            Vector3 weights=default;
            foreach(var b in branches){
                Ray local=new(ray.origin-b.shoot.origin,ray.direction);
                int hit=b.PickSurface(local,ref nearest,out var w);
                if(hit<0)continue;
                selected=b;triangle=hit;weights=w;
            }
            if(selected==null)return false;
            // Reject a hidden leaf if fixed wood is nearer than its shell.
            for(int i=0;i<woodTriangles.Length;i+=3)
                if(FoliageLab.Intersect(ray,woodVertices[woodTriangles[i]],woodVertices[woodTriangles[i+1]],woodVertices[woodTriangles[i+2]],out float t,out _) && t<nearest){
                    return false;
                }
            grabbed=selected;
            grabbed.BeginGrab(triangle,weights,ray.GetPoint(nearest)-grabbed.shoot.origin);
            var solver=grabbed.solver;
            grabDepth=stage.camera.WorldToViewportPoint(solver.target+grabbed.shoot.origin).z;
            return true;
        }
    }
}
