using UnityEngine;

namespace LiquidFX
{
    public enum LiquidQuality
    {
        Low = 0,
        Medium = 1,
        High = 2
    }

    /// <summary>
    /// Global tuning for the liquid effects. Everything that costs frame time on a phone is
    /// budgeted from here so the whole system scales with a single switch.
    /// </summary>
    public static class LiquidFXRuntime
    {
        public const float Gravity = 9.81f;

        /// <summary>1 cubic metre holds 1.000.000 millilitres.</summary>
        public const float MillilitresPerCubicMetre = 1000000f;

        static LiquidQuality quality = LiquidQuality.Medium;
        static bool qualityResolved;

        public static LiquidQuality Quality
        {
            get
            {
                if (!qualityResolved)
                {
                    qualityResolved = true;
                    quality = Application.isMobilePlatform ? LiquidQuality.Low : LiquidQuality.Medium;
                }
                return quality;
            }
            set
            {
                qualityResolved = true;
                quality = value;
            }
        }

        /// <summary>Segments used by every procedural stream ribbon.</summary>
        public static int StreamSegments
        {
            get
            {
                switch (Quality)
                {
                    case LiquidQuality.Low: return 10;
                    case LiquidQuality.High: return 24;
                    default: return 16;
                }
            }
        }

        /// <summary>Concurrent streams allowed. Older streams are cut when the budget is exceeded.</summary>
        public static int MaxConcurrentStreams => Quality == LiquidQuality.Low ? 2 : 4;

        /// <summary>Upper bound for particles alive across every impact effect.</summary>
        public static int ParticleBudget
        {
            get
            {
                switch (Quality)
                {
                    case LiquidQuality.Low: return 24;
                    case LiquidQuality.High: return 96;
                    default: return 48;
                }
            }
        }

        /// <summary>Grab-pass style refraction costs a framebuffer copy. Off on the low tier.</summary>
        public static bool AllowRefraction => Quality != LiquidQuality.Low;

        /// <summary>Seconds an idle stream stays warm before its mesh goes back to the pool.</summary>
        public const float DormantTimeout = 8f;

        // ---------------------------------------------------------------- ballistics

        /// <summary>
        /// Time for a particle leaving with <paramref name="verticalSpeed"/> (positive up) to
        /// fall <paramref name="dropHeight"/> metres. Always returns a positive value.
        ///
        /// Solving y(t) = y0 + v*t - g*t^2/2 for y = y0 - h gives g*t^2/2 - v*t - h = 0, hence
        /// t = (v + sqrt(v^2 + 2gh)) / g. Note the sign: a jet leaving downward (negative v)
        /// arrives sooner than one dropped from rest, not later.
        /// </summary>
        public static float FallTime(float dropHeight, float verticalSpeed)
        {
            if (dropHeight <= 0f)
                return 0f;

            float discriminant = verticalSpeed * verticalSpeed + 2f * Gravity * dropHeight;
            if (discriminant <= 0f)
                return 0f;

            return Mathf.Max(0f, (verticalSpeed + Mathf.Sqrt(discriminant)) / Gravity);
        }

        /// <summary>Ballistic position at <paramref name="time"/> for a projectile with no drag.</summary>
        public static Vector3 BallisticPoint(Vector3 origin, Vector3 velocity, float time)
        {
            return origin + velocity * time + new Vector3(0f, -0.5f * Gravity * time * time, 0f);
        }

        /// <summary>Tangent (unnormalised velocity) of the ballistic curve at <paramref name="time"/>.</summary>
        public static Vector3 BallisticVelocity(Vector3 velocity, float time)
        {
            return velocity + new Vector3(0f, -Gravity * time, 0f);
        }

        /// <summary>
        /// Radius of a free falling jet that carries <paramref name="flowM3PerSecond"/> at
        /// <paramref name="speed"/>. Mass continuity, so the jet thins out as it accelerates.
        /// </summary>
        public static float JetRadius(float flowM3PerSecond, float speed)
        {
            if (flowM3PerSecond <= 0f || speed <= 0.0001f)
                return 0f;

            return Mathf.Sqrt(flowM3PerSecond / (Mathf.PI * speed));
        }

        public static float MillilitresToCubicMetres(float millilitres) => millilitres / MillilitresPerCubicMetre;

        public static float CubicMetresToMillilitres(float cubicMetres) => cubicMetres * MillilitresPerCubicMetre;
    }
}
