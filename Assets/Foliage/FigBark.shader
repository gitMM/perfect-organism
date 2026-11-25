Shader "Foliage/Fig Bark"
{
    Properties
    {
        _Dry ("Dry bark", Color) = (.46,.395,.32,1)
        _Damp ("Damp bark", Color) = (.27,.235,.20,1)
        [Toggle] _ShowGrain ("Show growth stripes", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        CGPROGRAM
        #pragma surface surf Standard vertex:vert addshadow
        #pragma target 3.0

        fixed4 _Dry, _Damp;
        float _ShowGrain;
        struct Input
        {
            float3 grain;
            float3 rest;
            float recess;
            float age;
            float3 worldPos;
            float3 worldNormal;
            INTERNAL_DATA
        };

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input,o);
            o.grain=v.texcoord.xyz;
            o.rest=v.texcoord2.w>=2?v.texcoord2.xyz:v.vertex.xyz;
            o.age=v.texcoord2.w>=4?-1:v.texcoord2.w>=2?v.texcoord2.w-2:v.texcoord2.w;
            o.recess=v.texcoord1.w;
            float3 n=normalize(v.normal);
            float3 along=v.texcoord1.xyz-n*dot(n,v.texcoord1.xyz);
            if(dot(along,along)<.001)
                along=cross(n,abs(n.y)<.9?float3(0,1,0):float3(1,0,0));
            v.tangent=float4(normalize(along),1);
        }

        float hash3(float3 p)
        {
            p=frac(p*.1031);
            p+=dot(p,p.yzx+33.33);
            return frac((p.x+p.y)*p.z);
        }
        float noise3(float3 p)
        {
            float3 i=floor(p), f=frac(p);
            f=f*f*(3-2*f);
            float4 base=float4(hash3(i),hash3(i+float3(0,1,0)),hash3(i+float3(0,0,1)),hash3(i+float3(0,1,1)));
            i.x+=1;
            float4 x=lerp(base,float4(hash3(i),hash3(i+float3(0,1,0)),hash3(i+float3(0,0,1)),hash3(i+float3(0,1,1))),f.x);
            return lerp(lerp(x.x,x.y,f.y),lerp(x.z,x.w,f.y),f.z);
        }

        void surf(Input IN,inout SurfaceOutputStandard o)
        {
            // Meter-scale mottling stays on the wood. Fine furrows use the swept
            // cross section and slowly changing rest position to follow its length.
            float3 p=IN.rest;
            float patch=noise3(p*1.8)+.45*noise3(p*5.8);
            float3 grain=IN.grain+.075*p+.045*(patch-.7);
            float wrinkle=noise3(grain*9.6);
            float fissure=1-smoothstep(.035,.21,abs(wrinkle-.5));
            float fine=noise3(grain*32+2.4);
            float pores=noise3(p*72);
            // Sparse lenticels sit on the surface between the deep longitudinal furrows.
            float lenticels=smoothstep(.63,.81,noise3(46*p+34*grain))
                *smoothstep(.40,.67,noise3(p*6.1));
            float highDetail=1-smoothstep(.009,.042,max(length(ddx(p)),length(ddy(p))));
            float damp=(1-smoothstep(.12,1.45,p.y+.45*(patch-.6)))*(.24+.76*saturate(IN.recess));
            float3 bark=lerp(_Dry.rgb,_Damp.rgb,damp);
            // Relative pigments use the same lighting and grain on both sides of a physics joint.
            bark*=lerp(float3(1,1,1),float3(.78,.67,.52),saturate(IN.age));
            bark*=lerp(float3(1,1,1),float3(.82,.49,.32),saturate(-IN.age));
            float relief=.16*(fine-.5)+.19*(pores-.5)-.28*lenticels;
            o.Albedo=bark*(.87+.40*(patch-.60)-.13*fissure+relief*highDetail);
            o.Smoothness=.10+.11*damp;
            if(_ShowGrain>.5){
                float stripe=sin(dot(grain,float3(13,17,23)));
                o.Albedo=lerp(float3(.13,.08,.045),float3(.65,.49,.29),smoothstep(-.17,.17,stripe));
            }
            float height=.006*(wrinkle-.5)-.0025*fissure+.0018*highDetail*(fine+.25*pores);
            float3 t=WorldNormalVector(IN,float3(1,0,0));
            float3 b=WorldNormalVector(IN,float3(0,1,0));
            float3 dx=ddx(IN.worldPos),dy=ddy(IN.worldPos);
            float2 x=float2(dot(dx,t),dot(dx,b)),y=float2(dot(dy,t),dot(dy,b));
            float det=x.x*y.y-x.y*y.x;
            float2 slope=(ddx(height)*float2(y.y,-y.x)+ddy(height)*float2(-x.y,x.x))
                *sign(det)/max(abs(det),1e-8);
            o.Normal=normalize(float3(-clamp(slope,-.45,.45),1));
        }
        ENDCG
    }
    FallBack "Diffuse"
}
