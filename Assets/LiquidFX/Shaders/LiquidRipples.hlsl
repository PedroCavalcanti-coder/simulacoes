#ifndef LIQUIDFX_RIPPLES_INCLUDED
#define LIQUIDFX_RIPPLES_INCLUDED

// Analytical circular ripples, evaluated in metres relative to the centre of the surface.
//
// Everything here is closed form: no ripple RenderTexture, no ping-pong blit, no CPU wave
// simulation. Six concurrent impacts plus one continuous source is enough for a sink, and it
// costs a handful of ALU per pixel with zero bandwidth, which is the trade a phone wants.
//
// Because the inputs are metres and seconds rather than UV, the same numbers behave identically
// on a 2.35 m sink and on a 6 cm beaker.

#define LIQUIDFX_MAX_RIPPLES 6
#define LIQUIDFX_TWO_PI 6.28318530718

// Set from LiquidSurface.cs through a MaterialPropertyBlock.
int    _RippleCount;
float4 _RippleData[LIQUIDFX_MAX_RIPPLES];    // xy = centre in metres, z = start time, w = strength
float4 _RippleParams[LIQUIDFX_MAX_RIPPLES];  // x = initial radius in metres
float4 _ContinuousRipple;                    // xy = centre in metres, z = intensity, w = radius
float4 _SurfaceSize;                         // xy = basin width and depth in metres
float  _RippleSpeed;                         // metres per second
float  _RippleWavelength;                    // metres between crests
float  _RippleSpatialDecay;                  // per metre
float  _RippleTimeDecay;                     // per second
float  _RippleLifetime;                      // seconds
float  _RippleAmplitude;                     // metres, for a unit strength impact

struct LiquidRippleSample
{
    float  height;  // metres of vertical displacement
    float2 slope;   // d(height) / d(metres), used to build the normal
    float  energy;  // 0..1 activity, drives foam and highlights
};

LiquidRippleSample EvaluateLiquidRipples(float2 metres)
{
    LiquidRippleSample result;
    result.height = 0.0;
    result.slope = float2(0.0, 0.0);
    result.energy = 0.0;

    float waveNumber = LIQUIDFX_TWO_PI / max(_RippleWavelength, 0.001);
    float frontSoftness = max(_RippleWavelength * 0.6, 0.002);

    // ---------------------------------------------------------------- continuous source
    // A stream landing on the surface digs a standing depression and throws off rings that
    // travel outward forever while the stream runs.
    float continuousIntensity = saturate(_ContinuousRipple.z);
    if (continuousIntensity > 0.001)
    {
        float2 delta = metres - _ContinuousRipple.xy;
        float distance = max(length(delta), 0.0001);
        float radius = max(_ContinuousRipple.w, 0.002);
        float outside = max(distance - radius, 0.0);

        float amplitude = continuousIntensity * _RippleAmplitude * 0.9;
        float phase = outside * waveNumber - _Time.y * _RippleSpeed * waveNumber;
        float envelope = exp(-outside * _RippleSpatialDecay * 0.5);
        float mask = smoothstep(radius * 0.3, radius, distance);
        float wave = cos(phase);

        float rings = amplitude * wave * envelope * mask;

        // The cavity itself: a gaussian dent centred on the impact.
        float sigma = max(radius * 1.3, 0.006);
        float cavity = exp(-(distance * distance) / (2.0 * sigma * sigma));
        float dent = -amplitude * 1.6 * cavity;

        result.height += rings + dent;

        float ringSlope = amplitude * envelope * mask *
            (-waveNumber * sin(phase) - _RippleSpatialDecay * 0.5 * wave);
        float dentSlope = amplitude * 1.6 * cavity * distance / (sigma * sigma);

        result.slope += (ringSlope + dentSlope) * (delta / distance);
        result.energy += (abs(rings) + abs(dent)) / max(_RippleAmplitude, 0.0001) * 0.35;
    }

    // ---------------------------------------------------------------- one-shot impacts
    [loop]
    for (int index = 0; index < LIQUIDFX_MAX_RIPPLES; index++)
    {
        if (index >= _RippleCount)
            break;

        float4 data = _RippleData[index];
        float age = _Time.y - data.z;
        if (age < 0.0 || age > _RippleLifetime)
            continue;

        float initialRadius = max(_RippleParams[index].x, 0.002);
        float2 delta = metres - data.xy;
        float distance = max(length(delta), 0.0001);

        float front = initialRadius + age * _RippleSpeed;
        float ahead = max(distance - front, 0.0);
        float trail = max(front - distance, 0.0);

        // Nothing exists ahead of the wave front; the transition is one wavelength wide.
        float frontMask = 1.0 - smoothstep(0.0, frontSoftness, ahead);
        if (frontMask <= 0.0)
            continue;

        // A circular wave spreads its energy over a growing circumference.
        float spreading = sqrt(initialRadius / max(distance, initialRadius));

        float amplitude = data.w * _RippleAmplitude * spreading;
        float phase = trail * waveNumber;
        float wave = cos(phase);
        float envelope = exp(-age * _RippleTimeDecay) * exp(-trail * _RippleSpatialDecay) * frontMask;

        float contribution = amplitude * wave * envelope;
        result.height += contribution;

        // d/d(distance) with trail = front - distance, hence the sign flip.
        float radialSlope = amplitude * envelope *
            (waveNumber * sin(phase) + _RippleSpatialDecay * wave);

        result.slope += radialSlope * (delta / distance);
        result.energy += abs(contribution) / max(_RippleAmplitude, 0.0001) * 0.5;
    }

    result.energy = saturate(result.energy);
    return result;
}

#endif // LIQUIDFX_RIPPLES_INCLUDED
