using UnityEngine;

public static class FlightOverlay
{
    static void Line(Vector3 a,Vector3 b,Color c){GL.Color(c);GL.Vertex(a);GL.Vertex(b);}
    static void DrawEdge(FlightPlanner.Edge edge,Color color)
    {
        Vector3 previous=edge.from;
        for(int i=1;i<=4;i++){
            Vector3 next=FlightPlanner.FlightPoint(edge,i*FlightPlanner.Step/4);
            Line(previous,next,color);previous=next;
        }
    }
    static void Mark(Vector3 p,float size,Color c){
        Line(p-Vector3.right*size,p+Vector3.right*size,c);Line(p-Vector3.forward*size,p+Vector3.forward*size,c);
        Line(p-Vector3.up*size,p+Vector3.up*size,c);
    }
    public static void Draw(Material lines, FlightGraph graph, FlightPlanner.Decision decision, bool showGraph,
        bool inspect, int branch,int expansion, DeathReview review, int reviewFrame, bool reviewing,
        float railFlash, FlightPlanner.Model player, Vector3 railEnd)
    {
        bool overlay=decision!=null;
        lines.SetInt("_ZTest",(int)UnityEngine.Rendering.CompareFunction.Always);
        if(!lines.SetPass(0))return;GL.Begin(GL.LINES);
        if(overlay)
        {
            var d=decision;
            if(showGraph)for(int i=0;i<graph.nodes.Count;i++)foreach(int j in graph.links[i])
                if(j>i)Line(graph.nodes[i],graph.nodes[j],new Color(.18f,.3f,.35f,.45f));
            bool stepView=inspect && !reviewing && expansion>=0 && d.search!=null && expansion<d.search.expansions.Count;
            var step=stepView?d.search.expansions[expansion]:default;
            int start=stepView?step.firstChild:0,end=stepView?start+step.childCount:d.choices.Count;
            for(int i=start;i<end;i++){
                var e=d.choices[i];
                if(!stepView && e.parent!=-1 && !showGraph)continue;
                DrawEdge(e,e.firstShot>=0?new(.98f,.23f,.15f):new(.25f,.58f,.73f));
            }
            foreach(int i in d.route){var e=d.choices[i];DrawEdge(e,Color.green);Mark(e.to,.15f,Color.green);}
            if(stepView)for(int i=step.fromEdge;i>=0;i=d.choices[i].parent)DrawEdge(d.choices[i],Color.cyan);
            if(inspect && branch>=0 && branch<d.choices.Count){
                var b=d.choices[branch];DrawEdge(b,Color.magenta);Mark(b.to,.42f,Color.magenta);
            }
            for(int i=0;i<d.forecast.Count;i+=4){
                var f=d.forecast[i];
                if(f.visible){Mark(f.sightTarget,.1f,Color.yellow);
                    Line(f.eye+f.aim*.7f,f.eye+f.aim*Vector3.Distance(f.eye,f.sphere),new(.9f,.66f,.15f,.65f));}
            }
            if(d.crate.present){
                int id=-1;Vector3 previous=default;
                foreach(var f in d.forecast){
                    if(f.crateId!=id){id=f.crateId;previous=d.crate.Get(id).center;}
                    Line(previous,f.crate,Color.white);previous=f.crate;
                }
                if(id>=0)Mark(previous,.35f,Color.white);
            }
            if(d.continuation.Count>0 && d.forecast.Count>0){
                Vector3 last=d.forecast[d.forecast.Count-1].sphere;
                foreach(var f in d.continuation){Line(last,f.sphere,Color.white);last=f.sphere;}
            }
            if(d.choices.Count>0){
                Vector3 origin=d.choices[0].from;
                graph.surface.Distance(origin,out var normal,d.crate);
                Vector3 foot=origin-normal*graph.radius;
                Mark(origin,.35f,Color.cyan);Line(origin,foot,Color.white);
                Vector3 u=Vector3.Cross(normal,Mathf.Abs(normal.y)<.9f?Vector3.up:Vector3.right).normalized*.18f;
                Vector3 v=Vector3.Cross(normal,u);
                for(int i=0;i<12;i++){
                    float a=i*Mathf.PI/6,b=(i+1)*Mathf.PI/6;
                    Line(foot+u*Mathf.Cos(a)+v*Mathf.Sin(a),foot+u*Mathf.Cos(b)+v*Mathf.Sin(b),Color.white);
                }
            }
        }
        if(reviewing){
            var f=review.frames[reviewFrame];float length=Mathf.Max(3,Vector3.Distance(f.eye,f.sphere));
            ReviewRay(f.expectedEye,f.expectedAim,length,Color.yellow);
            ReviewRay(f.eye,f.aim,length,Color.cyan);
            Mark(f.expectedSphere,.25f,Color.yellow);Mark(f.sphere,.25f,Color.cyan);
            if(decision.alternateEnabled)ReviewRay(f.expectedEye,f.alternateAim,length,Color.magenta);
        }
        if(!inspect && railFlash>0)Line(player.eye+player.aim*.3f-Vector3.up*.08f,railEnd,Color.cyan);
        GL.End();
    }
    static void ReviewRay(Vector3 eye,Vector3 aim,float length,Color color){
        Mark(eye,.18f,color);var tip=eye+aim*length;Line(eye,tip,color);Mark(tip,.15f,color);
    }
}
