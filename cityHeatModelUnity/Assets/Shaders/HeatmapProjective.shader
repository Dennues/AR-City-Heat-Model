Shader "Custom/HeatmapProjective"
{
    Properties
    {
        _BaseMap ("Base Texture", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _HeatmapTex ("Heatmap Texture", 2D) = "white" {}
        _HeatmapOriginX ("Heatmap Origin X", Float) = 0
        _HeatmapOriginZ ("Heatmap Origin Z", Float) = 0
        _HeatmapSizeX ("Heatmap Size X", Float) = 100
        _HeatmapSizeZ ("Heatmap Size Z", Float) = 100
        _HeatmapOffsetX ("Heatmap Offset X", Float) = 0
        _HeatmapOffsetY ("Heatmap Offset Y", Float) = 0
        _HeatmapScaleX ("Heatmap Scale X", Float) = 1
        _HeatmapScaleY ("Heatmap Scale Y", Float) = 1
        _HeatmapRotation ("Heatmap Rotation (degrees)", Float) = 0
        _PlaneRotationY ("Plane Y Rotation (degrees)", Float) = 0
        _HeatmapOriginWS ("Heatmap Origin WS", Vector) = (0, 0, 0, 1)
        _PlaneAxisU ("Plane Axis U (WS)", Vector) = (1, 0, 0, 0)
        _PlaneAxisV ("Plane Axis V (WS)", Vector) = (0, 0, 1, 0)
        _FlipZ ("Flip Z (Left-Right)", Float) = 0
        _HeatmapOpacity ("Heatmap Opacity", Range(0, 1)) = 1
        _FlipY ("Flip Y", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _BaseMap;
            sampler2D _HeatmapTex;
            float4 _BaseColor;
            float _HeatmapOriginX;
            float _HeatmapOriginZ;
            float _HeatmapSizeX;
            float _HeatmapSizeZ;
            float _HeatmapOffsetX;
            float _HeatmapOffsetY;
            float _HeatmapScaleX;
            float _HeatmapScaleY;
            float _HeatmapRotation;
            float _PlaneRotationY;
            float4 _HeatmapOriginWS;
            float4 _PlaneAxisU;
            float4 _PlaneAxisV;
            float _FlipZ;
            float _HeatmapOpacity;
            float _FlipY;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // base colour from mesh texture or solid colour
                fixed4 baseColor = tex2D(_BaseMap, i.uv) * _BaseColor;

                // project world position to plane-local UV using full plane orientation
                float3 originWS = _HeatmapOriginWS.xyz;
                float3 axisU = normalize(_PlaneAxisU.xyz);
                float3 axisV = normalize(_PlaneAxisV.xyz);

                float3 toPoint = i.worldPos - originWS;
                float2 local = float2(dot(toPoint, axisU), dot(toPoint, axisV));

                // optional U-axis flip to fix left-right orientation
                if (_FlipZ > 0.5)
                    local.x = -local.x;
                
                // normalise to [-0.5, 0.5] range based on heatmap size (treated as U/V size)
                float2 normalized = local / float2(_HeatmapSizeX, _HeatmapSizeZ);

                // shift to [0,1] range
                float2 projUV = normalized + 0.5;
                
                // 5. apply Y flip optional
                if (_FlipY > 0.5)
                    projUV.y = 1.0 - projUV.y;
                
                // 6. apply scale around center (0.5, 0.5)
                projUV = (projUV - 0.5) * float2(_HeatmapScaleX, _HeatmapScaleY) + 0.5;
                
                // 7.apply texture rotation around center
                float texRotRad = _HeatmapRotation * 3.14159265 / 180.0;
                float2 centered = projUV - 0.5;
                float cosR = cos(texRotRad);
                float sinR = sin(texRotRad);
                float2 texRotated = float2(
                    cosR * centered.x - sinR * centered.y,
                    sinR * centered.x + cosR * centered.y
                );
                projUV = texRotated + 0.5;
                
                // 8. apply offset
                projUV += float2(_HeatmapOffsetX, _HeatmapOffsetY);

                // sample heatmap at projected UV (clamp to avoid tiling)
                fixed4 heatColor = tex2D(_HeatmapTex, saturate(projUV));

                // multiply base color with heatmap
                return fixed4(baseColor.rgb * heatColor.rgb, baseColor.a);
            }
            ENDCG
        }
    }
    
    Fallback "Diffuse"
}
