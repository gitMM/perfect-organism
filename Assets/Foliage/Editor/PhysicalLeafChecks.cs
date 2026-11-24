using System;
using UnityEditor;
using UnityEngine;
using Foliage;

public static class PhysicalLeafChecks
{
    [MenuItem("Tools/Foliage/Check physical leaves")]
    public static void Run()
    {
        var lab=UnityEngine.Object.FindFirstObjectByType<FoliageLab>();
        if(!Application.isPlaying || lab==null) throw new InvalidOperationException("Run the foliage lab first.");
        bool paused=lab.paused;
        try
        {
            lab.paused=true;
            Debug.Log(Pull(lab,0,true,6));
            Debug.Log(Pull(lab,0,true,1));
            Debug.Log(Pull(lab,lab.Geometry.bladeRows.Count-1,false,6));
        }
        finally {lab.OpenLeaf(0); lab.paused=paused;}
    }
    public static string Pull(FoliageLab lab,int leaf,bool tip,float ratio)
    {
        lab.OpenLeaf(leaf);
        var s=lab.Sheet;
        s.veinRatio=ratio;
        s.gravityScale=0;
        lab.Advance();
        float restEnergy=s.elasticEnergy.x+s.elasticEnergy.y;
        if(restEnergy>1e-8f) throw new Exception($"Curved rest shape under load without gravity: {restEnergy} J");
        s.gravityScale=1;
        void Advance()
        {
            lab.Advance();
            if(s.badVertex>=0) throw new Exception($"Non-finite leaf node {s.badVertex}");
        }
        for(int i=0;i<600;i++) Advance();
        Vector3[] baseline=(Vector3[])s.positions.Clone();
        var rows=lab.Geometry.bladeRows[leaf];
        int source=tip?rows[rows.Count-1][0]:rows[rows.Count/2][0];
        int node=Array.IndexOf(s.leaf.sourceNodes,source);
        int face=-1;
        Vector3 weights=default,initial=default;
        Vector2 cursor=default;
        for(int corner=0;corner<s.triangles.Length;corner++)
        {
            if(s.triangles[corner]!=node) continue;
            weights=Vector3.one*.15f; weights[corner%3]=.7f;
            cursor=lab.view.WorldToScreenPoint(s.Point(corner/3,weights));
            if(!lab.BeginSheetPick(lab.view.ScreenPointToRay(cursor))) continue;
            int hit=s.grabFace;
            if(s.triangles[3*hit]!=node && s.triangles[3*hit+1]!=node && s.triangles[3*hit+2]!=node) continue;
            face=hit; weights=s.weights; initial=s.Point(face,weights); break;
        }
        if(face<0) throw new Exception($"No visible face at node {source} on leaf {leaf+1}.");
        Vector3 dest=initial+(tip?Vector3.up:(Vector3.up+lab.view.transform.right*.55f).normalized)*s.leaf.length*.28f;
        // Use the same screen/depth target and release paths as mouse input in Play mode.
        Vector2 end=lab.view.WorldToScreenPoint(dest);
        float maxMotion=0,peakStrain=0,maxPin=0,maxAngle=0,peakForce=0;
        float peakRms=0;
        float Rms()
        {
            float sum=0;
            for(int v=0;v<s.Count;v++)
            {
                sum+=(baseline[v]-s.positions[v]).sqrMagnitude;
                if(s.colors[v]<0) maxPin=Mathf.Max(maxPin,Vector3.Distance(s.rest[v],s.positions[v]));
            }
            return Mathf.Sqrt(sum/s.Count);
        }
        Vector3 Normal(Vector3[] p,int i) => Vector3.Cross(p[s.triangles[i+1]]-p[s.triangles[i]],
            p[s.triangles[i+2]]-p[s.triangles[i]]).normalized;
        for(int i=1;i<=90;i++)
        {
            lab.MoveSheetTarget(Vector2.Lerp(cursor,end,i/45f),i==15?.8f:0);
            Advance();
            maxMotion=Mathf.Max(maxMotion,Vector3.Distance(initial,s.Point(face,weights)));
            peakForce=Mathf.Max(peakForce,s.appliedGrab.magnitude);
            peakRms=Mathf.Max(peakRms,Rms());
            foreach(var e in s.edges)
                peakStrain=Mathf.Max(peakStrain,Mathf.Abs(Vector3.Distance(s.positions[e.x],s.positions[e.y])/
                    Vector3.Distance(s.rest[e.x],s.rest[e.y])-1));
            for(int t=0;t<s.triangles.Length;t+=3)
                maxAngle=Mathf.Max(maxAngle,Vector3.Angle(Normal(s.positions,t),Normal(baseline,t)));
        }
        lab.EndDrag();
        for(int i=0;i<600;i++) Advance();
        float remaining=Rms();
        if(maxPin!=0 || peakForce<=0 || s.appliedGrab!=Vector3.zero || maxMotion<.001f ||
            maxAngle<1 || remaining>=peakRms) throw new Exception($"Grab/recovery check failed: peak RMS {peakRms}, final {remaining}, motion {maxMotion}, pin {maxPin}, angle {maxAngle}");
        return $"Leaf {leaf+1} {(tip?"tip":"side")} vein x{ratio}, {s.Count} nodes: unloaded rest energy {restEnergy:G4} J; peak load {peakForce:G4} N, point travel {maxMotion:G4} m, peak strain {peakStrain:P2}, face rotation {maxAngle:F1} deg; peak/released RMS {peakRms:G4}/{remaining:G4} m; pin drift {maxPin:G3} m.";
    }
}
