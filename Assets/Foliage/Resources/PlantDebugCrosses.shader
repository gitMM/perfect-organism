Shader "Hidden/Foliage/Debug Crosses"
{
    SubShader
    {
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            StructuredBuffer<float3> Points;
            float Radius;
            float4 Tint;
            float4 vert(uint vertex:SV_VertexID,uint instance:SV_InstanceID):SV_POSITION
            {
                float3 axis=float3(vertex/2==0,vertex/2==1,vertex/2==2);
                float3 world=Points[instance]+axis*((vertex&1)?Radius:-Radius);
                return mul(UNITY_MATRIX_VP,float4(world,1));
            }
            fixed4 frag():SV_Target{return Tint;}
            ENDCG
        }
    }
}
