using System.Threading.Tasks;
using UnityEngine;

namespace Foliage
{
    public partial class FoliageLab
    {
        PlantGeometry uncutGeometry;
        readonly System.Collections.Generic.HashSet<int> rootedComponents=new();
        bool cutTool,cutting,highlightDetached;
        int cutRod=-1,cutFace=-1;
        Vector2 cutStart,cutEnd;
        float cutDepth,cutFraction;
        readonly System.Collections.Generic.List<Vector2Int> splitPoints=new(),removedLinks=new(),newLinks=new();
        string cutMessage;

        sealed class PreparedCut
        {
            public PlantCut cut;
            public VbdSheet next;
            public readonly System.Collections.Generic.List<Vector2Int> removed=new(),added=new();
            public string error;
        }
        Task<PreparedCut> preparingCut;
        VbdSheet cutSource;
        Vector2 preparedEnd;
        bool releasePending;
        public bool BeginCut(Vector2 screen)
        {
            if(releasePending || Sheet?.plant==null)return false;
            FinishQueuedTick();
            if(!BeginPlantGrab(view.ScreenPointToRay(screen)))return false;
            cutRod=inspectedRod;cutFace=inspectedFace;cutFraction=rodFraction;
            cutDepth=depth;cutStart=cutEnd=screen;EndDrag();cutting=true;
            if(cutRod>=0)StartPreparingCut();
            return true;
        }
        Plane StrokePlane()
        {
            Vector3 a=view.ScreenToWorldPoint(new Vector3(cutStart.x,cutStart.y,cutDepth));
            Vector3 b=view.ScreenToWorldPoint(new Vector3(cutEnd.x,cutEnd.y,cutDepth));
            return new Plane(Vector3.Cross(b-a,view.ScreenPointToRay(cutStart).direction).normalized,a);
        }
        static PreparedCut Prepare(VbdSheet source,PlantCut cut,int rod,float fraction,int face,Plane plane,bool gpuSkin)
        {
            var result=new PreparedCut{cut=cut};
            if(!(rod>=0?cut.Rod(rod,fraction):cut.Blade(face,plane)))return result;
            cut.g.Validate();
            if(cut.g.issues.Count>0){result.error=cut.g.issues[0];return result;}
            cut.BuildSkin();
            var plant=new RodPlant(cut.g,source.plant,cut);
            result.next=new VbdSheet(null,plant,true);
            static System.Collections.Generic.HashSet<Vector2Int> Links(VbdSheet s)
            {
                var links=new System.Collections.Generic.HashSet<Vector2Int>(s.edges);
                foreach(var r in s.plant.geometry.rods)links.Add(new Vector2Int(Mathf.Min(r.a,r.b),Mathf.Max(r.a,r.b)));
                return links;
            }
            var before=Links(source);var after=Links(result.next);

            foreach(var edge in before)if(!after.Contains(edge))result.removed.Add(edge);
            foreach(var edge in after)if(!before.Contains(edge))result.added.Add(edge);
            if(gpuSkin)result.next.PrepareRenderData(null,cut,source);
            return result;
        }
        void StartPreparingCut()
        {
            cutSource=Sheet;preparedEnd=cutEnd;
            var source=Sheet;var snapshot=new PlantCut(source);
            int rod=cutRod,face=cutFace;float fraction=cutFraction;
            Plane plane=rod>=0?default:StrokePlane();
            bool gpuSkin=settings.PlantCount>1;
            // Snapshot moving nodes on the main thread. All topology and render-data
            // preparation below uses managed data; GPU resources are created at commit.
            preparingCut=Task.Run(()=>
            {
                try{return Prepare(source,snapshot,rod,fraction,face,plane,gpuSkin);}
                catch(System.Exception e){return new PreparedCut{error=e.ToString()};}
            });
        }
        void PreviewCut(Vector2 screen)
        {
            cutEnd=screen;
            if(cutRod<0 && (screen-cutStart).sqrMagnitude>=16 &&
                (preparingCut==null || preparingCut.IsCompleted && (preparedEnd-screen).sqrMagnitude>=9))
                StartPreparingCut();
        }
        void ReleaseCut(Vector2 screen)
        {
            cutEnd=screen;
            if(cutRod<0 && (cutEnd-cutStart).sqrMagnitude<16)
            {CancelCut();return;}
            releasePending=true;
            CompleteRelease(false);
        }
        bool CompleteRelease(bool wait)
        {
            if(preparingCut==null)StartPreparingCut();
            if(!wait && !preparingCut.IsCompleted)return false;
            if(cutRod<0 && preparedEnd!=cutEnd)
            {
                StartPreparingCut();
                if(!wait)return false;
            }
            var result=preparingCut.GetAwaiter().GetResult();
            bool current=cutSource==Sheet;
            CancelCut();
            return current && CommitCut(result);
        }
        void CancelCut()
        {
            preparingCut=null;cutSource=null;releasePending=false;cutting=false;
        }
        public bool FinishCut(Vector2 screen)
        {
            if(!cutting)return false;
            cutEnd=screen;
            if(cutRod<0 && (cutEnd-cutStart).sqrMagnitude<16){CancelCut();return false;}
            return CompleteRelease(true);
        }
        public bool CutStem(int rod,float along)
        {
            if(Sheet?.plant==null || rod<0)return false;
            FinishQueuedTick();CancelCut();
            return CommitCut(Prepare(Sheet,new PlantCut(Sheet),rod,along,-1,default,settings.PlantCount>1));
        }
        public bool SliceLeaf(int face,Plane plane)
        {
            if(Sheet?.plant==null || plane.normal.sqrMagnitude<.5f)return false;
            FinishQueuedTick();CancelCut();
            return CommitCut(Prepare(Sheet,new PlantCut(Sheet),-1,0,face,plane,settings.PlantCount>1));
        }
        bool CommitCut(PreparedCut result)
        {
            if(result.error!=null){cutMessage=result.error;Debug.LogWarning(cutMessage);return false;}
            var next=result.next;if(next==null)return false;
            var cut=result.cut;
            next.UploadTopology();
            try{next.ContinueCut(Sheet,cut);}catch{next.Dispose();throw;}
            EndDrag();
            var previous=Sheet;
            try
            {
                if(settings.PlantCount>1)next.BindRenderMesh(mesh,cut,previous);
                else
                {
                    mesh.Clear();mesh.SetVertices(cut.g.skin);mesh.triangles=next.plant.skinTriangles;
                    mesh.SetColors(cut.g.colors);mesh.SetUVs(0,cut.g.bladeUV);
                }
            }
            catch{next.Dispose();throw;}
            uncutGeometry??=Geometry;Geometry=cut.g;
            removedLinks.Clear();removedLinks.AddRange(result.removed);
            newLinks.Clear();newLinks.AddRange(result.added);
            rootedComponents.Clear();
            foreach(int node in Geometry.rootNodes)rootedComponents.Add(Geometry.component[node]);
            previous.Dispose();Sheet=next;inspectedFace=inspectedRod=-1;
            sheetColors=new Color[next.Count];strains=new float[next.Count];
            RefreshSheetMesh();
            splitPoints.Clear();splitPoints.AddRange(cut.seam);
            cutMessage=$"{Geometry.islands} components · {cut.seam.Count} split points · {cut.replacedFaces} triangles split";
            return true;
        }
        void RestoreGeometry()
        {
            CancelCut();
            if(uncutGeometry!=null)Geometry=uncutGeometry;
            uncutGeometry=null;splitPoints.Clear();cutMessage=null;cutting=false;cutTool=false;
        }
        void DrawCutPanel()
        {
            cutTool=GUILayout.Toolbar(cutTool?1:0,new[]{"Grab","Cut"})==1;
            if(cutTool)GUILayout.Label("Click a stem to cut. On a leaf, drag along the cut line and release.");
            if(cutMessage!=null)GUILayout.Label(cutMessage);
            if(sheetTab!=0 && splitPoints.Count>0)highlightDetached=GUILayout.Toggle(highlightDetached,"Highlight detached pieces");
            if(sheetTab!=0 && splitPoints.Count>0)GUILayout.Label($"Removed links (red) {removedLinks.Count} · New links (green) {newLinks.Count}");
        }
        void DrawCutOverlay()
        {
            if(cutting && (cutEnd-cutStart).sqrMagnitude>=16)
            {
                Vector3 At(Vector2 p,float z)=>view.ScreenToWorldPoint(new Vector3(p.x,p.y,z));
                float near=cutDepth*.92f,far=cutDepth*1.08f;
                GL.Color(Color.yellow);Line(At(cutStart,near),At(cutEnd,near));Line(At(cutStart,far),At(cutEnd,far));
                Line(At(cutStart,near),At(cutStart,far));Line(At(cutEnd,near),At(cutEnd,far));
                if(cutFace>=0)
                {
                    var plane=StrokePlane();
                    foreach(var f in Geometry.faces)if(f.part==Geometry.faces[cutFace].part)
                    {
                        Vector3 first=default;bool found=false;
                        void Edge(int a,int b)
                        {
                            Vector3 x=Sheet.positions[a],y=Sheet.positions[b];
                            float d=plane.GetDistanceToPoint(x),e=plane.GetDistanceToPoint(y);
                            if((d>0)==(e>0))return;
                            Vector3 hit=Vector3.Lerp(x,y,d/(d-e));
                            if(found)Line(first,hit);else {first=hit;found=true;}
                        }
                        Edge(f.a,f.b);Edge(f.b,f.c);Edge(f.c,f.a);
                    }
                }
            }
            if(splitPoints.Count==0 || sheetTab==0)return;
            void ComponentEdge(int a,int b)
            {
                int id=Geometry.component[a];if(rootedComponents.Contains(id))return;
                GL.Color(Color.HSVToRGB(Mathf.Repeat(id*.618f,1),.8f,1));Line(Sheet.positions[a],Sheet.positions[b]);
            }
            if(highlightDetached)
            {
                foreach(var e in Sheet.edges)ComponentEdge(e.x,e.y);
                foreach(var r in Geometry.rods)ComponentEdge(r.a,r.b);
            }
            GL.Color(Color.red);foreach(var e in removedLinks)Line(Sheet.positions[e.x],Sheet.positions[e.y]);
            GL.Color(Color.green);foreach(var e in newLinks)Line(Sheet.positions[e.x],Sheet.positions[e.y]);
            foreach(var pair in splitPoints)
            {
                GL.Color(Color.yellow);Cross(Sheet.positions[pair.x],.007f);
                GL.Color(Color.magenta);Cross(Sheet.positions[pair.y],.007f);
            }
        }
    }
}
