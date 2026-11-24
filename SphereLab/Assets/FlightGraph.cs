using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public struct FlightGrid
{
    public Vector3 firstNode, spacing;
    public Vector3Int counts;
    public static FlightGrid Hangar => new() {
        firstNode = new(-18, 1.25f, -15), spacing = new(3, 2.5f, 3), counts = new(13, 4, 11)
    };
}

public sealed class FlightGraph
{
    public readonly List<Vector3> nodes = new();
    public readonly List<List<int>> links = new();
    public readonly float radius;
    public readonly SurfaceWorld surface;

    public FlightGraph(FlightGrid grid, float radius)
    {
        this.radius = radius;
        if (!(grid.spacing.x > 0 && grid.spacing.y > 0 && grid.spacing.z > 0) ||
            !float.IsFinite(grid.firstNode.sqrMagnitude) || !float.IsFinite(grid.spacing.sqrMagnitude) ||
            grid.counts.x <= 0 || grid.counts.y <= 0 || grid.counts.z <= 0 || radius <= 0)
            throw new ArgumentException("Navigation spacing, counts and enemy radius must be positive.");

        surface=new SurfaceWorld(radius);
        Vector3 minimum=grid.firstNode-grid.spacing;
        Vector3 maximum=grid.firstNode+Vector3.Scale(grid.counts,grid.spacing);
        float spacing=Mathf.Clamp(Mathf.Min(grid.spacing.x,grid.spacing.y,grid.spacing.z),.6f,2.5f);
        var unique=new HashSet<Vector3Int>();
        foreach(var box in surface.boxes)
        for(int axis=0;axis<3;axis++){
            int u=(axis+1)%3,v=(axis+2)%3;
            int cols=Mathf.Max(1,Mathf.CeilToInt(2*box.half[u]/spacing));
            int rows=Mathf.Max(1,Mathf.CeilToInt(2*box.half[v]/spacing));
            for(int side=-1;side<=1;side+=2)
            for(int i=0;i<=cols;i++)for(int j=0;j<=rows;j++){
                Vector3 local=Vector3.zero;
                local[axis]=side*(box.half[axis]+radius+SurfaceWorld.Skin);
                local[u]=Mathf.Lerp(-box.half[u],box.half[u],(float)i/cols);
                local[v]=Mathf.Lerp(-box.half[v],box.half[v],(float)j/rows);
                if(!surface.Attach(box.center+box.rotation*local,radius+.08f,out var p))continue;
                if(p.x<minimum.x || p.y<minimum.y || p.z<minimum.z || p.x>maximum.x || p.y>maximum.y || p.z>maximum.z)continue;
                if(!unique.Add(Vector3Int.RoundToInt(p*20)))continue;
                nodes.Add(p);links.Add(new());
            }
        }
        if(nodes.Count==0)throw new ArgumentException("No solid surfaces in navigation bounds.");
        float range=spacing*1.48f;
        var buckets=new Dictionary<Vector3Int,List<int>>();
        for(int i=0;i<nodes.Count;i++){
            var p=nodes[i];var cell=Vector3Int.FloorToInt(p/range);
            // Keep nearby guides in distinct directions, including bridges at seams.
            var candidates=new List<int>();
            for(int x=-1;x<=1;x++)for(int y=-1;y<=1;y++)for(int z=-1;z<=1;z++)
                if(buckets.TryGetValue(cell+new Vector3Int(x,y,z),out var bucket))
                    foreach(int j in bucket)if((p-nodes[j]).sqrMagnitude<=range*range)candidates.Add(j);
            candidates.Sort((a,b)=>(p-nodes[a]).sqrMagnitude.CompareTo((p-nodes[b]).sqrMagnitude));
            var directions=new List<Vector3>();
            foreach(int j in candidates){
                Vector3 delta=nodes[j]-p;Vector3 direction=delta.normalized;
                if(directions.Exists(n=>Vector3.Dot(n,direction)>.975f))continue;
                int steps=Mathf.Max(1,Mathf.CeilToInt(delta.magnitude/.2f));
                bool supported=true;
                for(int k=1;k<steps && supported;k++)
                    supported=surface.Attach(p+delta*((float)k/steps),.20f,out _);
                if(!supported)continue;
                links[i].Add(j);links[j].Add(i);directions.Add(direction);
            }
            if(!buckets.TryGetValue(cell,out var own))buckets[cell]=own=new();
            own.Add(i);
        }
    }

    public bool RandomStarts(CargoPhysics.State crate, out Vector3 eye, out Vector3 enemy)
    {
        eye=enemy=default;
        for(int attempt=0;attempt<96;attempt++){
            int origin=UnityEngine.Random.Range(0,nodes.Count);
            Vector3 foot=nodes[origin];
            surface.Distance(foot,out var normal);
            if(normal.y<.9f || links[origin].Count==0)continue;
            var center=foot+Vector3.up*(.91f-radius-SurfaceWorld.Skin);
            var playerBox=new Bounds(center,new Vector3(.64f,1.8f,.64f));
            if(Physics.CheckBox(center,playerBox.extents,Quaternion.identity,1<<8))continue;
            bool occupied=false;
            for(int j=0;j<crate.Count;j++)occupied|=crate.Get(j).Box.Intersects(playerBox);
            if(occupied)continue;
            var queue=new Queue<int>();var seen=new HashSet<int>{origin};queue.Enqueue(origin);
            int selected=-1,count=0;
            while(queue.Count>0){
                int i=queue.Dequeue();
                foreach(int neighbor in links[i])if(seen.Add(neighbor))queue.Enqueue(neighbor);
                if((nodes[i]-foot).sqrMagnitude<100 || Physics.CheckSphere(nodes[i],radius,1<<8))continue;
                if(crate.present && crate.Get(crate.Nearest(nodes[i])).Box.SqrDistance(nodes[i])<=radius*radius)continue;
                if(UnityEngine.Random.Range(0,++count)==0)selected=i;
            }
            if(selected<0)continue;
            eye=center+Vector3.up*.60f;enemy=nodes[selected];return true;
        }
        return false;
    }

    public Vector3 Spawn(Vector3 requested)
    {
        int node=NearestVisible(requested);
        if(node<0)throw new ArgumentException("No reachable surface near spawn.");
        if(surface.Attach(requested,1f,out var attached) && FlightPlanner.Clear(requested,attached,radius-.005f))return attached;
        return nodes[node];
    }

    public int NearestVisible(Vector3 position)
    {
        int best = -1;
        float nearest = float.PositiveInfinity;
        for (int i = 0; i < nodes.Count; i++)
        {
            float distance = (nodes[i] - position).sqrMagnitude;
            if (distance >= nearest || !FlightPlanner.Clear(position, nodes[i], radius)) continue;
            best = i;
            nearest = distance;
            if (distance < .00001f) break;
        }
        return best;
    }
}
