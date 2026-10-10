Shader "RoadSnow/LaneStamp"
{
    SubShader { Pass {
        Cull Off ZWrite Off ZTest Always
        Blend 0 One One
        BlendOp 0 Max
        Blend 1 One Zero
        HLSLPROGRAM
        #pragma target 4.5
        #pragma only_renderers d3d11
        #pragma vertex Vert
        #pragma fragment Frag
        #include "UnityCG.cginc"
        float4 _RoadSnowLaneArea;
        struct Input { float4 vertex:POSITION; float2 uv:TEXCOORD0; float4 color:COLOR; };
        struct Varying { float4 position:SV_POSITION; float2 uv:TEXCOORD0; float height:TEXCOORD1; float pressure:TEXCOORD2; };
        Varying Vert(Input i) {
            Varying o; float2 uv=(i.vertex.xz-_RoadSnowLaneArea.xy)*_RoadSnowLaneArea.z;
            o.position=float4(uv*2-1,0,1);
            #if UNITY_UV_STARTS_AT_TOP
                o.position.y=-o.position.y;
            #endif
            o.uv=i.uv; o.height=i.vertex.y; o.pressure=i.color.r; return o;
        }
        struct Output { float wear:SV_Target0; float height:SV_Target1; };
        Output Frag(Varying i) {
            float side=abs(i.uv.x);
            float edge=1-smoothstep(.58,.94,side);

            float rut=exp(-pow((side*i.uv.y*.5-.72)/.30,2));
            float spread=lerp(.30,1,smoothstep(.15,.85,i.pressure));
            Output o; o.wear=.95*i.pressure*edge*lerp(spread,1,rut); o.height=i.height;
            clip(edge-.001); return o;
        }
        ENDHLSL
    } }
}
