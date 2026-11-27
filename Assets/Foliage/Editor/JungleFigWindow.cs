using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Foliage.Editor
{
    public class JungleFigWindow : EditorWindow
    {
        [SerializeField] FigSettings settings = new();
        [SerializeField] int seed=1;
        enum View { WholeTree, TrailFront, TrailRear, BarkDetail }
        [SerializeField] View cameraView;
        enum Surface { Bark, Grey, GrowthStripes }
        [SerializeField] Surface surface;
        Texture2D preview;

        [MenuItem("Tools/Foliage/Jungle Fig Generator")]
        static void Open()=>GetWindow<JungleFigWindow>("Jungle fig");
        void OnDisable(){if(preview)DestroyImmediate(preview);}
        void OnGUI()
        {
            var state=new SerializedObject(this);
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(state.FindProperty("seed"));
            EditorGUILayout.PropertyField(state.FindProperty("settings"),true);
            EditorGUILayout.PropertyField(state.FindProperty("cameraView"));
            EditorGUILayout.PropertyField(state.FindProperty("surface"));
            bool changed=EditorGUI.EndChangeCheck();
            state.ApplyModifiedProperties();
            if(changed && preview){DestroyImmediate(preview); preview=null;}
            if(GUILayout.Button("Preview seed") || (preview==null && Event.current.type==EventType.Repaint))
            {
                if(preview)DestroyImmediate(preview);
                preview=Render(seed,settings,640,cameraView,surface);
            }
            if(GUILayout.Button("Open interactive leafy crown")) FigBranchWindow.Open(seed,settings);
            if(GUILayout.Button("Save interactive tree prefab…")){
                string path=EditorUtility.SaveFilePanelInProject("Save interactive fig","Interactive Fig "+seed,"prefab","Choose prefab location");
                if(path.Length>0)SaveFigPrefab(AssetDatabase.GenerateUniqueAssetPath(path),seed,settings);
            }
            if(GUILayout.Button("Save seed as mesh…")){
                string path=EditorUtility.SaveFilePanelInProject("Save jungle fig","Jungle Fig "+seed,"asset","Choose mesh location");
                if(path.Length>0){
                    path=AssetDatabase.GenerateUniqueAssetPath(path);
                    AssetDatabase.CreateAsset(JungleFig.Generate(seed,settings),path);
                    Selection.activeObject=AssetDatabase.LoadAssetAtPath<Mesh>(path);
                }
            }
            if(GUILayout.Button("Export 16 consecutive seeds…")){
                string path=EditorUtility.SaveFilePanel("Save contact sheet","","jungle-fig-seeds.png","png");
                if(path.Length>0)Export(path,seed,settings,cameraView,surface:surface);
            }
            var rect=GUILayoutUtility.GetRect(100,100,GUILayout.ExpandWidth(true),GUILayout.ExpandHeight(true));
            if(preview)GUI.DrawTexture(rect,preview,ScaleMode.ScaleToFit);
        }

        public static void SaveFigPrefab(string path,int seed,FigSettings settings){
            var go=new GameObject("Interactive Fig "+seed);
            try{
                var fig=go.AddComponent<InteractiveJungleFig>();
                fig.seed=seed;fig.settings=settings;
                fig.bark=AssetDatabase.LoadAssetAtPath<Material>("Assets/Foliage/Fig Bark.mat");
                PrefabUtility.SaveAsPrefabAsset(go,path);
            }finally{DestroyImmediate(go);}
        }

        static Texture2D Render(int seed,FigSettings settings,int size,View view,Surface surface)
        {
            var stage=new PreviewRenderUtility();
            var figure=new Material(Shader.Find("Standard"));
            var ground=new Material(figure);
            var wood=surface==Surface.Grey?new Material(figure):new Material(AssetDatabase.LoadAssetAtPath<Material>("Assets/Foliage/Fig Bark.mat"));
            var shoots=surface==Surface.Bark?new List<FigShoot>():null;
            var mesh=JungleFig.Generate(seed,settings,shoots);
            var canopy=new List<Mesh>();
            Material leafMaterial=null;
            try{
                if(surface==Surface.Grey){
                    wood.color=new Color(.40f,.40f,.40f);
                    wood.SetFloat("_Glossiness",.07f);
                }else wood.SetFloat("_ShowGrain",surface==Surface.GrowthStripes?1:0);
                figure.color=new Color(.23f,.23f,.23f);
                ground.color=new Color(.43f,.43f,.43f);
                stage.ambientColor=new Color(.48f,.48f,.48f);
                stage.lights[0].color=Color.white;
                stage.lights[1].color=Color.white;
                stage.lights[0].intensity=1.25f;
                stage.lights[0].transform.rotation=Quaternion.Euler(40,-35,0);
                stage.lights[1].intensity=.65f;
                stage.lights[1].transform.rotation=Quaternion.Euler(35,140,0);
                stage.BeginStaticPreview(new Rect(0,0,size,size));
                var camera=stage.camera;
                camera.backgroundColor=new Color(.78f,.78f,.76f);
                camera.clearFlags=CameraClearFlags.SolidColor;
                camera.orthographic=true;
                float treeScale=settings.ScaleFor(seed);
                Vector3 gradient=settings.GroundGradient;
                camera.orthographicSize=21.5f*treeScale;
                Vector3 target=Vector3.up*13*treeScale;
                camera.transform.position=target+new Vector3(11,6,-48);
                if(view!=View.WholeTree){
                    float side=view==View.TrailRear?-1:1;
                    camera.orthographic=false;
                    camera.fieldOfView=54;
                    camera.transform.position=new Vector3(1.4f*side*treeScale,1.7f,-14*side*treeScale);
                    camera.transform.position+=Vector3.up*Vector3.Dot(gradient,camera.transform.position);
                    target=Vector3.up*4.7f*treeScale;
                }
                if(view==View.BarkDetail){
                    var points=mesh.vertices;
                    var normals=mesh.normals;
                    Vector3 probe=new Vector3(-1.5f,1.65f,-5)*treeScale;
                    int index=0;
                    float nearest=float.PositiveInfinity;
                    for(int i=0;i<points.Length;i++){
                        if(normals[i].z>-.5f || Mathf.Abs(normals[i].y)>.55f)continue;
                        float distance=(points[i]-probe).sqrMagnitude;
                        if(distance<nearest){nearest=distance;index=i;}
                    }
                    target=points[index];
                    camera.transform.position=target+normals[index]*1.15f;
                }
                camera.transform.LookAt(target);
                camera.nearClipPlane=.1f;camera.farClipPlane=120;
                stage.DrawMesh(mesh,Matrix4x4.identity,wood,0);
                if(shoots!=null){
                    leafMaterial=new Material(Resources.Load<Shader>("FigLeaf"));
                    foreach(var shoot in shoots){
                        var branch=shoot.CreateMesh();canopy.Add(branch);
                        var pose=Matrix4x4.Translate(shoot.origin);
                        stage.DrawMesh(branch,pose,wood,0);stage.DrawMesh(branch,pose,leafMaterial,1);
                    }
                }

                var round=Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
                void Ellipsoid(Vector3 pos,Vector3 scale,Quaternion rot,Material mat)=>
                    stage.DrawMesh(round,Matrix4x4.TRS(pos,rot,scale*.5f),mat,0);
                var groundRotation=Quaternion.FromToRotation(Vector3.up,(Vector3.up-gradient).normalized);
                Ellipsoid(groundRotation*(Vector3.down*.28f),new Vector3((view==View.WholeTree?40:110)*treeScale,.56f,(view==View.WholeTree?36:110)*treeScale),groundRotation,ground);
                Vector3 foot=new Vector3(5.4f,0,-3.8f)*treeScale;
                if(view!=View.WholeTree){
                    float direction=view==View.TrailRear?-1:1;
                    foot=new Vector3(2.4f*direction,0,-7.2f*direction)*treeScale;
                }
                foot.y=Vector3.Dot(gradient,foot);
                void Limb(Vector3 a,Vector3 b,float width){
                    Vector3 delta=b-a;
                    Ellipsoid(foot+(a+b)*.5f,new Vector3(width,delta.magnitude+width*.5f,width),Quaternion.FromToRotation(Vector3.up,delta),figure);
                }
                Ellipsoid(foot+Vector3.up*1.67f,new Vector3(.22f,.26f,.23f),Quaternion.identity,figure);
                Limb(new Vector3(0,.8f,0),new Vector3(0,1.43f,0),.34f);
                for(int sign=-1;sign<=1;sign+=2){
                    Limb(new Vector3(sign*.09f,.88f,0),new Vector3(sign*.15f,.07f,sign*.13f),.145f);
                    Limb(new Vector3(sign*.17f,1.43f,0),new Vector3(sign*.24f,.89f,-sign*.1f),.10f);
                }
                camera.Render();
                return stage.EndStaticPreview();
            } finally {
                stage.Cleanup();
                foreach(var branch in canopy)DestroyImmediate(branch);
                if(leafMaterial)DestroyImmediate(leafMaterial);
                DestroyImmediate(mesh);DestroyImmediate(wood);DestroyImmediate(figure);DestroyImmediate(ground);
            }
        }

        // The labels stay legible in the exported PNG even outside the editor.
        static readonly string[] digits={"111101101101111","010110010010111","111001111100111","111001111001111","101101111001001","111100111001111","111100111101111","111001010010010","111101111101111","111101111001111"};
        static void Label(Texture2D sheet,string label,int x,int y)
        {
            foreach(char ch in label){
                string pattern=ch=='-'?"000000111000000":digits[ch-'0'];
                for(int j=0;j<15;j++)if(pattern[j]=='1')
                    for(int dy=0;dy<3;dy++)for(int dx=0;dx<3;dx++)
                        sheet.SetPixel(x+3*(j%3)+dx,y+3*(4-j/3)+dy,new Color(.18f,.18f,.18f));
                x+=13;
            }
        }

        static void Export(string path,int start,FigSettings settings,View view,bool pair=false,Surface surface=Surface.Grey,bool compare=false)
        {
            int tile=pair?900:512;
            const int gap=4;
            int cols=pair?2:4,rows=pair?(compare?2:1):4;
            int width=cols*(tile+gap)+gap,height=rows*(tile+gap)+gap;
            var sheet=new Texture2D(width,height,TextureFormat.RGB24,false);
            try{
                var background=new Color32[width*height];
                System.Array.Fill(background,new Color32(244,244,240,255));
                sheet.SetPixels32(background);
                for(int i=0;i<cols*rows;i++){
                    int specimen=pair?start:unchecked(start+i);
                    var texture=Render(specimen,settings,tile,pair && i%2==1?View.TrailRear:view,compare && i<2?Surface.Grey:surface);
                    try{
                        int x=gap+(i%cols)*(tile+gap),y=gap+(rows-1-i/cols)*(tile+gap);
                        sheet.SetPixels32(x,y,tile,tile,texture.GetPixels32());
                        Label(sheet,specimen.ToString(),x+14,y+14);
                    }finally{DestroyImmediate(texture);}
                }
                sheet.Apply();
                File.WriteAllBytes(path,sheet.EncodeToPNG());
            } finally {DestroyImmediate(sheet);}
        }
        public static void ExportBarkComparison()
        {
            var settings=new FigSettings();
            Export(Path.GetFullPath("Jungle Fig - Seed 2 bark comparison.png"),2,settings,View.TrailFront,true,Surface.Bark,true);
            var detail=Render(2,settings,900,View.BarkDetail,Surface.Bark);
            try{File.WriteAllBytes(Path.GetFullPath("Jungle Fig - Bark detail.png"),detail.EncodeToPNG());}
            finally{DestroyImmediate(detail);}
            Export(Path.GetFullPath("Jungle Fig - Bark seeds 01-16.png"),1,settings,View.TrailFront,surface:Surface.Bark);
        }
        public static void ExportRootComparison()
        {
            var settings=new FigSettings();
            Export(Path.GetFullPath("Jungle Fig - Revised full trees.png"),1,settings,View.WholeTree);
            Export(Path.GetFullPath("Jungle Fig - Trail level.png"),1,settings,View.TrailFront);
            Export(Path.GetFullPath("Jungle Fig - Seed 2 front and rear.png"),2,settings,View.TrailFront,true);
        }
        public static void ExportMilestone()=>Export(Path.GetFullPath("Jungle Fig - Seeds 01-16.png"),1,new FigSettings(),View.WholeTree);
    }
}

