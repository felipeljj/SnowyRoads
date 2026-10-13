#ifndef ROADSNOW_COMMON
#define ROADSNOW_COMMON
TEXTURE2D(_RoadSnowNative);
TEXTURE2D(_RoadSnowTrackAtlas);
TEXTURE2D(_RoadSnowTrackPages);
TEXTURE2D(_RoadSnowDistantTrackAtlas);
TEXTURE2D(_RoadSnowDistantTrackPages);
float4 _RoadSnowDistantTrackArea;
Texture2DArray<uint> _RoadSnowLanePacked;
Texture2DArray<uint> _RoadSnowDetailLanePacked;
float4 _RoadSnowDetailLaneArea;
Texture2DArray<uint> _RoadSnowDistantLanePacked;
float4 _RoadSnowDistantLaneArea;
TEXTURE2D(_RoadSnowAlbedo);
TEXTURE2D(_RoadSnowNormal);
float4 _RoadSnowTrackArea, _RoadSnowLaneArea, _RoadSnowMapping;
float _RoadSnowPreview, _RoadSnowDepthView, _RoadSnowRelief, _RoadSnowUseNativeColor, _RoadSnowHasNormal;
float4 _RoadSnowBankCamera;
float _RoadSnowRealistic;
float4 _RoadSnowAmbientUp, _RoadSnowAmbientDown, _RoadSnowAmbientLeft,
    _RoadSnowAmbientRight, _RoadSnowAmbientForward, _RoadSnowAmbientBack;
