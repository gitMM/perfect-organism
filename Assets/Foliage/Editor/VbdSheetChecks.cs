using System;
using Foliage;
using UnityEditor;
using UnityEngine;

public static class VbdSheetChecks
{
    [MenuItem("Tools/Foliage/Run VBD sheet fixture")]
    public static void RunFixture() => Debug.Log(MeasureFixture());

    [MenuItem("Tools/Foliage/Check sheet mouse interaction")]
    public static void CheckMouse()
    {
        var lab=UnityEngine.Object.FindFirstObjectByType<FoliageLab>();
        if(!Application.isPlaying || lab==null) throw new InvalidOperationException("Run the foliage lab first.");
        lab.SetSheetMode(true);
        var sheet=lab.Sheet;
        float cap=sheet.grabLimit;
        bool paused=lab.paused;
        try
        {
            lab.paused=true; lab.ResetState();
            for(int i=0;i<120;i++) lab.Advance();
            int face=147;
            Vector3 weights=new Vector3(.2f,.35f,.45f);
            var original=sheet.Point(face,weights);
            Vector2 cursor=lab.view.WorldToScreenPoint(original);
            if(!lab.BeginSheetPick(lab.view.ScreenPointToRay(cursor)) || sheet.grabFace!=face ||
                Vector3.Distance(sheet.weights,weights)>.0001f) throw new Exception("Mouse triangle pick failed.");
            sheet.grabLimit=.005f;
            lab.MoveSheetTarget(cursor+Vector2.right*70,1);
            if(sheet.Point(face,weights)!=original) throw new Exception("Cursor motion teleported a vertex.");
            for(int i=0;i<12;i++)
            {
                lab.Advance();
                if(sheet.badVertex>=0 || sheet.appliedGrab.magnitude>sheet.grabLimit+1e-6f)
                    throw new Exception("Invalid or uncapped grab force.");
            }
            float travel=Vector3.Distance(sheet.Point(face,weights),original);
            float load=sheet.appliedGrab.magnitude;
            if(travel<1e-5f || load<=0 || sheet.elasticEnergy.y<=0) throw new Exception("Mouse pull did not load or bend the sheet.");
            lab.EndDrag(); lab.Advance();
            if(sheet.grabFace!=-1 || sheet.appliedGrab.sqrMagnitude!=0) throw new Exception("Mouse release left a force attached.");
            Debug.Log($"Sheet pointer check: face {face}, motion {travel:G5} m, constrained force {load:G5} N, detached.");
        }
        finally {sheet.grabLimit=cap; lab.ResetState(); lab.paused=paused;}
    }

    public static string MeasureFixture(float? dampingOverride=null)
    {
        if(!Application.isPlaying) throw new InvalidOperationException("Enter Play mode to run the GPU sheet fixture.");
        using var sheet=new VbdSheet();
        if(dampingOverride.HasValue) sheet.dampingTime=dampingOverride.Value;
        float pinDrift=0,peakStrain=0,tipMotion=0,peakGrab=0,peakBend=0,peakStretch=0,peakSpeed=0;
        float peakResidual=0,settledSpeed=0,lastSpeed=0,firstPullResidual=0,lastPullResidual=0;
        int nonFinite=0;
        void Step()
        {
            sheet.Step(1f/60);
            if(sheet.badVertex>=0) {nonFinite++;throw new Exception($"Non-finite state at tick {sheet.Steps}, node {sheet.badVertex}");}
            lastSpeed=0;
            for(int i=0;i<sheet.Count;i++)
            {
                if(sheet.colors[i]<0) pinDrift=Mathf.Max(pinDrift,Vector3.Distance(sheet.positions[i],sheet.rest[i]));
                lastSpeed=Mathf.Max(lastSpeed,sheet.samples[i].velocity.magnitude);
            }
            peakSpeed=Mathf.Max(peakSpeed,lastSpeed);
            foreach(var edge in sheet.edges)
            {
                float ratio=Vector3.Distance(sheet.positions[edge.x],sheet.positions[edge.y])/
                    Vector3.Distance(sheet.rest[edge.x],sheet.rest[edge.y]);
                peakStrain=Mathf.Max(peakStrain,Mathf.Abs(ratio-1));
            }
            peakGrab=Mathf.Max(peakGrab,sheet.appliedGrab.magnitude);
            peakBend=Mathf.Max(peakBend,sheet.elasticEnergy.y);
            peakStretch=Mathf.Max(peakStretch,sheet.elasticEnergy.x);
            peakResidual=Mathf.Max(peakResidual,sheet.residual[sheet.iterations-1]);
        }
        for(int i=0;i<360;i++) Step();
        var settled=(Vector3[])sheet.positions.Clone();
        settledSpeed=lastSpeed;
        // The final row's second triangle has two vertices on the free edge.
        int face=2*(9*10+5)+1;
        var weights=new Vector3(0,.5f,.5f);
        Vector3 origin=sheet.Point(face,weights);
        sheet.weights=weights; sheet.grabFace=face;
        for(int i=1;i<=60;i++)
        {
            sheet.target=origin+Vector3.back*.04f*Math.Min(i,30)/30;
            Step();
            tipMotion=Mathf.Max(tipMotion,Vector3.Distance(origin,sheet.Point(face,weights)));
            if(i==30) {firstPullResidual=sheet.residual[0]; lastPullResidual=sheet.residual[sheet.iterations-1];}
        }
        sheet.grabFace=-1;
        for(int i=0;i<240;i++) Step();
        float sum=0,max=0;
        for(int i=0;i<sheet.Count;i++)
        {
            float error=Vector3.Distance(settled[i],sheet.positions[i]);
            sum+=error*error; max=Mathf.Max(max,error);
        }
        return FormattableString.Invariant($"GPU {SystemInfo.graphicsDeviceName}; 10x10 quads, width 0.20 m, 11 pins, 1/60 s, sweeps {sheet.iterations}, colors {sheet.colorCount}; density {VbdSheet.Density} kg/m2, stretch {sheet.stretch} N/m, bend {sheet.bend} N, damping {sheet.dampingTime} s; grab k {sheet.grabK} N/m, c {sheet.grabD} Ns/m, cap {sheet.grabLimit} N; 360 settle / 30 pull 0.04 m / 30 hold / 240 release; peak pin drift {pinDrift:G6} m; peak absolute edge strain {peakStrain:P3}; peak attachment displacement {tipMotion:G6} m; peak grab {peakGrab:G6} N; peak stretch/bend energy {peakStretch:G6}/{peakBend:G6} J; recovery RMS/max {Mathf.Sqrt(sum/sheet.Count):G6}/{max:G6} m; peak/settled/final speed {peakSpeed:G6}/{settledSpeed:G6}/{lastSpeed:G6} m/s; pull-end sweep 1/final residual {firstPullResidual:G6}/{lastPullResidual:G6} N; worst final-sweep residual {peakResidual:G6} N; non-finite {nonFinite}.");
    }
}
