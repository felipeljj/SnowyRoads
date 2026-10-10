#ifndef ROADSNOW_COMMON
#define ROADSNOW_COMMON
TEXTURE2D(_RoadSnowNative);
TEXTURE2D(_RoadSnowTrackAtlas);
TEXTURE2D(_RoadSnowTrackPages);
Texture2D<uint> _RoadSnowLanePacked;
TEXTURE2D(_RoadSnowAlbedo);
TEXTURE2D(_RoadSnowNormal);
float4 _RoadSnowTrackArea, _RoadSnowLaneArea, _RoadSnowMapping;
float _RoadSnowPreview, _RoadSnowDepthView, _RoadSnowRelief, _RoadSnowUseNativeColor, _RoadSnowHasNormal;
float4 _RoadSnowBankCamera;
float SnowHash(float2 p) { p=frac(p*float2(.1031,.11369)); p+=dot(p,p.yx+19.19); return frac((p.x+p.y)*p.x); }
float SnowNoise(float2 p) {
    float2 i=floor(p),f=frac(p); f=f*f*(3-2*f);
    return lerp(lerp(SnowHash(i),SnowHash(i+float2(1,0)),f.x),lerp(SnowHash(i+float2(0,1)),SnowHash(i+1),f.x),f.y);
}
float SnowBreakup(float2 p) {

    return .5*SnowNoise(p*.22)+.27*SnowNoise(p*.79+13.7)+.15*SnowNoise(p*2.63-8.3)+.08*SnowNoise(p*8.9);
}
float SnowFineWear(float3 p) {
    if (_RoadSnowTrackArea.z <= 0) return 0;
    float2 cell=(p.xz-_RoadSnowTrackArea.xy)/8;
    if (any(cell<0)||any(cell>=16)) return 0;
    float2 page=floor(cell), local=clamp(frac(cell)*128,.5,127.5);
    float wear=0;

    [loop] for(int layer=0;layer<4;layer++) {
        float4 entry=SAMPLE_TEXTURE2D_LOD(_RoadSnowTrackPages,s_point_clamp_sampler,
            (page+float2(.5,.5+layer*16))/float2(16,64),0);
        if(entry.a<.5) break;
        float2 uv=(round(entry.rg*255)*128+local)/2048;
        float4 packed=SAMPLE_TEXTURE2D_LOD(_RoadSnowTrackAtlas,s_point_clamp_sampler,uv,0);
        float h=(round(packed.g*255)+round(packed.b*255)*256)*.1-1024;
        float pressure=SAMPLE_TEXTURE2D_LOD(_RoadSnowTrackAtlas,s_linear_clamp_sampler,uv,0).r;
        wear=max(wear,pressure*entry.b*(1-smoothstep(.12,.35,abs(p.y-h))));
    }
    return min(.95,wear);
}
float SnowLaneTexel(int2 xy,float y) {
    uint packed=_RoadSnowLanePacked.Load(int3(clamp(xy,0,2047),0));
    float h=(packed>>8)*.05-1024;
    return (packed&255)/255.0*(1-smoothstep(.25,.65,abs(y-h)));
}
float SnowLaneWear(float3 p) {
    if(_RoadSnowLaneArea.z<=0) return 0;
    float2 uv=(p.xz-_RoadSnowLaneArea.xy)*_RoadSnowLaneArea.z;
    if(any(uv<=0)||any(uv>=1)) return 0;
    float2 pixel=uv*2048-.5, f=frac(pixel); int2 xy=(int2)floor(pixel);
    return lerp(lerp(SnowLaneTexel(xy,p.y),SnowLaneTexel(xy+int2(1,0),p.y),f.x),
        lerp(SnowLaneTexel(xy+int2(0,1),p.y),SnowLaneTexel(xy+1,p.y),f.x),f.y);
}
float SnowWear(float3 p) { return max(SnowLaneWear(p),SnowFineWear(p)); }
float SnowTrackTint(float wear) {

    return 1-.55*sqrt(saturate(wear));
}
float SnowCoverage(float3 p,float4 metadata,out float depth,out float retained,out float fineWear) {
    float2 uv=(p.xz+_RoadSnowMapping.xy)*_RoadSnowMapping.zw;
    float encoded=SAMPLE_TEXTURE2D_LOD(_RoadSnowNative,s_linear_clamp_sampler,uv,0).r;
    float nativeMm=_RoadSnowPreview>.5 ? 15 : encoded*encoded*8000;
    float noise=SnowBreakup(p.xz);
    retained=metadata.r*metadata.g*80;

    retained*=smoothstep(.38,.68,noise);
    depth=max(min(80,nativeMm),retained);
    float fraction=saturate(depth/10);
    float threshold=.10+.75*noise;
    float coverage=smoothstep(threshold-.08,threshold+.12,fraction);
    coverage=lerp(.05*fraction,1,coverage);
    fineWear=SnowFineWear(p);
    float wear=max(SnowLaneWear(p),fineWear);
    depth*=1-wear;
    return coverage*(1-wear)*smoothstep(.10,.3,max(nativeMm,retained));
}
float SnowCoverage(float3 p,float4 metadata,out float depth,out float retained) {
    float fineWear;
    return SnowCoverage(p,metadata,depth,retained,fineWear);
}
float SnowBankScale(float3 basePosition,float4 metadata) {
    float depth,retained,fine;
    SnowCoverage(basePosition,metadata,depth,retained,fine);
    float distanceFade=1-smoothstep(_RoadSnowBankCamera.w*.60,_RoadSnowBankCamera.w,
        distance(basePosition,_RoadSnowBankCamera.xyz));
    return smoothstep(.2,30,depth)*distanceFade;
}
float SnowFacilityOpacity(float3 p,float fineWear) {
    float2 uv=(p.xz+_RoadSnowMapping.xy)*_RoadSnowMapping.zw;
    float encoded=SAMPLE_TEXTURE2D_LOD(_RoadSnowNative,s_linear_clamp_sampler,uv,0).r;
    float depth=_RoadSnowPreview>.5 ? 15 : encoded*encoded*8000;
    return smoothstep(.1,.3,depth)*saturate(sqrt(fineWear)*1.25);
}
float3 SnowDepthColor(float mm) {
    float3 blue=float3(.015,.12,.8), green=float3(.03,.75,.25), yellow=float3(1,.8,.02), red=float3(.85,.025,.015);
    if(mm<5) return lerp(float3(.015,.02,.07),blue,saturate(mm/5));
    if(mm<15) return lerp(blue,green,(mm-5)/10);
    if(mm<30) return lerp(green,yellow,(mm-15)/15);
    return lerp(yellow,red,saturate((mm-30)/50));
}
#endif
