using UnityEngine;

namespace Foliage
{
    public partial class FoliageLab
    {
        PlantGeometry uncutGeometry;
        bool cutTool,cutting;
        int cutRod=-1,cutFace=-1;
        Vector2 cutStart,cutEnd;
        float cutDepth,cutFraction;
        readonly System.Collections.Generic.List<Vector2Int> splitPoints=new(),removedLinks=new(),newLinks=new();
        string cutMessage;

        public bool BeginCut(Vector2 screen)
        {
            if(Sheet?.plant==null || !BeginPlantGrab(view.ScreenPointToRay(screen)))return false;
            cutRod=inspectedRod;cutFace=inspectedFace;cutFraction=rodFraction;
            cutDepth=depth;cutStart=cutEnd=screen;EndDrag();cutting=true;return true;
        }
        Plane StrokePlane()
        {
            Vector3 a=view.ScreenToWorldPoint(new Vector3(cutStart.x,cutStart.y,cutDepth));
            Vector3 b=view.ScreenToWorldPoint(new Vector3(cutEnd.x,cutEnd.y,cutDepth));
            return new Plane(Vector3.Cross(b-a,view.ScreenPointToRay(cutStart).direction).normalized,a);
        }
        public bool FinishCut(Vector2 screen)
        {
            if(!cutting)return false;
            cutting=false;cutEnd=screen;
            if(cutRod>=0)return CutStem(cutRod,cutFraction);
            return (cutEnd-cutStart).sqrMagnitude>=16 && SliceLeaf(cutFace,StrokePlane());
        }
        public bool CutStem(int rod,float along)
        {
            if(Sheet?.plant==null)return false;
            var cut=new PlantCut(Sheet);
            return cut.Rod(rod,along) && ApplyCut(cut);
        }
        public bool SliceLeaf(int face,Plane plane)
        {
            if(Sheet?.plant==null || plane.normal.sqrMagnitude<.5f)return false;
            var cut=new PlantCut(Sheet);
            return cut.Blade(face,plane) && ApplyCut(cut);
        }
        bool ApplyCut(PlantCut cut)
        {
            cut.g.Validate();
            if(cut.g.issues.Count>0){cutMessage=cut.g.issues[0];return false;}
            cut.BuildSkin();
            var next=new VbdSheet(plant:new RodPlant(cut.g));
            try{next.ContinueCut(Sheet,cut);}catch{next.Dispose();throw;}
            static System.Collections.Generic.HashSet<Vector2Int> Links(VbdSheet s)
            {
                var links=new System.Collections.Generic.HashSet<Vector2Int>(s.edges);
                foreach(var r in s.plant.geometry.rods)links.Add(new Vector2Int(Mathf.Min(r.a,r.b),Mathf.Max(r.a,r.b)));
                return links;
            }
            var before=Links(Sheet);var after=Links(next);
            removedLinks.Clear();newLinks.Clear();
            foreach(var edge in before)if(!after.Contains(edge))removedLinks.Add(edge);
            foreach(var edge in after)if(!before.Contains(edge))newLinks.Add(edge);
            EndDrag();uncutGeometry??=Geometry;Geometry=cut.g;
            Sheet.Dispose();Sheet=next;inspectedFace=inspectedRod=-1;
            sheetColors=new Color[next.Count];strains=new float[next.Count];
            mesh.Clear();mesh.SetVertices(Geometry.skin);mesh.triangles=next.plant.skinTriangles;mesh.SetColors(Geometry.colors);
            RefreshSheetMesh();
            splitPoints.Clear();splitPoints.AddRange(cut.seam);
            cutMessage=$"{Geometry.islands} components · {cut.seam.Count} split points · {cut.replacedFaces} triangles split";
            return true;
        }
        void RestoreGeometry()
        {
            if(uncutGeometry!=null)Geometry=uncutGeometry;
            uncutGeometry=null;splitPoints.Clear();cutMessage=null;cutting=false;cutTool=false;
        }
        void DrawCutPanel()
        {
            cutTool=GUILayout.Toolbar(cutTool?1:0,new[]{"Grab","Cut"})==1;
            if(cutTool)GUILayout.Label("Click a stem to cut. On a leaf, drag along the cut line and release.");
            if(cutMessage!=null)GUILayout.Label(cutMessage);
            if(sheetTab!=0 && splitPoints.Count>0)GUILayout.Label($"Removed links (red) {removedLinks.Count} · New links (green) {newLinks.Count}\nDetached components have colored outlines.");
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
                int id=Geometry.component[a];if(id==Geometry.component[0])return;
                GL.Color(Color.HSVToRGB(Mathf.Repeat(id*.618f,1),.8f,1));Line(Sheet.positions[a],Sheet.positions[b]);
            }
            foreach(var e in Sheet.edges)ComponentEdge(e.x,e.y);
            foreach(var r in Geometry.rods)ComponentEdge(r.a,r.b);
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
