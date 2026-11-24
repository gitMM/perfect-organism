using System;
using System.Collections.Generic;
using UnityEngine;

// Retain the last survival forecast and the actual frames inside its time window.
public sealed class DeathReview
{
    [Serializable] public struct Match
    {
        public float t,eyeError,aimError,alternateError,sphereError;
        public Vector3 eye,aim,sphere,expectedEye,expectedAim,alternateAim,expectedSphere;
    }
    public FlightPlanner.Decision plan;
    public readonly List<Match> frames=new();
    public int firstDifference=-1;
    public void Reset(){plan=null;frames.Clear();firstDifference=-1;}
    public void Begin(FlightPlanner.Decision d,Vector3 sphere)
    {
        bool bait=d.reason!=null && d.reason.StartsWith("Bait: ",StringComparison.Ordinal);
        if(!bait && d.reason!="Closing from behind" && d.reason!="Partial forecast" && d.reason!="Survives horizon" && d.reason!="Predicted safe contact" && d.reason!="Predicted crate impact")return;
        Reset();plan=d;Record(d.time,d.observed,sphere);
    }
    public void Record(float time,FlightPlanner.Model player,Vector3 sphere)
    {
        if(plan==null || plan.forecast.Count==0)return;
        float t=time-plan.time;
        var samples=plan.forecast;
        if(t<0 || t>samples[samples.Count-1].t+.00001f)return;
        int i=0;while(i+1<samples.Count && samples[i].t<t)i++;
        var a=i==0?new FlightPlanner.Sample{eye=plan.observed.eye,aim=plan.observed.aim,alternateAim=plan.observed.aim,
            sphere=plan.choices[plan.route[0]].from}:samples[i-1];
        var b=samples[i];float u=Mathf.InverseLerp(a.t,b.t,t);
        var m=new Match{t=t,eye=player.eye,aim=player.aim,sphere=sphere,expectedEye=Vector3.Lerp(a.eye,b.eye,u),
            expectedAim=Vector3.Slerp(a.aim,b.aim,u),alternateAim=Vector3.Slerp(a.alternateAim,b.alternateAim,u),
            expectedSphere=Vector3.Lerp(a.sphere,b.sphere,u)};
        m.eyeError=Vector3.Distance(m.eye,m.expectedEye);m.aimError=Vector3.Angle(m.aim,m.expectedAim);
        m.alternateError=Vector3.Angle(m.aim,m.alternateAim);m.sphereError=Vector3.Distance(sphere,m.expectedSphere);
        if(firstDifference<0 && (m.eyeError>=.25f || m.aimError>=5f))firstDifference=frames.Count;
        frames.Add(m);
    }
}
