using System;
using UnityEditor;
using UnityEngine;
using Foliage;

public static class PlantCutChecks
{
    [MenuItem("Tools/Foliage/Check cutting")]
    public static void Run()=>Debug.Log(Check());
    [MenuItem("Tools/Foliage/Check detached leaf release")]
    public static void CheckRelease()
    {
        var lab=UnityEngine.Object.FindFirstObjectByType<FoliageLab>();
        if(!Application.isPlaying || lab==null)throw new InvalidOperationException("Enter Play mode first.");
        var previous=lab.settings;
        bool paused=lab.paused;
        try
        {
            lab.paused=true;
            lab.SetSheetMode(false);
            lab.settings=PlantSettings.Default;
            lab.Rebuild();
            lab.OpenStems();
            var rows=lab.Geometry.bladeRows[0];
            var root=lab.Sheet.rest[rows[0][0]];
            int tip=rows[rows.Count-1][0];
            var end=lab.Sheet.rest[tip];
            if(!lab.SliceLeaf(0,new Plane((end-root).normalized,Vector3.Lerp(end,root,.4f))))
                throw new Exception("Could not cut the blade.");
            var sheet=lab.Sheet;
            var geom=lab.Geometry;
            int fragment=geom.ComponentOf(tip);
            var start=(Vector3[])sheet.positions.Clone();
            sheet.gravityScale=0;
            for(int i=0;i<120;i++)lab.Advance();
            for(int i=0;i<geom.nodes.Count;i++)
                if(geom.ComponentOf(i)==fragment && Vector3.Distance(start[i],sheet.positions[i])>.001f)
                    throw new Exception("The resting cut fragment moved without an external load.");
            sheet.gravityScale=1;
            for(int i=0;i<60;i++)lab.Advance();
            if(sheet.badVertex>=0 || start[tip].y-sheet.positions[tip].y<.3f)
                throw new Exception("The released blade failed to fall.");
            Debug.Log("Detached leaf stayed at rest without gravity and fell when gravity resumed.");
        }
        finally
        {
            lab.SetSheetMode(false);
            lab.settings=previous;
            lab.Rebuild();
            lab.OpenStems();
            lab.paused=paused;
        }
    }
    public static string Check()
    {
        var lab=UnityEngine.Object.FindFirstObjectByType<FoliageLab>();
        if(!Application.isPlaying || lab==null)throw new InvalidOperationException("Enter Play mode first.");
        bool paused=lab.paused;
        try
        {
            lab.paused=true;lab.OpenStems();
            var rows=lab.Geometry.bladeRows[0];int tip=rows[rows.Count-1][0],root=rows[0][0];
            for(int i=0;i<16;i++)lab.Advance();
            var old=lab.Sheet;var pos=old.positions[tip];var velocity=old.samples[tip].velocity;
            var board=pos-Vector3.up*.14f;
            lab.SetObstacle(ObstacleShape.Sphere,board,Quaternion.identity,.085f);
            var axis=(pos-old.positions[root]).normalized;
            var plane=new Plane(axis,Vector3.Lerp(pos,old.positions[root],.40f));
            int face=lab.Geometry.faces.FindIndex(f=>f.part==2);
            if(!lab.SliceLeaf(face,plane) || lab.Geometry.islands!=2 || lab.Sheet.positions[tip]!=pos ||
                lab.Sheet.samples[tip].velocity!=velocity || lab.Sheet.contact.center!=board)
                throw new Exception("Leaf split, obstacle pose or motion transfer failed.");
            int hits=0;float fall=0;
            for(int i=0;i<50;i++)
            {
                lab.Advance();var s=lab.Sheet;var g=lab.Geometry;
                for(int j=0;j<g.faces.Count;j++)
                    if(g.ComponentOf(g.faces[j].a)==g.ComponentOf(tip) && s.contact.hits[s.contact.rodCount+j].depth>0)hits++;
                fall=Mathf.Max(fall,pos.y-s.positions[tip].y);
            }
            if(hits==0 || fall<=.005f || lab.Sheet.badVertex>=0)throw new Exception($"Detached leaf fall {fall}, contact samples {hits}.");
            bool picked=false;
            foreach(var f in lab.Geometry.faces)
            {
                if(lab.Geometry.ComponentOf(f.a)!=lab.Geometry.ComponentOf(tip))continue;
                var x=lab.Sheet.positions;Vector3 n=Vector3.Cross(x[f.b]-x[f.a],x[f.c]-x[f.a]).normalized;
                var ray=new Ray((x[f.a]+x[f.b]+x[f.c])/3+n*.003f,-n);
                if(lab.BeginLabDrag(ray) && lab.Sheet.grabFace>=0 &&
                    lab.Geometry.ComponentOf(lab.Geometry.faces[lab.Sheet.grabFace].a)==lab.Geometry.ComponentOf(tip))
                    {picked=true;break;}
            }
            if(!picked)throw new Exception("Could not pick the detached leaf.");
            var grabStart=lab.Sheet.target;lab.Sheet.target+=Vector3.up*.15f;
            for(int i=0;i<30;i++)lab.Advance();
            float pulled=Vector3.Distance(grabStart,lab.Sheet.Point(lab.Sheet.grabFace,lab.Sheet.weights));
            lab.EndDrag();
            if(pulled<.01f || !lab.CutStem(5,.37f) || !lab.CutStem(3,0) || lab.Geometry.islands!=4)
                throw new Exception("Detached grab or repeated stem cut failed.");
            for(int i=0;i<60;i++)lab.Advance();
            if(lab.Sheet.badVertex>=0 || lab.Sheet.positions[0]!=lab.Sheet.rest[0])throw new Exception("Root or fragment state invalid.");
            lab.ResetState();
            if(lab.Geometry.islands!=1)throw new Exception("Reset did not restore the uncut plant.");
            return $"Moving leaf retained position and velocity, fell {fall*1000:F1} mm and contacted the sphere. Detached grab moved {pulled*1000:F1} mm. Interior and node cuts produced four components. Reset restored one.";
        }
        finally{lab.OpenStems();lab.paused=paused;}
    }
}
