Shader "Foliage/Fig Leaf"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off
        CGPROGRAM
        #pragma surface surf Standard vertex:vert addshadow
        #pragma target 3.0
        struct Input { float2 leafUV; float flush; float facing : VFACE; };
        void vert(inout appdata_full v,out Input o){UNITY_INITIALIZE_OUTPUT(Input,o);o.leafUV=v.texcoord.xy;o.flush=v.color.a;}
        void surf(Input i,inout SurfaceOutputStandard o)
        {
            float u=abs(i.leafUV.x), t=i.leafUV.y-2;
            bool blade=t>=-.01;
            bool upper=i.facing>=0;
            float3 pigment=upper?float3(.12,.28,.12):float3(.21,.33,.17);
            pigment*=lerp(float3(.73,.82,.74),float3(1.23,1.10,.81),i.flush);
            // Average the pale midrib into the blade when a whole leaf covers only a few pixels.
            float footprint=max(.001,fwidth(i.leafUV.x));
            float rib=(1-smoothstep(.025,.07+.5*footprint,u))*saturate(.10/footprint);
            float vein=abs(frac(6*t-1.65*u-.48*u*u+.5)-.5);
            float minor=(1-smoothstep(.025,.11+fwidth(vein),vein))*saturate(1-8*fwidth(t));
            pigment=lerp(pigment,float3(.52,.57,.35),.55*rib+.10*minor);
            pigment=blade?pigment:float3(.38,.45,.23);
            #ifndef UNITY_COLORSPACE_GAMMA
            pigment=GammaToLinearSpace(pigment);
            #endif
            o.Albedo=pigment;
            o.Smoothness=blade && upper?.25:.12;
            o.Occlusion=blade?lerp(.55,.90,i.flush):1;
            o.Normal=float3(0,0,upper?1:-1);
        }
        ENDCG
    }
    FallBack "Diffuse"
}
