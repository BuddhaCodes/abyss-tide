// Abyss Tide — shader de respaldo para Windows Terminal.
// Agua ASCII animada detrás del texto. Es solo una animación: los shaders de la terminal
// no conocen la posición de la ventana, así que NO reacciona al moverla
// (para eso está el motor AbyssTide.cs / abyss-tide.ps1).

Texture2D shaderTexture;
SamplerState samplerState;

cbuffer PixelShaderSettings
{
    float  Time;
    float  Scale;
    float2 Resolution;
    float4 Background;
};

#define FILL      0.34      // fracción de la altura con agua
#define WATER_A   0.55      // opacidad de los caracteres del agua
#define SURFACE   float3(0.61, 0.94, 1.00)
#define DEEP      float3(0.10, 0.36, 0.61)
#define FOAM      float3(1.00, 1.00, 1.00)

// glifos 5×5 (bit = x + 5*y, y = 0 arriba)
#define G_UNDER 32505856u
#define G_DOT    4194304u
#define G_DASH     14336u
#define G_TILDE   283712u
#define G_CARET      324u
#define G_STAR   4897444u
#define G_EQ     1016800u
#define G_COLON   131200u
#define G_O     15254976u

static const float PI = 3.14159265;

float hash1(float n) { return frac(sin(n * 12.9898) * 43758.5453); }

// altura de la superficie (en filas, desde abajo) en la columna col
float surfaceAt(float col, float cols, float rows, float t)
{
    float x = col / max(cols, 1.0);
    float slosh = cos(PI * x) * cos(t * 0.9) * (0.9 + 0.5 * sin(t * 0.23));
    float waves = 0.55 * sin(x * 13.0 - t * 1.7)
                + 0.30 * sin(x * 29.0 + t * 2.3)
                + 0.15 * sin(x * 63.0 - t * 3.4);
    return rows * FILL + 1.4 * slosh + waves;
}

float glyphMask(uint code, float2 q)   // q = posición dentro de la celda en [0,1)
{
    float2 gp = (q * float2(6.0, 8.0) - float2(0.5, 1.5)) / 5.0;
    if (gp.x < 0.0 || gp.y < 0.0 || gp.x >= 1.0 || gp.y >= 1.0) return 0.0;
    uint2 g = uint2(floor(gp * 5.0));
    uint bit = g.x + g.y * 5u;
    return (float)((code >> bit) & 1u);
}

float4 main(float4 pos : SV_POSITION, float2 tex : TEXCOORD) : SV_TARGET
{
    float4 color = shaderTexture.Sample(samplerState, tex);

    float2 cellPx = float2(10.0, 16.0) * max(Scale, 0.5);
    float cols = floor(Resolution.x / cellPx.x);
    float rows = floor(Resolution.y / cellPx.y);

    float2 px = tex * Resolution;
    float col = floor(px.x / cellPx.x);
    float yb = Resolution.y - px.y;                 // píxeles desde abajo
    float row = floor(yb / cellPx.y);               // fila desde abajo
    float2 q = float2(frac(px.x / cellPx.x), 1.0 - frac(yb / cellPx.y));

    float t = Time;
    float s = surfaceAt(col + 0.5, cols, rows, t);
    float top = floor(s);
    float fracS = s - top;
    float slope = surfaceAt(col + 1.5, cols, rows, t) - surfaceAt(col - 0.5, cols, rows, t);

    uint code = 0u;
    float3 wcol = SURFACE;
    float wa = WATER_A;
    float tint = 0.0;

    if (row < top)
    {
        float d = s - (row + 0.5);
        float wave = sin(col * 0.31 + t * 1.15 + row * 0.85 + 2.2 * sin(col * 0.07 - t * 0.45));
        float tex2 = 0.5 + 0.35 * wave + 0.15 * sin(col * 0.13 - t * 0.8 - row * 0.37);
        float nearS = exp(-d * 0.22);
        float v = (0.32 + 0.68 * nearS) * (0.3 + 0.7 * tex2);
        if (v < 0.18)      code = 0u;
        else if (v < 0.36) code = G_DOT;
        else if (v < 0.52) code = G_COLON;
        else if (v < 0.68) code = G_DASH;
        else if (v < 0.84) code = G_TILDE;
        else               code = G_EQ;
        wcol = lerp(DEEP, SURFACE, 0.15 + nearS * 0.75 + 0.1 * tex2);
        wa = WATER_A * (0.5 + 0.5 * saturate(v));
        tint = 0.22 + 0.14 * nearS;

        // burbujas que suben por algunas columnas
        float h = hash1(col);
        if (h > 0.92)
        {
            float period = 4.0 + h * 6.0;
            float by = frac(t / period + h * 7.0) * s;
            if (abs(row - floor(by)) < 0.5) { code = G_O; wcol = lerp(SURFACE, FOAM, 0.3); wa = WATER_A; }
        }
    }
    else if (row == top && fracS > 0.06)
    {
        if (abs(slope) > 1.6)  code = G_TILDE;
        else if (fracS < 0.2)  code = G_UNDER;
        else if (fracS < 0.42) code = G_DOT;
        else if (fracS < 0.62) code = G_DASH;
        else if (fracS < 0.9)  code = G_TILDE;
        else                   code = G_CARET;
        wcol = lerp(SURFACE, FOAM, 0.3);
        wa = saturate(WATER_A + 0.15);
        tint = 0.22 * fracS;
    }

    // solo pintamos sobre el fondo, el texto queda intacto
    float isBg = 1.0 - smoothstep(0.04, 0.10, distance(color.rgb, Background.rgb * color.a));
    float m = glyphMask(code, q) * wa;
    float3 deepTint = DEEP * 0.45;
    float3 o = color.rgb;
    o = lerp(o, deepTint * color.a, tint * isBg);
    o = lerp(o, wcol * color.a, m * isBg);
    return float4(o, color.a);
}
