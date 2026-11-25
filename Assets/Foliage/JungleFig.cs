using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Foliage
{
    [Serializable]
    public class FigSettings
    {
        [Range(.6f, 1.4f)] public float size = 1;
        [Range(.55f, 1.4f)] public float crownSpread = 1;
        [Range(.5f, 1.5f)] public float buttressReach = 1;
        [Range(0, 1)] public float fusion = .45f;
        [Range(1,5)] public int canopyDensity=4;
        [Range(0, 1)] public float propFrequency = .35f;
    }

    // All coordinates are in metres. Generate owns no scene state or global random state.
    public sealed class JungleFig
    {
        readonly System.Random random;
        readonly int seedForFoliage;
        readonly FigSettings settings;
        readonly List<Vector3> vertices = new();
        readonly List<int> triangles = new();
        readonly List<Vector3> fieldNormals = new();
        // Material coordinates stay in metres relative to the wood's local growth guide.
        struct BarkSample
        {
            public Vector3 crossSection, direction;
            public float recess;
            public BarkSample(Vector3 crossSection,Vector3 direction)
            {
                this.crossSection=crossSection;
                this.direction=direction;
                recess=0;
            }
            public static BarkSample Blend(BarkSample a,BarkSample b,float t)
            {
                if(Vector3.Dot(a.direction,b.direction)<0)b.direction=-b.direction;
                return new BarkSample(Vector3.Lerp(a.crossSection,b.crossSection,t),
                    Vector3.Lerp(a.direction,b.direction,t)){recess=Mathf.Lerp(a.recess,b.recess,t)};
            }
        }
        readonly List<Vector3> grainCoordinates=new();
        readonly List<Vector4> grainDirections=new();
        readonly List<Vector4> barkAge=new();
        void AddBark(BarkSample bark,float maturity=0)
        {
            barkAge.Add(new Vector4(0,0,0,maturity));
            grainCoordinates.Add(bark.crossSection*settings.size);
            Vector3 d=bark.direction.normalized;
            grainDirections.Add(new Vector4(d.x,d.y,d.z,bark.recess));
        }
        // The smooth union's weights also carry material data across each wood junction.
        static void Fuse(float[] field,BarkSample[] bark,int cell,float distance,BarkSample sample,float blend,bool crease)
        {
            float old=field[cell];
            if(distance>=old+blend)return;
            float weight=Mathf.Clamp01(.5f+(old-distance)/(2*blend));
            var mixed=BarkSample.Blend(bark[cell],sample,weight);
            if(crease) mixed.recess=Mathf.Max(mixed.recess,4*weight*(1-weight));
            bark[cell]=mixed;
            field[cell]=Mathf.Lerp(old,distance,weight)-blend*weight*(1-weight);
        }
        readonly List<Root> roots = new();
        readonly List<(Vector3 a,Vector3 b,float ra,float rb)> limbs = new();
        readonly Vector3 lean;
        readonly float baseHeight, footRadius, girth, spread;
        readonly float openness;
        readonly Vector3 passageSide;
        struct Root { public float angle, twist, length, width, phase; }
        float Range(float a, float b) => Mathf.Lerp(a,b,(float)random.NextDouble());
        static Vector3 Radial(float angle) => new(Mathf.Cos(angle),0,Mathf.Sin(angle));

        JungleFig(int seed, FigSettings settings)
        {
            this.settings=settings;
            seedForFoliage=seed;
            uint hash=unchecked((uint)seed*2654435761u);
            hash^=hash>>16;
            random=new System.Random(unchecked((int)hash));
            baseHeight=Range(6,11);
            footRadius=Range(1.6f,3.8f);
            girth=Range(1.0f,1.65f);
            spread=Range(.65f,1.35f)*settings.crownSpread;
            lean=Radial(Range(0,2*Mathf.PI))*Range(.15f,2.7f);
            // Shape choices use their own stream so root development preserves the crown.
            var lower=new System.Random(unchecked((int)(hash ^ 0x53b190a7u)));
            openness=Mathf.Clamp01((float)lower.NextDouble()*1.55f-.25f);
            float heading=((float)lower.NextDouble()-.5f)*1.05f;
            passageSide=Radial(heading);
            int count=random.Next(4,8);
            float phase=Range(0,6.28f);
            for(int i=0;i<count;i++)
                roots.Add(new Root {angle=phase+6.283f*(i+Range(-.18f,.18f))/count,
                    twist=Range(-.6f,.6f), length=Range(1.2f,4.6f)*settings.buttressReach,
                    width=Range(.72f,1.22f), phase=Range(0,6.28f)});
        }

        List<FigShoot> shoots;
        public static Mesh Generate(int seed, FigSettings settings)=>Generate(seed,settings,null);

        // With shoot output supplied, each flexible subtree is emitted once, separately from fixed wood.
        public static Mesh Generate(int seed, FigSettings settings,List<FigShoot> shoots)
            => FigFileCache.LoadOrGenerate(seed, settings, shoots);

        internal static Mesh GenerateFresh(int seed, FigSettings settings,List<FigShoot> shoots)
        {
            var fig=new JungleFig(seed,settings){shoots=shoots};
            int firstShoot=shoots?.Count??0;
            fig.BuildCrown();
            fig.triangles.RemoveAll(index=>index<0);
            if(shoots!=null)
                for(int i=firstShoot;i<shoots.Count;i++)shoots[i].geometry.surface.RemoveAll(face=>face.rod==-2);
            fig.BuildBase();
            var mesh=new Mesh {name="Jungle fig "+seed, indexFormat=IndexFormat.UInt32};
            for(int i=0;i<fig.vertices.Count;i++) fig.vertices[i]*=settings.size;
            mesh.SetVertices(fig.vertices);
            mesh.SetUVs(0,fig.grainCoordinates);
            mesh.SetUVs(1,fig.grainDirections);
            mesh.SetUVs(2,fig.barkAge);
            mesh.SetTriangles(fig.triangles,0);
            mesh.RecalculateNormals();
            var normals=mesh.normals;
            fig.fieldNormals.CopyTo(normals,normals.Length-fig.fieldNormals.Count);
            mesh.normals=normals;
            if(shoots!=null)
                for(int i=firstShoot;i<shoots.Count;i++)shoots[i].StoreBoundaryNormals(normals);
            mesh.RecalculateBounds();
            return mesh;
        }

        // Each horizontal slice contains several eccentric lobes. Their long axes
        // flatten into buttress walls near soil and converge into a fluted column above.
        void BuildBase()
        {
            const float step=.24f;
            float half=footRadius+10f*settings.buttressReach+lean.magnitude+2;
            float top=baseHeight+2;
            foreach(var limb in limbs){
                foreach(var p in new[]{limb.a,limb.b}){
                    half=Mathf.Max(half,Mathf.Max(Mathf.Abs(p.x),Mathf.Abs(p.z))+limb.ra+1);
                    top=Mathf.Max(top,p.y+limb.ra+2);
                }
            }
            int n=Mathf.CeilToInt(2*half/step)+1;
            int levels=Mathf.CeilToInt(top/step)+1;
            var field=new float[n*n*levels];
            var bark=new BarkSample[field.Length];
            Vector3 origin=new(-half,-.48f,-half);
            for(int y=0;y<levels;y++)
            {
                float h=origin.y+y*step, t=Mathf.Clamp01(h/baseHeight);
                float cap=Mathf.Sqrt(Mathf.Max(0,1-Mathf.Pow(Mathf.Max(0,h-baseHeight)/1.3f,2)));
                int layer=y*n*n;
                Array.Fill(field,10f,layer,n*n);
                if(h>baseHeight+1.3f)continue;
                foreach(var root in roots)
                {
                    Vector3 u=Radial(root.angle+root.twist*t+.09f*Mathf.Sin(root.phase+5*t));
                    float lower=1-Mathf.SmoothStep(0,1,Mathf.InverseLerp(.23f,.76f,t));
                    Vector3 center=ColumnCenter(root,t,u,lower);
                    float next=Mathf.Clamp01(t+.01f);
                    Vector3 nextAxis=Radial(root.angle+root.twist*next+.09f*Mathf.Sin(root.phase+5*next));
                    float nextLower=1-Mathf.SmoothStep(0,1,Mathf.InverseLerp(.23f,.76f,next));
                    Vector3 growth=(Vector3.up*(baseHeight*.01f)+ColumnCenter(root,next,nextAxis,nextLower)-center).normalized;
                    float ridgeHeight=.54f+.16f*Mathf.Cos(root.phase);
                    float ridgeTaper=Mathf.SmoothStep(0,1,Mathf.InverseLerp(ridgeHeight,0,t));
                    float a=girth*root.width*(1+.24f*lower)*cap;
                    float b=girth*root.width*(.57f+.25f*t+.28f*settings.fusion)*cap;
                    float bound=Mathf.Max(a,b)+.8f;
                    int x0=Mathf.Max(0,Mathf.FloorToInt((center.x-bound+half)/step));
                    int x1=Mathf.Min(n-1,Mathf.CeilToInt((center.x+bound+half)/step));
                    int z0=Mathf.Max(0,Mathf.FloorToInt((center.z-bound+half)/step));
                    int z1=Mathf.Min(n-1,Mathf.CeilToInt((center.z+bound+half)/step));
                    if(cap<.001f) continue;
                    for(int z=z0;z<=z1;z++) for(int x=x0;x<=x1;x++)
                    {
                        float dx=origin.x+x*step-center.x,dz=origin.z+z*step-center.z;
                        float along=(dx*u.x+dz*u.z)/a, across=(dz*u.x-dx*u.z)/b;
                        float d=(Mathf.Sqrt(along*along+across*across)-1)*b;
                        // Two long folds turn with each column and fade into the upper trunk.
                        float angle=Mathf.Atan2(across,along);
                        d-=lower*b*(.13f*Mathf.Cos(3*angle+root.phase+t*2)+.08f*Mathf.Cos(5*angle-t*3));
                        // A shallow rib winds up one side of each existing root column.
                        float ridgeAngle=angle-root.phase-.38f*Mathf.Sin(t*5+root.phase);
                        // Leave the tight, inward-facing passage surface at its original thickness.
                        float outward=Vector3.Dot(new Vector3(dx,0,dz),passageSide)*Mathf.Sign(Vector3.Dot(center,passageSide));
                        float exposed=Mathf.SmoothStep(0,1,Mathf.InverseLerp(.12f,.65f,outward));
                        d-=.11f*exposed*ridgeTaper*Mathf.Exp(-12*(1-Mathf.Cos(ridgeAngle)));
                        d=Mathf.Max(d,-h-.22f);
                        int k=layer+z*n+x;
                        float blend=.16f+.48f*settings.fusion;
                        Fuse(field,bark,k,d,new BarkSample(new Vector3(dx,0,dz),growth),blend,true);
                    }
                }
            }
            foreach(var root in roots) AddButtress(field,bark,n,levels,origin,step,root);
            foreach(var limb in limbs)
            {
                Vector3 axis=limb.b-limb.a;
                float pad=Mathf.Max(limb.ra,limb.rb)+.65f;
                Vector3 lo=(Vector3.Min(limb.a,limb.b)-Vector3.one*pad-origin)/step;
                Vector3 hi=(Vector3.Max(limb.a,limb.b)+Vector3.one*pad-origin)/step;
                for(int y=Mathf.Max(1,Mathf.FloorToInt(lo.y));y<=Mathf.Min(levels-1,Mathf.CeilToInt(hi.y));y++)
                for(int z=Mathf.Max(0,Mathf.FloorToInt(lo.z));z<=Mathf.Min(n-1,Mathf.CeilToInt(hi.z));z++)
                for(int x=Mathf.Max(0,Mathf.FloorToInt(lo.x));x<=Mathf.Min(n-1,Mathf.CeilToInt(hi.x));x++)
                {
                    Vector3 delta=origin+step*new Vector3(x,y,z)-limb.a;
                    float t=Mathf.Clamp01(Vector3.Dot(delta,axis)/axis.sqrMagnitude);
                    float r=Mathf.Lerp(limb.ra,limb.rb,t);
                    float d=(delta-t*axis).magnitude-r;
                    int k=x+n*(z+n*y);
                    float blend=.08f*r;
                    Fuse(field,bark,k,d,new BarkSample(Vector3.ProjectOnPlane(delta,axis),axis.normalized),blend,false);
                }
            }
            Polygonize(field,bark,n,levels,origin,step);
        }

        Vector3 ColumnCenter(Root root,float t,Vector3 u,float lower)
        {
            Vector3 axis=lean*(t*t);
            Vector3 foot=ColumnFoot(root);
            Vector3 column=u*(footRadius*Mathf.Pow(1-t,1.05f)+.35f);
            Vector3 center=axis+column+lower*(foot-Radial(root.angle)*(footRadius+.35f));
            Vector3 sideways=new(-u.z,0,u.x);
            center+=sideways*(Mathf.Sin(Mathf.PI*t)*lower*.75f*Mathf.Sin(root.phase+4*t));
            return center;
        }

        Vector3 ColumnFoot(Root root)
        {
            Vector3 radial=Radial(root.angle);
            Vector3 foot=radial*(footRadius+.35f)*Mathf.Lerp(.30f,1.08f,openness);
            float side=Vector3.Dot(foot,passageSide);
            float gap=openness*openness*2.25f;
            return foot+passageSide*(Mathf.Sign(side)*Mathf.Max(0,gap-Mathf.Abs(side)));
        }

        // Sweep a thin wall under a curved crest. Width, crest elevation, and
        // plan curvature vary independently. The buried skirt closes the mesh.
        void AddButtress(float[] field,BarkSample[] bark,int n,int levels,Vector3 origin,float step,Root root)
        {
            Vector3 foot=ColumnFoot(root);
            Vector3 outwards=Radial(root.angle);
            Vector3 lateral=new(-outwards.z,0,outwards.x);
            float length=root.length*1.82f+.6f*settings.buttressReach;
            float curl=.2f+.23f*Mathf.Sin(root.phase);
            float shoulder=baseHeight*(.36f+.11f*Mathf.Cos(root.phase));
            Vector3 Path(float t)=>foot+outwards*(length*t)+lateral*(length*curl*
                (Mathf.Sin(t*4+root.phase)-Mathf.Sin(root.phase))*.53f*t);
            const int spans=12;
            var slice=new float[n*n];
            var surface=new BarkSample[n*n];
            int ymax=Mathf.Min(levels-1,Mathf.CeilToInt((shoulder+1f-origin.y)/step));
            for(int y=1;y<=ymax;y++){
                Array.Fill(slice,10f);
                float h=origin.y+y*step,level=Mathf.Clamp01(h/baseHeight);
                float lower=1-Mathf.SmoothStep(0,1,Mathf.InverseLerp(.23f,.76f,level));
                Vector3 u=Radial(root.angle+root.twist*level+.09f*Mathf.Sin(root.phase+5*level));
                Vector3 shift=ColumnCenter(root,level,u,lower)-foot;
                for(int i=0;i<spans;i++){
                    float t0=i/(float)spans,t1=(i+1)/(float)spans;
                    Vector3 a=Path(t0)+shift*(1-t0),b=Path(t1)+shift*(1-t1),delta=b-a;
                    float pad=girth*root.width+1;
                    Vector3 lo=(Vector3.Min(a,b)-Vector3.one*pad-origin)/step;
                    Vector3 hi=(Vector3.Max(a,b)+Vector3.one*pad-origin)/step;
                    for(int z=Mathf.Max(0,Mathf.FloorToInt(lo.z));z<=Mathf.Min(n-1,Mathf.CeilToInt(hi.z));z++)
                    for(int x=Mathf.Max(0,Mathf.FloorToInt(lo.x));x<=Mathf.Min(n-1,Mathf.CeilToInt(hi.x));x++){
                        Vector3 offset=new(origin.x+x*step-a.x,0,origin.z+z*step-a.z);
                        float local=Mathf.Clamp01(Vector3.Dot(offset,delta)/delta.sqrMagnitude);
                        float t=Mathf.Lerp(t0,t1,local),taper=1-t;
                        float dist=(offset-delta*local).magnitude;
                        float crest=shoulder*Mathf.Pow(taper,1.45f)+.24f*Mathf.Sin(t*Mathf.PI*2)*taper;
                        float width=.23f+girth*root.width*(.22f+.46f*taper)*taper;
                        float v=Mathf.Max(0,h+.18f)/(.18f+crest);
                        float d=width*(Mathf.Sqrt(dist*dist/(width*width)+v*v)-1);
                        int k=x+n*z;
                        d=Mathf.Max(Mathf.Max(d,h-crest),-h-.22f);
                        if(d>=slice[k])continue;
                        slice[k]=d;
                        Vector3 cross=offset-delta*local;
                        float slope=(-1.45f*shoulder*Mathf.Pow(taper,.45f)+.24f*(6.283f*Mathf.Cos(6.283f*t)*taper-Mathf.Sin(6.283f*t)))/length;
                        float relativeHeight=Mathf.Clamp01((h+.18f)/(crest+.18f));
                        Vector3 growth=(-delta.normalized-Vector3.up*(slope*relativeHeight)).normalized;
                        cross.y=h-.65f*crest;
                        // Return to the column chart as the buttress disappears into the trunk.
                        Vector3 columnOffset=origin+new Vector3(x*step,0,z*step)-(foot+shift);
                        columnOffset.y=0;
                        float wallWeight=Mathf.SmoothStep(0,1,Mathf.InverseLerp(.04f,.55f,t));
                        surface[k]=new BarkSample(Vector3.Lerp(columnOffset,cross,wallWeight),growth);
                    }
                }
                for(int k=0;k<slice.Length;k++){
                    if(slice[k]>=1)continue;
                    int cell=k+n*n*y;
                    const float blend=.55f;
                    Fuse(field,bark,cell,slice[k],surface[k],blend,true);
                }
            }
        }

        void Polygonize(float[] f,BarkSample[] bark,int n,int levels,Vector3 origin,float step)
        {
            int plane=n*n;
            int[] corners={0,1,1+n,n,plane,plane+1,plane+1+n,plane+n};
            int[,] tetra={{0,1,2,6},{0,2,3,6},{0,3,7,6},{0,7,4,6},{0,4,5,6},{0,5,1,6}};
            var cache=new Dictionary<ulong,int>();
            Vector3 Point(int i)=>origin+new Vector3(i%n,i/plane,(i/n)%n)*step;
            Vector3 Gradient(int i)
            {
                int x=i%n,z=(i/n)%n,y=i/plane;
                return new Vector3(
                    f[x+1<n?i+1:i]-f[x>0?i-1:i],
                    f[y+1<levels?i+plane:i]-f[y>0?i-plane:i],
                    f[z+1<n?i+n:i]-f[z>0?i-n:i]);
            }
            int Crossing(int a,int b)
            {
                ulong key=((ulong)(uint)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);
                if(cache.TryGetValue(key,out int v))return v;
                v=vertices.Count;
                float t=f[a]/(f[a]-f[b]);
                vertices.Add(Vector3.Lerp(Point(a),Point(b),t));
                AddBark(BarkSample.Blend(bark[a],bark[b],t));
                fieldNormals.Add(Vector3.Lerp(Gradient(a),Gradient(b),t).normalized);
                cache.Add(key,v);
                return v;
            }
            void Face(int a,int b,int c,Vector3 outward)
            {
                if(Vector3.Dot(Vector3.Cross(vertices[b]-vertices[a],vertices[c]-vertices[a]),outward)<0)
                    (b,c)=(c,b);
                triangles.Add(a);triangles.Add(b);triangles.Add(c);
            }
            int[] solid=new int[4],air=new int[4];
            for(int y=0;y<levels-1;y++)for(int z=0;z<n-1;z++)for(int x=0;x<n-1;x++)
            {
                int p=x+n*z+plane*y;
                if(f[p]>1f && f[p+1]>1f && f[p+n]>1f && f[p+n+1]>1f &&
                    f[p+plane]>1f && f[p+plane+1]>1f && f[p+plane+n]>1f && f[p+plane+n+1]>1f)continue;
                bool boundary=false;
                for(int j=1;j<8;j++)if((f[p]<0)!=(f[p+corners[j]]<0)){boundary=true;break;}
                if(!boundary)continue;
                for(int j=0;j<6;j++)
                {
                    int s=0,e=0;
                    for(int k=0;k<4;k++){
                        int id=p+corners[tetra[j,k]];
                        if(f[id]<0)solid[s++]=id;else air[e++]=id;
                    }
                    if(s==0||e==0)continue;
                    Vector3 outDir=Point(air[0])-Point(solid[0]);
                    if(s==2){
                        int a=Crossing(solid[0],air[0]),b=Crossing(solid[0],air[1]);
                        int c=Crossing(solid[1],air[0]),d=Crossing(solid[1],air[1]);
                        Face(a,b,c,outDir);Face(c,b,d,outDir);
                    } else {
                        var one=s==1?solid:air;var three=s==1?air:solid;
                        Face(Crossing(one[0],three[0]),Crossing(one[0],three[1]),Crossing(one[0],three[2]),outDir);
                    }
                }
            }
        }

        void BuildCrown()
        {
            int leaders=random.Next(3,7);
            float heading=Range(0,6.283f);
            for(int i=0;i<leaders;i++)
            {
                float angle=heading+6.283f*(i+Range(-.2f,.2f))/leaders;
                float h=i==0?baseHeight:baseHeight*Range(.57f,.98f);
                Vector3 start=lean*Mathf.Pow(h/baseHeight,2)+Vector3.up*h;
                bool lateral=i==1 || (i>1 && Range(0,1)<.22f);
                var dir=(Radial(angle)*(lateral?1.9f:Range(.38f,1.1f))*spread+Vector3.up*(lateral?.48f:1)).normalized;
                Branch(start,dir,Range(5.3f,9.4f),girth*(i==0?.9f:Range(.51f,.8f)),5,lateral);
            }
        }

        void Branch(Vector3 start,Vector3 dir,float length,float radius,int depth,bool canProp,FigShoot shoot=null,int attachment=-1,Socket inlet=null)
        {
            Vector3 endDir=(dir+Vector3.up*Range(-.15f,.52f)+Radial(Range(0,6.28f))*.32f).normalized;
            Vector3 mid=start+dir*(length*.52f), end=mid+endDir*(length*.48f);
            if(shoot==null && shoots!=null){
                // A short collar lies inside the trunk. Its end ring is the only
                // fixed surface on this leader; every exposed span is a live rod.
                var collar=new Section{rings=7,vertex=vertices.Count,face=triangles.Count/3};
                Vector3 embed=dir*radius*.45f;
                Tube(start-embed,start-.5f*embed,start,radius,radius,forceRing:true);
                inlet=TipSocket(collar);
                shoot=new FigShoot(start*settings.size,embed*settings.size,radius*settings.size,settings,unchecked(seedForFoliage+7919*shoots.Count));
                attachment=shoot.geometry.rods[0].b;
                shoots.Add(shoot);
            }
            float tipRadius=depth==0?Mathf.Min(.012f,radius*.65f):radius*.68f;
            int[] path=null;
            var section=new Section{
                shoot=shoot, rings=shoot==null?7:6,
                vertex=shoot==null?vertices.Count:shoot.geometry.skin.Count,
                face=shoot==null?triangles.Count/3:shoot.geometry.surface.Count
            };
            if(shoot==null)Tube(start,mid,end,radius,depth==0?.025f:tipRadius);
            else path=shoot.AddTwig(start*settings.size,mid*settings.size,end*settings.size,
                radius*settings.size,tipRadius*settings.size,attachment);
            bool ringMesh=shoot!=null || (depth==0?.025f:tipRadius)<.23f;
            if(inlet!=null && ringMesh)Join(section,inlet);
            var aerial=canProp && shoot!=null?new List<(float t,Vector3 tip,float radius,bool rooted)>():null;
            if(canProp && Range(0,1)<settings.propFrequency){
                Vector3 drift=Radial(Range(0,6.28f))*Range(.04f,.18f);
                float t=Range(.60f,.84f);
                Vector3 control=2*mid-(start+end)*.5f;
                Vector3 top=(1-t)*(1-t)*start+2*(1-t)*t*control+t*t*end;
                top.y-=radius*.48f;
                Vector3 foot=new Vector3(top.x,-.16f,top.z)+drift;
                float girth=Mathf.Clamp(radius*.13f,.045f,.13f);
                if(shoot==null)Tube(top,Vector3.Lerp(top,foot,.5f)+drift*.18f,foot,girth,girth*.84f,true);
                else aerial.Add((t,foot,girth,true));

            }
            if(canProp){
                int hanging=random.Next(1,5);
                for(int i=0;i<hanging;i++){
                    float t=Range(.38f,.94f);
                    Vector3 control=mid*2-(start+end)*.5f;
                    Vector3 top=(1-t)*(1-t)*start+2*(1-t)*t*control+t*t*end;
                    Vector3 tip=top-Vector3.up*(top.y*Range(.4f,.88f))+Radial(Range(0,6.28f))*.26f;
                    float rootRadius=Range(.018f,.045f);
                    if(shoot==null)Tube(top,Vector3.Lerp(top,tip,.5f)+dir*.12f,tip,rootRadius,.009f,true);
                    else aerial.Add((t,tip,rootRadius,false));
                }
            }
            if(depth==0){
                shoot?.GrowLeafSprays(path,depth,length*settings.size);
                return;
            }
            var side=Vector3.Cross(endDir,Vector3.up).normalized;
            var normal=Vector3.Cross(endDir,side);
            float fork=Range(0,6.283f);
            for(int i=0;i<2;i++){
                float a=fork+i*Mathf.PI+Range(-.3f,.3f);
                Vector3 outward=Mathf.Cos(a)*side+Mathf.Sin(a)*normal;
                Vector3 next=(endDir+outward*Range(.4f,1.2f)+Vector3.up*.13f).normalized;
                float fraction=i==0?1:Range(.45f,.96f);
                Vector3 child=Vector3.Lerp(mid,end,fraction);
                int node=-1;
                float attachmentT=.5f+.5f*fraction;
                if(ringMesh && i==1)
                    ShapeSideOutlet(section,path,attachmentT,start,mid,end,Mathf.Lerp(radius,tipRadius,attachmentT));
                if(shoots!=null || ringMesh){
                    float t=attachmentT;
                    if(shoot!=null){
                        node=path[i==0?section.rings:section.rings-2];
                        child=(shoot.origin+shoot.geometry.nodes[node])/settings.size;
                    }else{
                        Vector3 control=2*mid-(start+end)*.5f;
                        child=(1-t)*(1-t)*start+2*(1-t)*t*control+t*t*end;
                    }
                }
                Socket outlet=null;
                if(ringMesh)outlet=i==0?TipSocket(section):SideSocket(section,next);
                Branch(child,next,length*Range(.56f,.79f),radius*(i==0?.68f:.47f),depth-1,false,shoot,node,outlet);
            }
            if(depth<=2)shoot?.GrowLeafSprays(path,depth,length*settings.size);
            if(depth>=2 && depth<=4)shoot?.GrowCrownLaterals(path,length*settings.size);
            // Fork shaping has now set the final rod positions. Attach roots to
            // their nearest actual bough node, after all junction edits.
            if(aerial!=null)foreach(var root in aerial){
                Vector3 center=(1-root.t)*(1-root.t)*start+2*(1-root.t)*root.t*(2*mid-(start+end)*.5f)+root.t*root.t*end;
                int best=path[0];float closest=float.PositiveInfinity;
                foreach(int node in path){
                    float d=(shoot.geometry.nodes[node]+shoot.origin-center*settings.size).sqrMagnitude;
                    if(d<closest){best=node;closest=d;}
                }
                shoot.AddAerialRoot(best,(root.tip-center)*settings.size,root.radius*settings.size,root.rooted,root.tip.y*settings.size);
            }
        }

        // A fork opens an existing tube. Its child uses the perimeter of that opening
        // as its first ring. Only a solver ownership change needs duplicate vertices.
        const int Sides=12;
        struct Section { public FigShoot shoot; public int rings,vertex,face; }
        sealed class Socket
        {
            public Section parent;
            public readonly int[] rim=new int[Sides];
        }
        Vector3 TreePoint(Section section,int vertex)=>section.shoot==null
            ?vertices[vertex]*settings.size:section.shoot.origin+section.shoot.geometry.skin[vertex];

        void Omit(Section s,int face){
            if(s.shoot==null){
                for(int i=0;i<3;i++)triangles[3*face+i]=-1;
            }else{
                var f=s.shoot.geometry.surface[face];f.rod=-2;s.shoot.geometry.surface[face]=f;
            }
        }
        void OmitCap(Section s,bool tip){
            int first=s.face+2*s.rings*Sides;
            if(s.shoot==null){
                // Fixed tube caps are interleaved triangle fans.
                for(int i=0;i<Sides-2;i++)Omit(s,first+2*i+(tip?1:0));
            }else{
                for(int i=0;i<Sides;i++)Omit(s,first+(tip?Sides:0)+i);
            }
        }
        Socket TipSocket(Section s){
            var socket=new Socket{parent=s};
            for(int i=0;i<Sides;i++)socket.rim[i]=s.vertex+s.rings*Sides+i;
            OmitCap(s,true);
            return socket;
        }
        // Concentrate three rings around the real branch origin. This makes the
        // collar proportional to the wood diameter even on long, sparsely sampled limbs.
        void ShapeSideOutlet(Section s,int[] path,float t,Vector3 a,Vector3 mid,Vector3 b,float radius){
            Vector3 control=2*mid-(a+b)*.5f;
            Vector3 Center(float u)=>(1-u)*(1-u)*a+2*u*(1-u)*control+u*u*b;
            float speed=(2*Vector3.Lerp(control-a,b-control,t)).magnitude;
            float half=Mathf.Min(.85f*radius/Mathf.Max(speed,.001f),.7f*(1-t));
            int center=s.rings-2;
            var points=new Vector3[(s.rings+1)*Sides];
            for(int j=0;j<points.Length;j++)points[j]=TreePoint(s,s.vertex+j);
            for(int ring=center-1;ring<=center+1;ring++){
                float u=t+(ring-center)*half;
                int lo=Mathf.Min(Mathf.FloorToInt(u*s.rings),s.rings-1);
                float mix=u*s.rings-lo;
                Vector3 origin=Center(u)*settings.size;
                Vector3 axis=Vector3.Lerp(control-a,b-control,u).normalized;
                Vector3 oldCenter=Vector3.Lerp(Center(lo/(float)s.rings),Center((lo+1)/(float)s.rings),mix)*settings.size;
                if(s.shoot!=null)s.shoot.geometry.nodes[path[ring]]=origin-s.shoot.origin;
                for(int k=0;k<Sides;k++){
                    int index=s.vertex+ring*Sides+k;
                    Vector3 offset=Vector3.Lerp(points[lo*Sides+k],points[(lo+1)*Sides+k],mix)-oldCenter;
                    Vector3 p=origin+offset;
                    if(s.shoot!=null)s.shoot.geometry.skin[index]=p-s.shoot.origin;
                    else{
                        vertices[index]=p/settings.size;grainCoordinates[index]=offset;
                        grainDirections[index]=new Vector4(axis.x,axis.y,axis.z,0);
                        barkAge[index]=new Vector4(0,0,0,FigShoot.YoungWood(offset.magnitude));
                    }
                }
            }
        }
        Socket SideSocket(Section s,Vector3 outgoing){
            int band=s.rings-3;
            int first=s.vertex+band*Sides;
            int facing=0;
            float best=float.NegativeInfinity;
            for(int k=0;k<Sides;k++){
                float score=Vector3.Dot(TreePoint(s,first+Sides+k),outgoing);
                if(score>best){facing=k;best=score;}
            }
            int start=facing+Sides-2;
            var socket=new Socket{parent=s};
            for(int k=0;k<5;k++){
                socket.rim[k]=first+(start+k)%Sides;
                socket.rim[10-k]=first+2*Sides+(start+k)%Sides;
                if(k==4)continue;
                for(int row=0;row<2;row++){
                    int face=s.face+2*((band+row)*Sides+(start+k)%Sides);
                    Omit(s,face);Omit(s,face+1);
                }
            }
            socket.rim[5]=first+Sides+(start+4)%Sides;
            socket.rim[11]=first+Sides+start%Sides;
            return socket;
        }
        void Join(Section child,Socket socket){
            var parent=socket.parent;
            OmitCap(child,false);
            // Rotate the ring correspondence to minimise twisting of the first span.
            float best=float.PositiveInfinity;
            int rotation=0;
            for(int offset=0;offset<Sides;offset++){
                float cost=0;
                for(int k=0;k<Sides;k++)
                    cost+=(TreePoint(child,child.vertex+Sides+k)-TreePoint(parent,socket.rim[(k+offset)%Sides])).sqrMagnitude;
                if(cost<best){best=cost;rotation=offset;}
            }
            bool separate=parent.shoot!=child.shoot;
            int Map(int index){
                int slot=index-child.vertex;
                return slot>=0 && slot<Sides?socket.rim[(slot+rotation)%Sides]:index;
            }
            if(separate){
                for(int k=0;k<Sides;k++){
                    int dst=child.vertex+k,src=Map(dst);
                    var shoot=child.shoot;
                    shoot.geometry.skin[dst]=TreePoint(parent,src)-shoot.origin;
                    shoot.SetBoundary(k,src,grainCoordinates[src],grainDirections[src],barkAge[src].w);
                }
            }else{
                for(int f=child.face;f<child.face+2*Sides;f++){
                    if(child.shoot==null){
                        for(int j=3*f;j<3*f+3;j++)triangles[j]=Map(triangles[j]);
                    }else{
                        var face=child.shoot.geometry.surface[f];
                        face.a=Map(face.a);face.b=Map(face.b);face.c=Map(face.c);
                        child.shoot.geometry.surface[f]=face;
                    }
                }
            }
        }

        void Tube(Vector3 a,Vector3 b,Vector3 c,float r0,float r1,bool aerial=false,bool forceRing=false)
        {
            const int rings=7,sides=12;
            int first=vertices.Count;
            bool blendWood=!forceRing && r1>=.23f;
            Vector3 previous=a;
            float previousRadius=r0;
            Vector3 forward=(b-a).normalized;
            Vector3 side=Vector3.Cross(forward,Mathf.Abs(forward.y)>.9f?Vector3.right:Vector3.up).normalized;
            for(int j=0;j<=rings;j++)
            {
                float t=j/(float)rings;
                // Quadratic Bezier passing through b at the centre.
                Vector3 control=b*2-(a+c)*.5f;
                Vector3 center=(1-t)*(1-t)*a+2*(1-t)*t*control+t*t*c;
                float sectionRadius=Mathf.Lerp(r0,r1,t);
                if(blendWood){
                    if(j>0)limbs.Add((previous,center,previousRadius,sectionRadius));
                    previous=center;previousRadius=sectionRadius;
                    continue;
                }
                Vector3 tangent=Vector3.Lerp(control-a,c-control,t).normalized;
                side=Vector3.ProjectOnPlane(side,tangent).normalized;
                Vector3 across=Vector3.Cross(tangent,side);
                for(int k=0;k<sides;k++){
                    float angle=2*Mathf.PI*k/sides;
                    float r=Mathf.Lerp(r0,r1,t)*(1+.045f*Mathf.Cos(5*angle+t));
                    vertices.Add(center+r*(side*Mathf.Cos(angle)+across*Mathf.Sin(angle)));
                    AddBark(new BarkSample(vertices[vertices.Count-1]-center,tangent),aerial?-1:FigShoot.YoungWood(sectionRadius*settings.size));
                    if(j==rings)continue;
                    int v=first+j*sides+k,w=first+j*sides+(k+1)%sides;
                    triangles.Add(v);triangles.Add(w);triangles.Add(v+sides);
                    triangles.Add(w);triangles.Add(w+sides);triangles.Add(v+sides);
                }
            }
            if(blendWood)return;
            for(int i=1;i<sides-1;i++){
                triangles.Add(first);triangles.Add(first+i+1);triangles.Add(first+i);
                int last=first+rings*sides;
                triangles.Add(last);triangles.Add(last+i);triangles.Add(last+i+1);
            }
        }
    }
}



