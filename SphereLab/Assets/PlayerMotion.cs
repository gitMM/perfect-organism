using UnityEngine;

public static class PlayerMotion
{
    const int Solid=1<<8;
    const float Skin=.01f, StepHeight=.57f;
    // Flat soles give the same support height at the edge and middle of a tread.
    static readonly Vector3 HalfBody=new(.30f,.88f,.30f);
    static bool Sweep(Vector3 eye,Vector3 direction,float distance,out RaycastHit hit)
    {
        return Physics.BoxCast(eye-Vector3.up*.61f,HalfBody,direction,out hit,
            Quaternion.identity,distance+Skin,Solid,QueryTriggerInteraction.Ignore);
    }

    public static Vector3 Slide(Vector3 eye,Vector3 delta)
    {
        delta.y=0;
        if(delta.sqrMagnitude<1e-12f)return eye;
        bool grounded=Sweep(eye,Vector3.down,.025f,out var floor) && floor.normal.y>.7f;
        var start=eye;
        for(int axis=0;axis<2;axis++){
            float amount=axis==0?delta.x:delta.z;
            if(Mathf.Abs(amount)<.00001f)continue;
            Vector3 dir=(axis==0?Vector3.right:Vector3.forward)*Mathf.Sign(amount);
            float distance=Mathf.Abs(amount);
            if(!Sweep(eye,dir,distance,out var wall)){eye+=dir*distance;continue;}
            if(grounded){
                float lift=Sweep(eye,Vector3.up,StepHeight,out var ceiling)
                    ? Mathf.Max(0,ceiling.distance-Skin):StepHeight;
                Vector3 raised=eye+Vector3.up*lift;
                if(lift>Skin && !Sweep(raised,dir,distance,out _) &&
                    Sweep(raised+dir*distance,Vector3.down,lift+.025f,out var tread) && tread.normal.y>.7f){
                    eye=raised+dir*distance-Vector3.up*Mathf.Max(0,tread.distance-Skin);
                    continue;
                }
            }
            eye+=dir*Mathf.Clamp(wall.distance-Skin,0,distance);
        }
        if(grounded && eye.y<=start.y+Skin && Sweep(eye,Vector3.down,StepHeight,out var below) && below.normal.y>.7f)
            eye.y-=Mathf.Max(0,below.distance-Skin);
        return eye;
    }

    public static void Vertical(ref FlightPlanner.Model m,float dt)
    {
        m.velocity.y-=19*dt;
        float offset=m.velocity.y*dt;
        if(offset!=0 && Sweep(m.eye,Vector3.up*Mathf.Sign(offset),Mathf.Abs(offset),out var hit)){
            offset=Mathf.Sign(offset)*Mathf.Max(0,hit.distance-Skin);
            m.velocity.y=0;
        }
        m.eye.y+=offset;
    }
}
