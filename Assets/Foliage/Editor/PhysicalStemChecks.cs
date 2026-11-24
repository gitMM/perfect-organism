using System;
using Foliage;
using UnityEditor;
using UnityEngine;

public static class PhysicalStemChecks
{
    [MenuItem("Tools/Foliage/Check physical stems")]
    public static void Run()
    {
        var lab=UnityEngine.Object.FindFirstObjectByType<FoliageLab>();
        if(!Application.isPlaying || lab==null) throw new InvalidOperationException("Run the foliage lab first.");
        bool paused=lab.paused;
        try { lab.paused=true; lab.OpenStems(); Debug.Log(Pull(lab)); }
        finally {lab.OpenStems(); lab.paused=paused;}
    }
    static string Pull(FoliageLab lab)
    {
        var s=lab.Sheet; var p=s.plant; var g=p.geometry;
        s.gravityScale=0;
        for(int i=0;i<8;i++) lab.Advance();
        float restDrift=0;
        for(int i=0;i<s.Count;i++) restDrift=Mathf.Max(restDrift,Vector3.Distance(s.rest[i],s.positions[i]));
        if(restDrift>.0001f) throw new Exception($"Unloaded plant drifted {restDrift} m.");
        // Pick an exposed face on a blade margin, away from both the vein and the tip.
        int leaf=-1,face=-1; Vector2 start=default;
        for(int n=0;n<g.bladeRows.Count && face<0;n++)
        {
            int candidate=(n+g.bladeRows.Count/2)%g.bladeRows.Count;
            var rows=g.bladeRows[candidate]; var margin=rows[rows.Count/2];
            for(int f=0;f<g.faces.Count;f++)
            {
                var t=g.faces[f];
                int v=margin[0];
                if(t.part!=2*candidate+2 || (t.a!=v && t.b!=v && t.c!=v)) continue;
                var w=Vector3.one*.15f; w[t.a==v?0:t.b==v?1:2]=.7f;
                Vector2 cursor=lab.view.WorldToScreenPoint(s.Point(f,w));
                if(lab.BeginSheetPick(lab.view.ScreenPointToRay(cursor)) && s.grabFace==f)
                {face=f; leaf=candidate; start=cursor; break;}
            }
        }
        if(face<0) throw new Exception("No visible leaf margin found.");
        int terminal=p.leafRod[leaf], stalkBase=g.bladeRows[leaf][0][0];
        int trunk=g.joints[2*leaf], other=g.bladeRows[(leaf+1)%g.bladeRows.Count][0][0];
        Vector3 initial=s.Point(face,s.weights);
        Vector2 end=lab.view.WorldToScreenPoint(initial+Vector3.up*.28f+lab.view.transform.right*.11f);
        float stalk=0,stem=0,remote=0,bend=0,twist=0,strain=0,collar=0,force=0;
        for(int i=1;i<=150;i++)
        {
            lab.MoveSheetTarget(Vector2.Lerp(start,end,i/60f),i==25?.6f:0);
            lab.Advance();
            if(s.badVertex>=0) throw new Exception($"Invalid node {s.badVertex}.");
            force=Mathf.Max(force,s.appliedGrab.magnitude);
            stalk=Mathf.Max(stalk,Vector3.Distance(s.positions[stalkBase],s.rest[stalkBase]));
            stem=Mathf.Max(stem,Vector3.Distance(s.positions[trunk],s.rest[trunk]));
            remote=Mathf.Max(remote,Vector3.Distance(s.positions[other],s.rest[other]));
            for(int rod=terminal;rod>=0;rod=p.parent[rod])
            {
                p.Strain(rod,s.positions,out float ext,out float flex,out float torsion);
                strain=Mathf.Max(strain,Mathf.Abs(ext)); bend=Mathf.Max(bend,flex);
                if(g.rods[rod].part!=0) twist=Mathf.Max(twist,Mathf.Abs(torsion));
            }
            var row=g.bladeRows[leaf][1];
            for(int j=row.Length/2-1;j<=row.Length/2+1;j++)
                collar=Mathf.Max(collar,Vector3.Distance(s.positions[row[j]],p.CollarTarget(terminal,row[j],s.positions)));
        }
        lab.EndDrag();
        for(int i=0;i<480;i++) lab.Advance();
        float recovered=Vector3.Distance(s.positions[trunk],s.rest[trunk]);
        if(s.badVertex>=0 || s.appliedGrab.sqrMagnitude!=0 || force<=0 || bend<.1f || twist<.1f ||
            stalk<.001f || stem<.001f || (g.bladeRows.Count>1 && remote<.001f) || recovered>=stem ||
            s.positions[g.rods[0].a]!=s.rest[g.rods[0].a])
            throw new Exception($"Coupling check failed: bend {bend}, twist {twist}, stalk {stalk}, stem {stem}, remote {remote}, released {recovered}.");
        // Reverse the load path: an interpolated centreline grab must move an attached shell.
        lab.ResetState();
        int picked=-1;
        for(int r=1;r<g.rods.Count;r++)
        {
            var rod=g.rods[r]; if(rod.part!=0) continue;
            Vector2 cursor=lab.view.WorldToScreenPoint(Vector3.Lerp(s.positions[rod.a],s.positions[rod.b],.37f));
            if(!lab.BeginSheetPick(lab.view.ScreenPointToRay(cursor)) || s.grabRod!=r) continue;
            if(s.weights.y<=0 || s.weights.y>=1) continue;
            picked=r;
            for(int i=0;i<60;i++) { lab.MoveSheetTarget(cursor+Vector2.right*Mathf.Min(i,40)*2,0); lab.Advance(); }
            break;
        }
        float bladeMotion=0;
        foreach(var rows in g.bladeRows)
        {
            int tip=rows[rows.Count-1][0];
            bladeMotion=Mathf.Max(bladeMotion,Vector3.Distance(s.rest[tip],s.positions[tip]));
        }
        lab.EndDrag();
        if(picked<0 || s.badVertex>=0 || bladeMotion<.001f) throw new Exception($"Rod-to-leaf check failed: rod {picked}, tip motion {bladeMotion}.");
        return $"Stem fixture: leaf {leaf+1}, force {force:F3} N, stalk {stalk*1000:F1} mm, parent stem {stem*1000:F1} mm, other attachment {remote*1000:F1} mm, bend {bend:F2}°, stalk twist {twist:F2}°, peak strain {strain:P2}, max collar error {collar*1000:F2} mm, released stem {recovered*1000:F1} mm. Rod {picked} grab moved a blade tip {bladeMotion*1000:F1} mm.";
    }
}
