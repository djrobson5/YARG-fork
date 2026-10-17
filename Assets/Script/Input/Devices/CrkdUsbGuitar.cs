#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
using System.Runtime.InteropServices;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;

namespace YARG.Input.Devices
{
    /// <summary>
    /// The raw Xbox 360 input report that macOS's built-in Xbox driver passes through for the CRKD guitar.
    /// Bytes 0-1 are the report header; the remaining offsets come from the driver's HID descriptor.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    internal struct CrkdUsbGuitarState : IInputStateTypeInfo
    {
        public FourCC format => new('H', 'I', 'D');

        [FieldOffset(0)]
        public ushort header;

        // Byte 2 holds the d-pad, Start, Back and stick presses; byte 3 the shoulders, Guide (bit 10) and face buttons
        [InputControl(name = "dpad", layout = "Dpad", sizeInBits = 4, bit = 0)]
        [InputControl(name = "dpad/up", bit = 0)]
        [InputControl(name = "dpad/down", bit = 1)]
        [InputControl(name = "dpad/left", bit = 2)]
        [InputControl(name = "dpad/right", bit = 3)]
        [InputControl(name = "start", layout = "Button", bit = 4)]
        [InputControl(name = "select", layout = "Button", bit = 5)]
        [InputControl(name = "leftStickPress", layout = "Button", bit = 6)]
        [InputControl(name = "rightStickPress", layout = "Button", bit = 7)]
        [InputControl(name = "leftShoulder", layout = "Button", bit = 8)]
        [InputControl(name = "rightShoulder", layout = "Button", bit = 9)]
        [InputControl(name = "buttonSouth", layout = "Button", bit = 12, displayName = "A")]
        [InputControl(name = "buttonEast", layout = "Button", bit = 13, displayName = "B")]
        [InputControl(name = "buttonWest", layout = "Button", bit = 14, displayName = "X")]
        [InputControl(name = "buttonNorth", layout = "Button", bit = 15, displayName = "Y")]
        [FieldOffset(2)]
        public ushort buttons;

        [InputControl(name = "leftTrigger", format = "BYTE")]
        [FieldOffset(4)]
        public byte leftTrigger;

        [InputControl(name = "rightTrigger", format = "BYTE")]
        [FieldOffset(5)]
        public byte rightTrigger;

        // Positive Y is up, as XInput reports it
        [InputControl(name = "leftStick", layout = "Stick", format = "VC2S")]
        [InputControl(name = "leftStick/x", offset = 0, format = "SHRT", parameters = "")]
        [InputControl(name = "leftStick/left", offset = 0, format = "SHRT", parameters = "")]
        [InputControl(name = "leftStick/right", offset = 0, format = "SHRT", parameters = "")]
        [InputControl(name = "leftStick/y", offset = 2, format = "SHRT", parameters = "")]
        [InputControl(name = "leftStick/up", offset = 2, format = "SHRT", parameters = "clamp=1,clampMin=0,clampMax=1,invert=false")]
        [InputControl(name = "leftStick/down", offset = 2, format = "SHRT", parameters = "clamp=1,clampMin=-1,clampMax=0,invert=true")]
        [FieldOffset(6)]
        public short leftStickX;

        [FieldOffset(8)]
        public short leftStickY;

        [InputControl(name = "rightStick", layout = "Stick", format = "VC2S")]
        [InputControl(name = "rightStick/x", offset = 0, format = "SHRT", parameters = "")]
        [InputControl(name = "rightStick/left", offset = 0, format = "SHRT", parameters = "")]
        [InputControl(name = "rightStick/right", offset = 0, format = "SHRT", parameters = "")]
        [InputControl(name = "rightStick/y", offset = 2, format = "SHRT", parameters = "")]
        [InputControl(name = "rightStick/up", offset = 2, format = "SHRT", parameters = "clamp=1,clampMin=0,clampMax=1,invert=false")]
        [InputControl(name = "rightStick/down", offset = 2, format = "SHRT", parameters = "clamp=1,clampMin=-1,clampMax=0,invert=true")]
        [FieldOffset(10)]
        public short rightStickX;

        [FieldOffset(12)]
        public short rightStickY;
    }

    /// <summary>
    /// A CRKD guitar connected by USB cable or through its wireless USB receiver on macOS.<br/>
    /// Both identify as a wired Xbox 360 controller (045E:028E), so macOS's built-in Xbox driver claims them
    /// and exposes the raw report as a generic HID gamepad, which no stock layout matches by name. With the
    /// Mode Dial on 1 the buttons follow CRKD Mode 1: A/B/Y/X/LB frets, d-pad strum, Back for Star Power
    /// and left trigger for whammy.
    /// </summary>
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
#endif
    [InputControlLayout(stateType = typeof(CrkdUsbGuitarState), displayName = "CRKD Guitar (USB)")]
    internal class CrkdUsbGuitar : Gamepad
    {
        private const int VENDOR_ID = 0x045E;
        private const int PRODUCT_ID = 0x028E;

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#else
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
#endif
        internal static void Initialize()
        {
            // The product names keep real Xbox 360 pads, which share the IDs, on their own layouts
            InputSystem.RegisterLayout<CrkdUsbGuitar>(
                matches: new InputDeviceMatcher()
                    .WithInterface("HID")
                    .WithCapability("vendorId", VENDOR_ID)
                    .WithCapability("productId", PRODUCT_ID)
                    .WithProduct("CRKD Guitar")
            );

            // The wireless receiver reports itself as a Microsoft "Guitar Controller"
            InputSystem.RegisterLayoutMatcher<CrkdUsbGuitar>(
                new InputDeviceMatcher()
                    .WithInterface("HID")
                    .WithCapability("vendorId", VENDOR_ID)
                    .WithCapability("productId", PRODUCT_ID)
                    .WithProduct("Guitar Controller")
            );
        }
    }
}
#endif
