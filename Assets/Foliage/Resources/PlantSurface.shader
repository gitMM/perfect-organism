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
            struct Input { float4 vertex:POSITION; float3 normal:NORMAL; float4 color:COLOR; };
            struct Output { float4 pos:SV_POSITION; float3 normal:TEXCOORD0; float4 color:COLOR; };
            Output vert(Input v)
            {
                Output o;
                o.pos=UnityObjectToClipPos(v.vertex);
                o.normal=UnityObjectToWorldNormal(v.normal);
                o.color=v.color;
                return o;
            }
            fixed4 frag(Output i, fixed facing:VFACE):SV_Target
            {
                float3 n=normalize(i.normal)*sign(facing);
                float light=.70+.37*dot(n,normalize(float3(-.35,.85,-.42)));
                return float4(i.color.rgb*light,1);
            }
            ENDCG
        }
    }
}
