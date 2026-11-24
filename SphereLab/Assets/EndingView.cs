using UnityEngine;

public struct EndingView
{
    Vector3 start, destination, subject;
    Quaternion rotation;
    public EndingView(Transform camera,Vector3 sphere)
    {
        start=camera.position;rotation=camera.rotation;subject=sphere;
        Vector3 retreat=-camera.forward*.85f+camera.right*.45f;
        float travel=retreat.magnitude;
        if(Physics.SphereCast(start,.12f,retreat.normalized,out var hit,travel,1<<8))
            travel=Mathf.Max(0,hit.distance-.025f);
        destination=start+retreat.normalized*travel;
    }
    public void Apply(Transform camera,float seconds)
    {
        float blend=Mathf.SmoothStep(0,1,Mathf.Clamp01(seconds/.75f));
        camera.position=Vector3.Lerp(start,destination,blend);
        Vector3 direction=subject-camera.position;
        camera.rotation=direction.sqrMagnitude>.00001f
            ?Quaternion.Slerp(rotation,Quaternion.LookRotation(direction),blend):rotation;
    }
}