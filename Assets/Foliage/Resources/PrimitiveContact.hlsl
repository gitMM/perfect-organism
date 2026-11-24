// Kinematic primitive against a triangle or a thick centreline segment.
struct Feature { int a,b,c; float skin; };
struct Contact { float3 witness; float depth; float3 normal; float candidate; float3 weights; float clearance; int layers,region; };
StructuredBuffer<Feature> Features;
RWStructuredBuffer<Contact> Contacts;
int Shape, FeatureCount;
float3 ObstacleCenter, Extents;
float4 ObstacleRotation;
float ContactK;
int UseOgc;
float ContactOffset,QueryRadius;
struct Region { int3 vertexOwner,edgeOwner,across; };
StructuredBuffer<Region> Regions;
StructuredBuffer<int> RingOffsets,RingNodes;

// Classify the closest witness and clip its normal against the deformed one-ring.
int ContactRegion(int id, Feature f,float3 w,float3 witness,float3 toward)
{
    if(f.c<0)return 2;
    int3 nodes=int3(f.a,f.b,f.c);
    int corner=w.y>w.x?1:0;
    if(w.z>w[corner])corner=2;
    Region region=Regions[id];
    if(w[corner]>=.99999)
    {
        if(region.vertexOwner[corner]!=id)return 0;
        int v=nodes[corner];
        for(int j=RingOffsets[v];j<RingOffsets[v+1];j++)
            if(dot(toward,X[RingNodes[j]]-X[v])>1e-6)return 0;
        return 1;
    }
    for(int side=0;side<3;side++)
    {
        if(w[(side+2)%3]>1e-5)continue;
        if(region.edgeOwner[side]!=id)return 0;
        int across=region.across[side];
        if(across>=0 && dot(toward,X[across]-witness)>1e-6)return 0;
        return 2;
    }
    return 3;
}

float3 Turn(float4 q,float3 p) {return p+2*cross(q.xyz,cross(q.xyz,p)+q.w*p);}
float SegmentT(float3 p,float3 a,float3 b)
{float3 e=b-a;return saturate(dot(p-a,e)/max(dot(e,e),1e-16));}

// Project onto the triangle plane if inside; otherwise the closest point lies on an edge.
float3 TriangleWeights(float3 p,float3 a,float3 b,float3 c)
{
    float3 ab=b-a,ac=c-a,n=cross(ab,ac);
    float den=dot(n,n);
    if(den>1e-18)
    {
        float u=dot(cross(p-a,ac),n)/den, v=dot(cross(ab,p-a),n)/den;
        if(u>=0 && v>=0 && u+v<=1) return float3(1-u-v,u,v);
    }
    float t=SegmentT(p,a,b),s=SegmentT(p,b,c),r=SegmentT(p,c,a);
    float da=length(p-lerp(a,b,t)), db=length(p-lerp(b,c,s)), dc=length(p-lerp(c,a,r));
    if(da<=db && da<=dc) return float3(1-t,t,0);
    return db<=dc?float3(0,1-s,s):float3(r,0,1-r);
}

// Closest axis/edge pair, solving the clamped two-line least squares problem.
void AxisEdge(float3 a,float3 b,float3 wa,float3 wb,float halfAxis,
              inout float best,inout float3 weights,inout float3 delta)
{
    float3 e=b-a; float ee=dot(e,e);
    float t=clamp((a.x*-e.x+a.z*-e.z)/max(dot(e.xz,e.xz),1e-16),0,1);
    float y=clamp(a.y+t*e.y,-halfAxis,halfAxis);
    t=saturate(dot(float3(0,y,0)-a,e)/max(ee,1e-16));
    float3 p=a+t*e;
    float3 d=p-float3(0,clamp(p.y,-halfAxis,halfAxis),0);
    float sq=dot(d,d);
    if(sq<best){best=sq;weights=lerp(wa,wb,t);delta=d;}
}
void RoundContact(float3 a,float3 b,float3 c,bool tri,float skin,out float3 w,out float3 n,out float depth)
{
    float h=Shape==2?Extents.y-Extents.x:0;
    float3 delta=0; float best=1e20;
    w=float3(1,0,0);
    float3 faceNormal=cross(b-a,c-a);
    if(tri)
    {
        // Include an interior axis/face crossing as well as both cap endpoints.
        float y=abs(faceNormal.y)>1e-12?clamp(dot(faceNormal,a)/faceNormal.y,-h,h):0;
        [unroll] for(int i=0;i<3;i++)
        {
            float3 axis=float3(0,i==0?-h:i==1?h:y,0);
            float3 wc=TriangleWeights(axis,a,b,c);
            float3 d=wc.x*a+wc.y*b+wc.z*c-axis;
            float sq=dot(d,d);
            if(sq<best){best=sq;w=wc;delta=d;}
        }
    }
    AxisEdge(a,b,float3(1,0,0),float3(0,1,0),h,best,w,delta);
    if(tri && Shape==2)
    {
        AxisEdge(b,c,float3(0,1,0),float3(0,0,1),h,best,w,delta);
        AxisEdge(c,a,float3(0,0,1),float3(1,0,0),h,best,w,delta);
    }
    float dist=sqrt(best);
    depth=Extents.x+skin-dist;
    if(dist>1e-7){n=delta/dist;return;}
    float3 fallback=tri?faceNormal:cross(b-a,float3(0,1,0));
    if(dot(fallback,fallback)<1e-16) fallback=cross(b-a,float3(1,0,0));
    if(dot(fallback,fallback)<1e-16) fallback=float3(0,0,1);
    n=normalize(fallback);
    if(dot(n,a+b+c)<0)n=-n;
}

