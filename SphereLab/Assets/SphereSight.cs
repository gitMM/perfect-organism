using UnityEngine;

public static class SphereSight
{
    const int Solid=1<<8;

    public static float ObstructionRange(Vector3 eye,Vector3 aim,CargoPhysics.State cargo)
    {
        float range=Physics.Raycast(eye,aim,out var wall,80,Solid)?wall.distance:80;
        for(int i=0;i<cargo.Count;i++)if(cargo.Get(i).Box.IntersectRay(new Ray(eye,aim),out float crateRange))range=Mathf.Min(range,crateRange);
        return range;
    }

    // The rail stops at the first sphere surface or hangar surface along this ray.
    public static bool Rail(Vector3 eye, Vector3 aim, Vector3 center, out float range,float radius=FlightPlanner.Radius,CargoPhysics.State cargo=default)
    {
        Vector3 offset=center-eye;
        float along=Vector3.Dot(offset,aim);
        float disc=radius*radius-(offset-along*aim).sqrMagnitude;
        range=ObstructionRange(eye,aim,cargo);
        if(disc<0)return false;
        float half=Mathf.Sqrt(disc);
        if(along+half<0 || along-half>range)return false;
        range=Mathf.Max(0,along-half);return true;
    }

    // A convex blocker covering a square around the entire projected disk
    // before the sphere's nearest depth proves full cover for this arena.
    public static bool Exposed(Vector3 eye,Vector3 center,out Vector3 target,float radius=FlightPlanner.Radius,CargoPhysics.State cargo=default)
    {
        target=center;
        Vector3 offset=center-eye;float distance=offset.magnitude;
        if(distance<=radius)return true;
        bool staticHit=Physics.Linecast(eye,center,out var wall,Solid);
        int blocker=-1;float closest=distance;
        var centerRay=new Ray(eye,offset.normalized);
        for(int i=0;i<cargo.Count;i++)if(cargo.Get(i).Box.IntersectRay(centerRay,out float entry) && entry<closest){blocker=i;closest=entry;}
        bool crateHit=blocker>=0;
        if(!staticHit && !crateHit)return true;
        Vector3 forward=offset/distance;
        Vector3 right=Vector3.Cross(Mathf.Abs(forward.y)<.9f?Vector3.up:Vector3.right,forward).normalized;
        Vector3 up=Vector3.Cross(forward,right);
        float front=distance-radius;
        float half=front*radius/Mathf.Sqrt(distance*distance-radius*radius);
        bool covered=staticHit && wall.collider is BoxCollider;
        bool crateCovered=crateHit;
        for(int x=-1;x<=1 && (covered || crateCovered);x+=2)
        for(int y=-1;y<=1 && (covered || crateCovered);y+=2){
            Vector3 corner=forward*front+(right*x+up*y)*half;
            var ray=new Ray(eye,corner.normalized);
            covered=covered && wall.collider.Raycast(ray,out _,corner.magnitude);
            crateCovered=crateCovered && cargo.Get(blocker).Box.IntersectRay(ray,out float entry) && entry<corner.magnitude;
        }
        if(covered || crateCovered)return false;
        for(int i=0;i<8;i++){
            float angle=i*Mathf.PI/4;
            Vector3 direction=(forward*front+(right*Mathf.Cos(angle)+up*Mathf.Sin(angle))*half*.995f).normalized;
            if(Rail(eye,direction,center,out float entry,radius,cargo)){target=eye+direction*entry;break;}
        }
        // Treat uncertain seams as exposed, even when the eight rim rays miss.
        return true;
    }
}
