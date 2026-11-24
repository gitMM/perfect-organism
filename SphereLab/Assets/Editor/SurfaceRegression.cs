using System;
using System.Collections.Generic;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class SurfaceRegression
{
    static void Traverse(SurfaceWorld world,ref Vector3 p,ref Vector3 velocity,Vector3 goal,string stage)
    {
        for(int i=0;i<320 && Vector3.Distance(p,goal)>.10f;i++){
            if(!world.Walk(p,goal,ref velocity,16,64,1f/64,false,out var next))
                throw new Exception($"Lost support during {stage} at {p}");
            for(int j=1;j<=4;j++){
                float d=world.Distance(Vector3.Lerp(p,next,j*.25f),out _);
                if(d<.478f || d>.592f)throw new Exception($"Invalid clearance {d} during {stage} at {p}");
            }
            p=next;
        }
        if(Vector3.Distance(p,goal)>.11f)throw new Exception($"Stalled during {stage} at {p}, goal {goal}");
    }
    public static void Run()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
        ArenaGeometry.Block("Floor",new(0,-.5f,0),new(14,1,14),Color.gray);
        ArenaGeometry.Block("Pillar",new(0,3,0),new(2,6,2),Color.gray);
        ArenaGeometry.Block("Ceiling",new(0,6.5f,0),new(14,1,14),Color.gray);
        var ramp=ArenaGeometry.Block("Inclined slab",new(16,3,0),new(6,.6f,6),Color.gray);
        ramp.transform.rotation=Quaternion.Euler(0,15,38);
        Physics.SyncTransforms();
        var graph=new FlightGraph(FlightGrid.Hangar,.48f);
        float r=.48f+SurfaceWorld.Skin;
        Vector3 p=new(4,r,0),v=Vector3.zero;
        Traverse(graph.surface,ref p,ref v,new(1+r+.08f,r+.08f,0),"floor to column base");
        Traverse(graph.surface,ref p,ref v,new(1+r,3,0),"vertical ascent");
        Traverse(graph.surface,ref p,ref v,new(0,3,1+r),"outside corner");
        Traverse(graph.surface,ref p,ref v,new(0,6-r-.08f,1+r+.08f),"column to ceiling");
        Traverse(graph.surface,ref p,ref v,new(0,6-r,4),"inverted traversal");
        var cornerPos=new Vector3(3.5f,r,0);
        var momentum=Vector3.left*9;
        float slowest=float.MaxValue;
        for(int i=0;i<64 && cornerPos.y<1.5f;i++){
            if(!graph.surface.Walk(cornerPos,new(1+r,4,0),ref momentum,16,64,1f/64,false,out var next,default,true))
                throw new Exception("Lost the moving floor-to-wall transition");
            slowest=Mathf.Min(slowest,(next-cornerPos).magnitude*64);
            cornerPos=next;
        }
        if(cornerPos.y<1.5f || slowest<7.2f)throw new Exception($"Moving corner stalled at {cornerPos}, minimum speed {slowest:F2} m/s");
        Debug.Log($"Moving floor-to-wall turn: entry 9 m/s, minimum {slowest:F2} m/s");
        cornerPos=new Vector3(1+r,4,0);momentum=Vector3.up*9;slowest=float.MaxValue;
        for(int i=0;i<64 && cornerPos.x<2.5f;i++){
            if(!graph.surface.Walk(cornerPos,new(4,6-r,0),ref momentum,16,64,1f/64,false,out var next,default,true))
                throw new Exception("Lost the wall-to-ceiling transition");
            slowest=Mathf.Min(slowest,(next-cornerPos).magnitude*64);
            cornerPos=next;
        }
        if(cornerPos.x<2.5f || slowest<7.2f)throw new Exception($"Moving ceiling turn stalled at {cornerPos}, minimum speed {slowest:F2} m/s");
        Debug.Log($"Moving wall-to-ceiling turn: entry 9 m/s, minimum {slowest:F2} m/s");
        // Check the search graph can also connect the same two sides of the room.
        int start=graph.NearestVisible(new(4,r,0)),end=graph.NearestVisible(p);
        var seen=new HashSet<int>{start};var queue=new Queue<int>();queue.Enqueue(start);
        while(queue.Count>0){int i=queue.Dequeue();foreach(int j in graph.links[i])if(seen.Add(j))queue.Enqueue(j);}
        if(!seen.Contains(end))throw new Exception("Floor and ceiling disconnected in surface graph");
        var normal=ramp.transform.up;Vector3 center=ramp.transform.position+normal*(r+.3f);
        p=center-ramp.transform.right*2;v=Vector3.zero;
        Traverse(graph.surface,ref p,ref v,center+ramp.transform.right*2,"38-degree surface");
        // An isolated crate offers its own support, even outside the static graph.
        var cargo=CargoPhysics.At(new Vector3(30,3,0));
        float reach=CargoPhysics.Half+r;
        p=cargo.center+Vector3.right*reach;v=Vector3.zero;
        for(int i=0;i<160;i++){
            Vector3 goal=cargo.center+Vector3.up*reach;
            if(!graph.surface.Walk(p,goal,ref v,16,64,1f/64,false,out var next,cargo))
                throw new Exception("Lost cargo side while climbing onto the top");
            Vector3 displacement=new(-.008f,-.003f,0);
            cargo.center+=displacement;p=next+displacement;
            if(Mathf.Abs(graph.surface.Distance(p,out _,cargo)-r)>.003f)
                throw new Exception("Lost contact with translating cargo");
        }
        if(Vector3.Distance(p,cargo.center+Vector3.up*reach)>.11f)
            throw new Exception("Failed to round the moving crate edge");
        Debug.Log("Moving crate surface traversal passed.");
        Debug.Log("Surface traversal passed: floor, pillar ascent, outside corner, inverted ceiling, inclined slab.");
        CheckEnvelope();
    }

    static void CheckEnvelope()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
        ArenaGeometry.Block("Floor",new(0,-.5f,0),new(40,1,40),Color.gray);
        ArenaGeometry.Block("Low ceiling",new(0,1.7f,0),new(5,.6f,5),Color.gray);
        ArenaGeometry.Block("Narrow pillar",new(8,2,0),new(1.5f,4,1),Color.gray);
        Physics.SyncTransforms();
        const float core=.48f;
        var visual=GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var form=new SphereEnvelope(new SurfaceWorld(core),core,visual);
        var open=new Vector3(-6,.505f,0);
        var eye=new Vector3(-6,1.5f,-8);
        form.Update(open,eye,default,0,true);
        if(Mathf.Abs(form.Width-core*3)>.001f || Mathf.Abs(form.Height-core*3)>.001f)
            throw new Exception("Open body did not reach triple diameter");
        var outerRay=eye+Vector3.right;
        if(!form.Rail(outerRay,Vector3.forward,default,out _) || !form.Touches(form.Center+Vector3.right*1.5f+Vector3.up*.45f))
            throw new Exception("Expanded outer body missing rail or contact collision");
        var under=new Vector3(0,.505f,0);
        form.Update(under,new Vector3(0,.6f,-8),default,1f/60);
        if(form.Center.y+form.Height>1.401f || form.Width<1.3f)
            throw new Exception("Low passage did not flatten the body");
        if(form.Rail(new Vector3(0,1.39f,-8),Vector3.forward,default,out _) && form.Center.y+form.Height<1.39f)
            throw new Exception("Rail struck above flattened mesh");
        float compressed=form.Height;
        form.Update(open,eye,default,1f/60);
        if(form.Height<=compressed || form.Height>=core*3)throw new Exception("Body did not animate its expansion");
        var hidden=new Vector3(8,.505f,2);
        eye=new Vector3(8,1.5f,-9);
        for(int i=0;i<60;i++)form.Update(hidden,eye,default,1f/60);
        if(form.Width>=core*2.6f || form.Rail(eye,(form.Center-eye).normalized,default,out _))
            throw new Exception("Body failed to tuck behind the narrow pillar");
        Debug.Log($"Envelope passed: full diameter {core*6:F2}, tunnel height {compressed*2:F2}, hidden width {form.Width*2:F2}");
    }
}
