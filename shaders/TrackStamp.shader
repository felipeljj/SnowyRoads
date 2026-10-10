Shader "RoadSnow/TrackStamp"
{
    Properties { _MainTex ("Persistent wear tiles", 2D) = "black" {} }
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            Blend 0 One Zero
            Blend 1 One Zero
            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _RoadSnowTrackArea;
            struct Input { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct Varying { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float height : TEXCOORD1; float weight : TEXCOORD2; };
            Varying Vert(Input input)
            {
                Varying o;
                float2 uv = (input.vertex.xz - _RoadSnowTrackArea.xy) * _RoadSnowTrackArea.z;
                o.position = float4(uv * 2 - 1, 0, 1);
                #if UNITY_UV_STARTS_AT_TOP
                    o.position.y = -o.position.y;
                #endif
                o.uv = input.uv; o.height = input.vertex.y; o.weight = input.color.r;
                return o;
            }
            struct Output { float wear : SV_Target0; float height : SV_Target1; };
            Output Frag(Varying input)
            {
                float4 tile = tex2D(_MainTex, input.uv);
                clip(tile.r - 0.001);
                Output o; o.wear = tile.r * input.weight;
                o.height = (round(tile.g * 255) + round(tile.b * 255) * 256) * 0.1 - 1024;
                return o;
            }
            ENDHLSL
        }
    }
}
