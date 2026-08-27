using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace LiquidFX
{
    /// <summary>
    /// Test harness for the prototype scenes. Not part of the effects themselves: it only pushes
    /// values into them so the behaviour can be checked by hand on a device.
    ///
    /// Desktop:  A / D or arrows open and close the valve, S toggles the drain,
    ///           Q cycles the quality tier, R refills, Space tips the flask.
    /// Touch:    drag up and down anywhere to open and close the valve or tip the flask.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LiquidFXDemoRig : MonoBehaviour
    {
        [Header("Targets")]
        [SerializeField] LiquidPourController pour;

        [SerializeField] LiquidSurface surface;

        [Tooltip("Optional: a flask that this rig tips over instead of opening a valve.")]
        [SerializeField] Transform tiltTarget;

        [SerializeField] FlaskVolume tiltFlask;

        [Header("Valve")]
        [SerializeField, Min(0.05f)] float valveSpeed = 0.8f;

        [Header("Tilt")]
        [SerializeField] Vector3 tiltAxis = Vector3.forward;

        [SerializeField, Range(0f, 180f)] float maximumTilt = 115f;

        [SerializeField, Min(1f)] float tiltSpeed = 55f;

        [Header("Refill")]
        [SerializeField, Min(0f)] float refillML = 250f;

        Quaternion restRotation;
        float tilt;
        float dragValue;

        void Awake()
        {
            if (tiltTarget != null)
                restRotation = tiltTarget.localRotation;
        }

        void Update()
        {
            float deltaTime = Time.deltaTime;
            float axis = ReadVerticalAxis();

            if (pour != null && tiltTarget == null)
                pour.ValveOpen = Mathf.Clamp01(pour.ValveOpen + axis * valveSpeed * deltaTime);

            if (tiltTarget != null)
            {
                tilt = Mathf.Clamp(tilt + axis * tiltSpeed * deltaTime, 0f, maximumTilt);
                tiltTarget.localRotation = restRotation * Quaternion.AngleAxis(tilt, tiltAxis.normalized);
            }

            if (WasPressedThisFrame(DemoKey.Drain) && surface != null)
                surface.DrainOpen = !surface.DrainOpen;

            if (WasPressedThisFrame(DemoKey.Quality))
                LiquidFXRuntime.Quality = (LiquidQuality)(((int)LiquidFXRuntime.Quality + 1) % 3);

            if (WasPressedThisFrame(DemoKey.Refill))
                Refill();
        }

        void Refill()
        {
            if (tiltFlask != null)
                tiltFlask.SetContentsML(Mathf.Min(refillML, tiltFlask.CapacityML));

            if (surface != null)
                surface.SetContentsML(surface.CapacityML * 0.18f);
        }

        enum DemoKey
        {
            Drain,
            Quality,
            Refill
        }

        float ReadVerticalAxis()
        {
#if ENABLE_INPUT_SYSTEM
            float axis = 0f;

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed || keyboard.spaceKey.isPressed)
                    axis += 1f;
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
                    axis -= 1f;
            }

            // Dragging up opens, dragging down closes. Works with a mouse and with a finger.
            Pointer pointer = Touchscreen.current != null ? (Pointer)Touchscreen.current : Mouse.current;
            if (pointer != null && pointer.press.isPressed)
            {
                float delta = pointer.delta.ReadValue().y;
                dragValue = Mathf.Clamp(delta * 0.02f, -1f, 1f);
                axis += dragValue;
            }

            return Mathf.Clamp(axis, -1f, 1f);
#else
            return 0f;
#endif
        }

        static bool WasPressedThisFrame(DemoKey key)
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return false;

            switch (key)
            {
                case DemoKey.Drain: return keyboard.sKey.wasPressedThisFrame;
                case DemoKey.Quality: return keyboard.qKey.wasPressedThisFrame;
                case DemoKey.Refill: return keyboard.rKey.wasPressedThisFrame;
            }
#endif
            return false;
        }
    }
}
