Shader "Foliage/Fig Solver Debug"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            StructuredBuffer<int> _VertexFlags, _ClusterState;
            int _BoughAwake, _Selective;
            struct Output { float4 pos : SV_POSITION; float3 color : COLOR; };
            Output vert(appdata_base v, uint id : SV_VertexID)
            {
                int flags = _VertexFlags[id];
                int cluster = flags >> 1;
                bool awake = _BoughAwake && (!_Selective || cluster == 0 || _ClusterState[cluster] > 0);
                float3 color = (flags & 1)
                    ? (awake ? float3(1,.55,.08) : float3(.59,.35,.82))
                    : (awake ? float3(.10,1,.83) : float3(.22,.43,.75));
                Output o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = color * (.75 + .25 * abs(UnityObjectToWorldNormal(v.normal).y));
                #ifndef UNITY_COLORSPACE_GAMMA
                o.color = GammaToLinearSpace(o.color);
                #endif
                return o;
            }
            fixed4 frag(Output i) : SV_Target { return fixed4(i.color,1); }
            ENDCG
        }
    }
}
