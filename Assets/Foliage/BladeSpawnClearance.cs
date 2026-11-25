using System.Collections.Generic;
using UnityEngine;

namespace Foliage
{
    // Swept triangle SAT along world Y. Each pair forbids an interval of stalk-tip
    // heights. Leaving their union clears both collision skins and the contact envelope.
    sealed class BladeSpawnClearance
    {
        const float clearance=.0061f;
        struct Triangle
        {
            public Vector3 a,b,c,ab,bc,ca,normal,min,max;
            public Triangle(List<Vector3> nodes,PlantGeometry.Face face)
            {
                a=nodes[face.a];b=nodes[face.b];c=nodes[face.c];
                ab=b-a;bc=c-b;ca=a-c;normal=Vector3.Cross(ab,-ca);
                min=Vector3.Min(a,Vector3.Min(b,c));max=Vector3.Max(a,Vector3.Max(b,c));
            }
            public Vector3 Edge(int i)=>i==0?ab:i==1?bc:ca;
        }
        static bool Clip(in Triangle moving,in Triangle fixedFace,Vector3 axis,ref float lo,ref float hi)
        {
            float length=axis.magnitude;
            if(length<1e-12f)return true;
            axis/=length;
            float a=Vector3.Dot(axis,moving.a),b=Vector3.Dot(axis,moving.b),c=Vector3.Dot(axis,moving.c);
            float d=Vector3.Dot(axis,fixedFace.a),e=Vector3.Dot(axis,fixedFace.b),f=Vector3.Dot(axis,fixedFace.c);
            float low=Mathf.Min(d,Mathf.Min(e,f))-Mathf.Max(a,Mathf.Max(b,c))-clearance;
            float high=Mathf.Max(d,Mathf.Max(e,f))-Mathf.Min(a,Mathf.Min(b,c))+clearance;
            if(Mathf.Abs(axis.y)<1e-8f)return low<=0 && high>=0;
            float t0=low/axis.y,t1=high/axis.y;
            lo=Mathf.Max(lo,Mathf.Min(t0,t1));hi=Mathf.Min(hi,Mathf.Max(t0,t1));
            return lo<=hi;
        }
        static bool Interval(in Triangle m,in Triangle f,out Vector2 interval)
        {
            interval=default;
            if(m.min.x>f.max.x+clearance || f.min.x>m.max.x+clearance ||
               m.min.z>f.max.z+clearance || f.min.z>m.max.z+clearance)return false;
            float lo=f.min.y-m.max.y-clearance,hi=f.max.y-m.min.y+clearance;
            if(!Clip(m,f,m.normal,ref lo,ref hi)||!Clip(m,f,f.normal,ref lo,ref hi))return false;
            for(int i=0;i<3;i++)
            {
                // In-plane axes also separate coplanar triangles.
                if(!Clip(m,f,Vector3.Cross(m.normal,m.Edge(i)),ref lo,ref hi) ||
                   !Clip(m,f,Vector3.Cross(f.normal,f.Edge(i)),ref lo,ref hi))return false;
                for(int j=0;j<3;j++)
                    if(!Clip(m,f,Vector3.Cross(m.Edge(i),f.Edge(j)),ref lo,ref hi))return false;
            }
            interval=new Vector2(lo-.00001f,hi+.00001f);return true;
        }
        readonly float cellSize;
        readonly Dictionary<Vector2Int,List<int>> cells=new();
        readonly List<Triangle> placed=new();
        readonly List<int> seen=new();
        readonly List<Vector2> intervals=new();
        int visit;

        internal BladeSpawnClearance(float cellSize)
        {
            this.cellSize=Mathf.Max(cellSize,.01f);
        }
        Vector2Int Cell(Vector3 p)=>new Vector2Int(Mathf.FloorToInt(p.x/cellSize),Mathf.FloorToInt(p.z/cellSize));

        internal float Place(List<Vector3> nodes,List<PlantGeometry.Face> faces,int first)
        {
            var added=new Triangle[faces.Count-first];
            for(int i=0;i<added.Length;i++)added[i]=new Triangle(nodes,faces[first+i]);
            // Usually only the original height needs testing. Expand the vertical
            // window if neither exit from the forbidden interval is inside it.
            float window=clearance,offset;
            while(true)
            {
                intervals.Clear();
                foreach(var m in added)
                {
                    visit++;
                    var lo=Cell(m.min-Vector3.one*clearance);var hi=Cell(m.max+Vector3.one*clearance);
                    for(int x=lo.x;x<=hi.x;x++)
                    for(int z=lo.y;z<=hi.y;z++)
                    {
                        if(!cells.TryGetValue(new Vector2Int(x,z),out var group))continue;
                        foreach(int id in group)
                        {
                            if(seen[id]==visit)continue;
                            seen[id]=visit;
                            var f=placed[id];
                            if(m.min.y-window>f.max.y+clearance || m.max.y+window<f.min.y-clearance)continue;
                            if(Interval(m,f,out var range))intervals.Add(range);
                        }
                    }
                }
                offset=NearestExit();
                if(Mathf.Abs(offset)<=window)break;
                window=Mathf.Max(window*2,Mathf.Abs(offset)+clearance);
            }
            foreach(var triangle in added)
            {
                var t=triangle;
                Vector3 shift=Vector3.up*offset;
                t.a+=shift;t.b+=shift;t.c+=shift;t.min+=shift;t.max+=shift;
                var lo=Cell(t.min);var hi=Cell(t.max);
                for(int x=lo.x;x<=hi.x;x++)
                for(int z=lo.y;z<=hi.y;z++)
                {
                    var cell=new Vector2Int(x,z);
                    if(!cells.TryGetValue(cell,out var group))cells[cell]=group=new List<int>();
                    group.Add(placed.Count);
                }
                placed.Add(t);seen.Add(0);
            }
            return offset;
        }

        float NearestExit()
        {
            intervals.Sort((a,b)=>a.x.CompareTo(b.x));
            for(int i=0;i<intervals.Count;i++)
            {
                var merged=intervals[i];
                if(merged.x>0)return 0;
                while(i+1<intervals.Count && intervals[i+1].x<=merged.y)
                    merged.y=Mathf.Max(merged.y,intervals[++i].y);
                if(merged.y>=0)return -merged.x<merged.y?merged.x:merged.y;
            }
            return 0;
        }
    }
}