float3 SnowAmbient(float3 normal) {
    float3 w=normal*normal;
    return max(0,(normal.y>=0?_RoadSnowAmbientUp.rgb:_RoadSnowAmbientDown.rgb)*w.y
        +(normal.x>=0?_RoadSnowAmbientRight.rgb:_RoadSnowAmbientLeft.rgb)*w.x
        +(normal.z>=0?_RoadSnowAmbientForward.rgb:_RoadSnowAmbientBack.rgb)*w.z);
}
float SnowHash(float2 p) { p=frac(p*float2(.1031,.11369)); p+=dot(p,p.yx+19.19); return frac((p.x+p.y)*p.x); }
float SnowNoise(float2 p) {
    float2 i=floor(p),f=frac(p); f=f*f*(3-2*f);
    return lerp(lerp(SnowHash(i),SnowHash(i+float2(1,0)),f.x),lerp(SnowHash(i+float2(0,1)),SnowHash(i+1),f.x),f.y);
}
float SnowBreakup(float2 p) {

    return .5*SnowNoise(p*.22)+.27*SnowNoise(p*.79+13.7)+.15*SnowNoise(p*2.63-8.3)+.08*SnowNoise(p*8.9);
}
float3 SnowTexture(float2 world) {
    // Same world scale as the shipped network snow shader (Cities 1.6.2).
    // Rotating/blending this map made our pavement disagree with native lots.
    return SAMPLE_TEXTURE2D(_RoadSnowAlbedo,s_linear_repeat_sampler,world*.03).rgb;
}
float TrackTexel(Texture2D atlas,int2 pixel,float y) {
    float4 packed=atlas.Load(int3(pixel,0));
    float height=(round(packed.g*255)+round(packed.b*255)*256)*.1-1024;
    return packed.r*(1-smoothstep(.12,.35,abs(y-height)));
}
float SampleSnowTracks(Texture2D atlas, Texture2D pages, float4 area, float3 p,
    float pageSide, float resolution, float atlasSize, out float resident) {
    resident=0;
    if(area.z<=0) return 0;
    float2 cell=(p.xz-area.xy)/8;
    if(any(cell<0)||any(cell>=pageSide)) return 0;
    float2 page=floor(cell),local=clamp(frac(cell)*resolution,.5,resolution-.5);
    float wear=0;
    [loop] for(int layer=0;layer<4;layer++) {
        float4 entry=pages.SampleLevel(s_point_clamp_sampler,
            (page+float2(.5,.5+layer*pageSide))/float2(pageSide,pageSide*4),0);
        if(entry.a<.5) break;
        float2 uv=(round(entry.rg*255)*resolution+local)/atlasSize;
        float4 packed=atlas.SampleLevel(s_point_clamp_sampler,uv,0);
        float height=(round(packed.g*255)+round(packed.b*255)*256)*.1-1024;
        float matchHeight=1-smoothstep(.12,.35,abs(p.y-height));
        float2 pixel=local-.5,blend=frac(pixel);
        int2 base=(int2)round(entry.rg*255)*resolution;
        int2 a=(int2)floor(pixel),b=min(a+1,(int2)(resolution-1));
        float value=lerp(lerp(TrackTexel(atlas,base+a,p.y),TrackTexel(atlas,base+int2(b.x,a.y),p.y),blend.x),
            lerp(TrackTexel(atlas,base+int2(a.x,b.y),p.y),TrackTexel(atlas,base+b,p.y),blend.x),blend.y);
        wear=max(wear,value*entry.b);
        // Empty pixels still belong to this height layer, but contain no height sample.
        if(matchHeight>.5 || packed.r==0) resident=max(resident,entry.b);
    }
    return wear;
}
float SnowFineWear(float3 p) {
    float nearResident, farResident;
    float nearWear=SampleSnowTracks(_RoadSnowTrackAtlas,_RoadSnowTrackPages,_RoadSnowTrackArea,p,128,128,2048,nearResident);
    if(nearResident>.999) return min(.95,nearWear);
    float farWear=SampleSnowTracks(_RoadSnowDistantTrackAtlas,_RoadSnowDistantTrackPages,_RoadSnowDistantTrackArea,p,512,16,1024,farResident);
    return min(.95,nearWear+farWear*(1-nearResident));
}
float SnowLaneTexel(Texture2DArray<uint> mask,int2 xy,float y) {
    float wear=0;
    [unroll] for(int layer=0;layer<4;layer++) {
        uint packed=mask.Load(int4(clamp(xy,0,1023),layer,0));
        float h=(packed>>8)*.05-1024;
        wear=max(wear,(packed&255)/255.0*(1-smoothstep(.25,.65,abs(y-h))));
    }
    return wear;
}
float SnowLaneSample(Texture2DArray<uint> mask,float4 area,float3 p) {
    if(area.z<=0) return 0;
    float2 uv=(p.xz-area.xy)*area.z;
    if(any(uv<=0)||any(uv>=1)) return 0;
    float2 pixel=uv*1024-.5, f=frac(pixel); int2 xy=(int2)floor(pixel);
    return lerp(lerp(SnowLaneTexel(mask,xy,p.y),SnowLaneTexel(mask,xy+int2(1,0),p.y),f.x),
        lerp(SnowLaneTexel(mask,xy+int2(0,1),p.y),SnowLaneTexel(mask,xy+1,p.y),f.x),f.y);
}
float SnowLaneBlend(float4 area,float3 p) {
    float2 uv=(p.xz-area.xy)*area.z;
    return area.z>0?smoothstep(0,.12,min(min(uv.x,uv.y),min(1-uv.x,1-uv.y))):0;
}
float SnowLaneBaseWear(float3 p) {
    float nearWear=SnowLaneSample(_RoadSnowLanePacked,_RoadSnowLaneArea,p);
    float fine=SnowLaneBlend(_RoadSnowLaneArea,p);
    if(fine>.999) return nearWear;
    float farWear=SnowLaneSample(_RoadSnowDistantLanePacked,_RoadSnowDistantLaneArea,p);
    return lerp(farWear,nearWear,fine);
}
float SnowLaneWear(float3 p) {
    float detail=SnowLaneBlend(_RoadSnowDetailLaneArea,p);
    if(detail<=0) return SnowLaneBaseWear(p);
    float wear=SnowLaneSample(_RoadSnowDetailLanePacked,_RoadSnowDetailLaneArea,p);
    if(detail>.999) return wear;
    return lerp(SnowLaneBaseWear(p),wear,detail);
}
float SnowWear(float3 p) { return max(SnowLaneWear(p),SnowFineWear(p)); }
float SnowImpressionRelief(float3 p,float centerWear) {
    float fade=_RoadSnowRealistic*(1-smoothstep(90,220,distance(p.xz,_RoadSnowBankCamera.xz)));
    [branch] if(fade<=0) return 0;
    // Displaced snow sits beside the recorded wheel/footprint, never beside an
    // invented lane stripe. Four local taps are confined to the close view.
    const float radius=.16;
    float neighbor=max(max(SnowFineWear(p+float3(radius,0,0)),SnowFineWear(p-float3(radius,0,0))),
        max(SnowFineWear(p+float3(0,0,radius)),SnowFineWear(p-float3(0,0,radius))));
    return (min(.025,max(0,neighbor-centerWear)*.07)-centerWear*.014)*fade;
}
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
#if defined(ROADSNOW_PLOW)
    return 1;
#endif
    float depth,retained,fine;
    SnowCoverage(basePosition,metadata,depth,retained,fine);
    float distanceFade=1-smoothstep(_RoadSnowBankCamera.w*.60,_RoadSnowBankCamera.w,
        distance(basePosition.xz,_RoadSnowBankCamera.xz));
    float scale=smoothstep(.2,30,depth);
    if(metadata.w<0) {
        scale*=1-SnowLaneWear(basePosition);
        scale=max(0,scale+SnowImpressionRelief(basePosition,fine)/.10);
        scale*=_RoadSnowRealistic;
    }
    return scale*distanceFade;
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
