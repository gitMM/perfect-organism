using System.Collections.Generic;
using UnityEngine;

// Challenge a potential win before giving it priority over survival routes.
public sealed class ContactCheck
{
    public const float Tick=FlightPlanner.Step/8;
    const int Count=(FlightPlanner.Depth+2)*8;
    readonly Vector3[,] eyes=new Vector3[4,Count];
    FlightPlanner.Model observed;
    float turnRate;
    CargoPhysics physics;
    SphereEnvelope envelope;
    CargoPhysics.State initialCrate;
    bool generated;

    readonly FlightBody body;
    public ContactCheck() : this(FlightBody.Default) {}
    public ContactCheck(FlightBody body) { this.body=body; }

    public void Begin(FlightPlanner.Model player,float rate,CargoPhysics cargoPhysics=null,CargoPhysics.State crate=default,SphereEnvelope envelope=null)
    {
        physics=cargoPhysics;initialCrate=crate;
        this.envelope=envelope;
        observed=player;turnRate=Mathf.Max(2.6f,rate);generated=false;
    }

    void Generate()
    {
        Vector3 right=Vector3.Cross(Vector3.up,observed.aim).normalized;
        for(int hypothesis=0;hypothesis<4;hypothesis++){
            var player=observed;
            for(int i=0;i<Count;i++){
                // Allow four ticks of unchanged input before testing a changed input.
                if(i==4 && hypothesis!=0){
                    Vector3 lateral=hypothesis==1?Vector3.zero:right*(hypothesis==2?6:-6);
                    player.velocity.x=lateral.x;player.velocity.z=lateral.z;
                }
                player.eye=PlayerMotion.Slide(player.eye,player.velocity*Tick);
                PlayerMotion.Vertical(ref player,Tick);
                eyes[hypothesis,i]=player.eye;
            }
        }
        generated=true;
    }

    public string Verify(List<FlightPlanner.Edge> route,float deadline)
    {
        if(!generated)Generate();
        int ticks=Mathf.Min(Count,Mathf.RoundToInt(deadline/Tick));
        for(int h=0;h<4;h++){
            var crate=initialCrate;
            var shape=envelope?.Current??default;
            Vector3 previousEye=observed.eye,previousSphere=route[0].from;
            bool touched=false;
            for(int i=0;i<ticks;i++){
                float t=(i+1)*Tick;
                Vector3 sphere=FlightPlanner.FlightPoint(route[i/8],(i%8+1)*Tick);
                Vector3 eye=eyes[h,i];
                if(crate.present){
                    Vector3 delta=eye-(i==0?observed.eye:eyes[h,i-1]);
                    var movement=physics.Advance(ref crate,previousEye,previousEye+delta,previousSphere,route[i/8].supportCenters==null?sphere:previousSphere,body.radius,Tick);
                    if(route[i/8].supportCenters==null && (movement.sphere-sphere).sqrMagnitude>.0036f)return "Crate obstructs flight";
                    eye=movement.player;previousEye=eye;previousSphere=sphere;
                }
                if(envelope!=null)shape=envelope.Predict(shape,sphere,eye,crate,Tick,upcoming:FlightPlanner.FlightPoint(route[i/8],(i%8+1)*Tick+.12f));
                Vector3 center=envelope==null?sphere:shape.center;
                float sightRadius=envelope==null?body.radius:Mathf.Max(shape.width,shape.height);
                if(t>=observed.ready && SphereSight.Exposed(eye,center,out var target,sightRadius,crate)){
                    // A human can turn while the sphere is outside the tracking cone.
                    // Test the nearest reachable bearing and the exposed aim point.
                    Vector3 aim=Vector3.RotateTowards(observed.aim,(target-eye).normalized,turnRate*t,0);
                    if(envelope!=null?shape.Intercepts(eye,aim,crate):SphereSight.Rail(eye,aim,sphere,out _,body.radius,crate))return "Rail can intercept";
                }
                if(envelope!=null?shape.Touches(eye):body.Touches(sphere,eye)){touched=true;break;}
            }
            if(!touched)return h==0?"Contact timing uncertain":h==1?"Player can stop short":"Player can sidestep";
        }
        return null;
    }
}
