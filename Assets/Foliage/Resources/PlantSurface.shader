Shader "Foliage/Plant Surface"
{
    SubShader
    {
        Pass
        {
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct Input { float2 uv:TEXCOORD0; float4 vertex:POSITION; float3 normal:NORMAL; float4 color:COLOR; };
            struct Output { float2 uv:TEXCOORD1; float4 pos:SV_POSITION; float3 normal:TEXCOORD0; float4 color:COLOR; };
            Output vert(Input v)
            {
                Output o;
                o.pos=UnityObjectToClipPos(v.vertex);
                o.normal=UnityObjectToWorldNormal(v.normal);
                o.color=v.color;o.uv=v.uv;
                return o;
            }
            fixed4 frag(Output i, fixed facing:VFACE):SV_Target
            {
                float3 n=normalize(i.normal)*sign(facing);
                float light=.70+.37*dot(n,normalize(float3(-.35,.85,-.42)));
                float3 color=i.color.rgb;
                if(i.uv.y>=1.99)
                {
                    float u=abs(i.uv.x),t=saturate(i.uv.y-2);
                    float rib=1-smoothstep(.012,.035+fwidth(u),u);
                    float vein=abs(frac(6*t-1.65*u-.48*u*u+.5)-.5);
                    float contrast=saturate(1-8*fwidth(t));
                    color+=contrast*(rib*.08+(1-smoothstep(.025,.10+fwidth(vein),vein))*.027)
                        *float3(.82,1,.42);
                    light+=.15*saturate(-dot(n,normalize(float3(-.35,.85,-.42))));
                }
                return float4(color*light,1);
            }
            ENDCG
        }
    }
}