void TestAxis(float3 axis,float3 a,float3 b,float3 c,inout float depth,inout float3 normal)
{
    float sq=dot(axis,axis);
    if(sq<1e-16)return;
    axis*=rsqrt(sq);
    float ra=dot(axis,a),rb=dot(axis,b),rc=dot(axis,c),radius=dot(abs(axis),Extents);
    float plus=radius-min(ra,min(rb,rc)),minus=radius+max(ra,max(rb,rc));
    float separation=min(plus,minus);
    if(separation<depth){depth=separation;normal=plus<=minus?axis:-axis;}
}
void ClipWitness(float3 a,float3 b,float3 wa,float3 wb,inout float3 acc,inout float samples)
{
    float enter=0,leave=1;
    [unroll] for(int i=0;i<3;i++)
    {
        float d=b[i]-a[i],r=Extents[i]+1e-5;
        if(abs(d)<1e-9){if(abs(a[i])>r)return;}
        else
        {
            float lo=(-r-a[i])/d,hi=(r-a[i])/d;
            enter=max(enter,min(lo,hi));leave=min(leave,max(lo,hi));
        }
    }
    if(enter<=leave){acc+=lerp(wa,wb,.5*(enter+leave));samples++;}
}
void BoxContact(float3 a,float3 b,float3 c,bool tri,float skin,out float3 w,out float3 n,out float depth)
{
    depth=1e20;n=0;w=0;
    float3 edge[3]={b-a,c-b,a-c};
    [unroll] for(int j=0;j<3;j++)
    {
        float3 axis=float3(j==0,j==1,j==2);
        TestAxis(axis,a,b,c,depth,n);
        [unroll] for(int e=0;e<3;e++)TestAxis(cross(edge[e],axis),a,b,c,depth,n);
    }
    if(tri)TestAxis(cross(b-a,c-a),a,b,c,depth,n);
    // Translate to first touch along the minimum separating axis to locate the contact feature.
    float3 offset=n*depth;
    depth+=skin;
    if(depth<=0)return;
    float samples=0;
    ClipWitness(a+offset,b+offset,float3(1,0,0),float3(0,1,0),w,samples);
    if(tri)
    {
        ClipWitness(b+offset,c+offset,float3(0,1,0),float3(0,0,1),w,samples);
        ClipWitness(c+offset,a+offset,float3(0,0,1),float3(1,0,0),w,samples);
    }
    if(samples>0)w/=samples;
    else
    {
        float3 support=sign(n)*Extents-offset;
        if(tri)w=TriangleWeights(support,a,b,c);
        else {float t=SegmentT(support,a,b);w=float3(1-t,t,0);}
    }
}
int WallCount;
float WallOffset;
[numthreads(64,1,1)]
void DetectContacts(uint slot:SV_DispatchThreadID)
{
    if(slot>=(uint)(FeatureCount*WallCount))return;
    uint id=slot<(uint)FeatureCount?slot:slot-FeatureCount;
    Feature f=Features[id]; bool tri=f.c>=0;
    float4 inv=float4(-ObstacleRotation.xyz,ObstacleRotation.w);
    float3 center=ObstacleCenter+Turn(ObstacleRotation,float3(slot==id?WallOffset:-WallOffset,0,0));
    float3 a=Turn(inv,X[f.a]-center),b=Turn(inv,X[f.b]-center);
    float3 c=tri?Turn(inv,X[max(0,f.c)]-center):b;
    Contact hit=(Contact)0;
    float margin=f.skin+(UseOgc!=0?QueryRadius:0);
    hit.clearance=QueryRadius;
    if(all(min(a,min(b,c))<=Extents+margin) && all(max(a,max(b,c))>=-Extents-margin))
    {
        hit.candidate=1;
        float3 n,w;float depth;
        float offset=UseOgc!=0?ContactOffset:0;
        if(Shape==3)BoxContact(a,b,c,tri,f.skin+offset,w,n,depth);
        else RoundContact(a,b,c,tri,f.skin,w,n,depth);
        if(Shape!=3)depth+=offset;
        hit.clearance=min(QueryRadius,offset-depth);
        if(depth>0)
        {
            hit.normal=Turn(ObstacleRotation,n);hit.depth=depth;hit.weights=w;
            hit.witness=center+Turn(ObstacleRotation,w.x*a+w.y*b+w.z*c);
            hit.region=UseOgc!=0?ContactRegion(id,f,w,hit.witness,-hit.normal):3;
            hit.layers=hit.region>0?1:0;
            if(hit.layers==0)hit.depth=0;
        }
    }
    Contacts[slot]=hit;
}
