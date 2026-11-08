Shader "UnityLidarViewer/Points"
{
    Properties
    {
        _PointRadiusPixels("Radius (pixels)",Float)=1.5
        _Emission("Emission",Float)=4
        _Reveal("Reveal",Float)=1
        _Palette("Scalar palette",2D)="white"{}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Cull Off ZWrite On ZTest LEqual
            Tags { "LightMode"="SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            struct Point { float3 position; uint rgba; float intensity; float scalar; uint classification; uint returnNumber; };
            StructuredBuffer<Point> _Points;
            struct CompactPoint { float3 position; uint rgba; };
            StructuredBuffer<CompactPoint> _TilePoints;
            StructuredBuffer<float> _DisplayValues;
            sampler2D _Palette;
            float _PointRadiusPixels,_Emission,_FieldGamma,_FieldMin,_FieldMax,_PaletteRow,_Reveal;
            int _ShowGround,_FieldSource,_PointOffset,_LegacyIntensityRange,_Streamed;
            struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; float3 color:TEXCOORD1; nointerpolation float visible:TEXCOORD2; };
            v2f vert(uint vertex:SV_VertexID,uint instance:SV_InstanceID)
            {
                Point p = (Point)0;
                if (_Streamed != 0)
                {
                    CompactPoint tilePoint = _TilePoints[instance];
                    p.position = tilePoint.position;
                    p.rgba = tilePoint.rgba;
                }
                else p = _Points[instance];
                if (_Reveal < 1)
                {
                    uint sampleId = instance + (uint)_PointOffset;
                    uint seed = (sampleId ^ (sampleId >> 16)) * 0x45d9f3bu;
                    seed = (seed ^ (seed >> 16)) * 0x45d9f3bu;
                    if ((seed & 65535u) / 65535.0 >= _Reveal) return (v2f)0;
                }
                float2 corners[6]={float2(-1,-1),float2(1,-1),float2(1,1),float2(-1,-1),float2(1,1),float2(-1,1)};
                float2 uv=corners[vertex];
                float4 vp=mul(UNITY_MATRIX_V,float4(p.position,1));
                float4 clipPosition=mul(UNITY_MATRIX_P,vp);
                clipPosition.xy += uv * (2.0 * _PointRadiusPixels * lerp(.38, 1, _Reveal) / _ScaledScreenParams.xy) * clipPosition.w;
                bool isGround = p.classification == 2 || (p.rgba >> 24) == 0;
                v2f o;o.pos=clipPosition;o.uv=uv;o.visible=(_ShowGround!=0||!isGround)?1:0;
                float value = .5;
                if (_FieldSource == 1)
                    value = (p.scalar - _FieldMin) / max(.00001, _FieldMax - _FieldMin);
                else if (_FieldSource == 2)
                    value = frac(p.classification * .61803398875);
                else if (_FieldSource == 3)
                    value = _LegacyIntensityRange ? (p.intensity - _FieldMin) / max(.00001, _FieldMax - _FieldMin) : p.intensity / 65535.0;
                else if (_FieldSource == 4)
                    value = saturate((p.returnNumber - 1.0) / 14.0);
                else if (_FieldSource == 5)
                    value = _DisplayValues[_PointOffset + instance];
                float t = isfinite(value) ? pow(saturate(value), _FieldGamma) : 0;
                float3 rgb=float3(p.rgba&255,(p.rgba>>8)&255,(p.rgba>>16)&255)/255.0;
                rgb=SRGBToLinear(rgb);
                float3 ramp=tex2Dlod(_Palette,float4((t*255+.5)/256,_PaletteRow,0,0)).rgb;
                if (_FieldSource == 5 && value < 0) ramp = float3(.1,.1,.1);
                float3 color=_FieldSource==0?rgb:ramp;
                // All returns emit, including the dark end of Magma. Intensity still
                // controls hue and relative energy; source RGB never gates scalar glow.
                float peak=max(color.r,max(color.g,color.b));
                float3 hue=color/max(.0001,peak);
                o.color=hue*(.4+.6*sqrt(peak))*(1+_Emission*_Reveal*(.55+.45*t));return o;
            }
            float4 frag(v2f i):SV_Target
            {
                clip(i.visible-.5);float r2=dot(i.uv,i.uv);clip(1-r2);
                // Rounded emissive kernel instead of a flat colored disc.
                float core=exp(-r2*3.5);return float4(i.color*core,1);
            }
            ENDHLSL
        }
    }
}
