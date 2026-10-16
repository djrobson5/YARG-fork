#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
using System.Runtime.InteropServices;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;

namespace YARG.Input.Devices
{
    /// <summary>
    /// Report 7 of the CRKD guitar's Bluetooth HID descriptor: the gamepad collection.
    /// Byte 0 is the report ID; the remaining offsets come from the descriptor.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 11)]
    internal struct CrkdBluetoothGuitarState : IInputStateTypeInfo
    {
        public const byte GAMEPAD_REPORT_ID = 7;

        public FourCC format => new('H', 'I', 'D');

        [FieldOffset(0)]
        public byte reportId;

        [InputControl(name = "leftStick", layout = "Stick", format = "VC2B")]
        [InputControl(name = "leftStick/x", offset = 0, format = "BYTE", parameters = "normalize,normalizeMin=0,normalizeMax=1,normalizeZero=0.5")]
        [InputControl(name = "leftStick/left", offset = 0, format = "BYTE", parameters = "normalize,normalizeMin=0,normalizeMax=1,normalizeZero=0.5,clamp=1,clampMin=0,clampMax=0.5,invert")]
        [InputControl(name = "leftStick/right", offset = 0, format = "BYTE", parameters = "normalize,normalizeMin=0,normalizeMax=1,normalizeZero=0.5,clamp=1,clampMin=0.5,clampMax=1")]
        [InputControl(name = "leftStick/y", offset = 1, format = "BYTE", parameters = "invert,normalize,normalizeMin=0,normalizeMax=1,normalizeZero=0.5")]
        [InputControl(name = "leftStick/up", offset = 1, format = "BYTE", parameters = "normalize,normalizeMin=0,normalizeMax=1,normalizeZero=0.5,clamp=1,clampMin=0,clampMax=0.5,invert")]
        [InputControl(name = "leftStick/down", offset = 1, format = "BYTE", parameters = "normalize,normalizeMin=0,normalizeMax=1,normalizeZero=0.5,clamp=1,clampMin=0.5,clampMax=1,invert=false")]
        [FieldOffset(1)]
        public byte x;

        [FieldOffset(2)]
        public byte y;

        [InputControl(name = "rightStick", layout = "Stick", format = "VC2B")]
        [InputControl(name = "rightStick/x", offset = 0, format = "BYTE", parameters = "normalize,normalizeMin=0,normalizeMax=1,normalizeZero=0.5")]
        [InputControl(name = "rightStick/left", offset = 0, format = "BYTE", parameters = "normalize,normalizeMin=0,normalizeMax=1,normalizeZero=0.5,clamp=1,clampMin=0,clampMax=0.5,invert")]
        [InputControl(name = "rightStick/right", offset = 0, format = "BYTE", parameters = "normalize,normalizeMin=0,normalizeMax=1,normalizeZero=0.5,clamp=1,clampMin=0.5,clampMax=1")]
        [InputControl(name = "rightStick/y", offset = 1, format = "BYTE", parameters = "invert,normalize,normalizeMin=0,normalizeMax=1,normalizeZero=0.5")]
        [InputControl(name = "rightStick/up", offset = 1, format = "BYTE", parameters = "normalize,normalizeMin=0,normalizeMax=1,normalizeZero=0.5,clamp=1,clampMin=0,clampMax=0.5,invert")]
        [InputControl(name = "rightStick/down", offset = 1, format = "BYTE", parameters = "normalize,normalizeMin=0,normalizeMax=1,normalizeZero=0.5,clamp=1,clampMin=0.5,clampMax=1,invert=false")]
        [FieldOffset(3)]
        public byte z;

        [FieldOffset(4)]
        public byte rz;

        // Hat switch in the low nibble; 8 and above is centered
        [InputControl(name = "dpad", format = "BIT", layout = "Dpad", sizeInBits = 4, defaultState = 8)]
        [InputControl(name = "dpad/up", format = "BIT", layout = "DiscreteButton", parameters = "minValue=7,maxValue=1,nullValue=8,wrapAtValue=7", bit = 0, sizeInBits = 4)]
        [InputControl(name = "dpad/right", format = "BIT", layout = "DiscreteButton", parameters = "minValue=1,maxValue=3", bit = 0, sizeInBits = 4)]
        [InputControl(name = "dpad/down", format = "BIT", layout = "DiscreteButton", parameters = "minValue=3,maxValue=5", bit = 0, sizeInBits = 4)]
        [InputControl(name = "dpad/left", format = "BIT", layout = "DiscreteButton", parameters = "minValue=5,maxValue=7", bit = 0, sizeInBits = 4)]
        [FieldOffset(5)]
        public byte hat;

        // HID buttons 1-16, button 1 in bit 0
        [InputControl(name = "buttonSouth", layout = "Button", bit = 0, displayName = "A")]
        [InputControl(name = "buttonEast", layout = "Button", bit = 1, displayName = "B")]
        [InputControl(name = "buttonWest", layout = "Button", bit = 3, displayName = "X")]
        [InputControl(name = "buttonNorth", layout = "Button", bit = 4, displayName = "Y")]
        [InputControl(name = "leftShoulder", layout = "Button", bit = 6)]
        [InputControl(name = "rightShoulder", layout = "Button", bit = 7)]
        [InputControl(name = "select", layout = "Button", bit = 10)]
        [InputControl(name = "start", layout = "Button", bit = 11)]
        [InputControl(name = "leftStickPress", layout = "Button", bit = 13)]
        [InputControl(name = "rightStickPress", layout = "Button", bit = 14)]
        [FieldOffset(6)]
        public ushort buttons;

        [InputControl(name = "rightTrigger", format = "BYTE")]
        [FieldOffset(8)]
        public byte accelerator;

        [InputControl(name = "leftTrigger", format = "BYTE")]
        [FieldOffset(9)]
        public byte brake;
    }

    /// <summary>
    /// A CRKD guitar connected over Bluetooth on macOS.<br/>
    /// macOS exposes its keyboard, consumer, mouse and gamepad collections as one HID device whose
    /// primary usage is Keyboard, so the InputSystem leaves it unsupported. This layout reads only the
    /// gamepad report and presents it as a <see cref="Gamepad"/> for the CRKD Mode 1 binding presets.
    /// </summary>
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
#endif
    [InputControlLayout(stateType = typeof(CrkdBluetoothGuitarState), displayName = "CRKD Guitar (Bluetooth)")]
    internal class CrkdBluetoothGuitar : Gamepad, IInputStateCallbackReceiver
    {
        private const int VENDOR_ID = 0x1949;
        private const int PRODUCT_ID = 0x0402;

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#else
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
#endif
        internal static void Initialize()
        {
            InputSystem.RegisterLayout<CrkdBluetoothGuitar>(
                matches: new InputDeviceMatcher()
                    .WithInterface("HID")
                    .WithCapability("vendorId", VENDOR_ID)
                    .WithCapability("productId", PRODUCT_ID)
            );
        }

        void IInputStateCallbackReceiver.OnNextUpdate()
        {
        }

        unsafe void IInputStateCallbackReceiver.OnStateEvent(InputEventPtr eventPtr)
        {
            // The keyboard, consumer and mouse reports share this device; drop everything but the gamepad report
            if (eventPtr.type != StateEvent.Type)
                return;

            var stateEvent = StateEvent.From(eventPtr);
            if (stateEvent->stateFormat != stateBlock.format ||
                stateEvent->stateSizeInBytes < sizeof(CrkdBluetoothGuitarState) ||
                *(byte*) stateEvent->state != CrkdBluetoothGuitarState.GAMEPAD_REPORT_ID)
                return;

            InputState.Change(this, eventPtr);
        }

        bool IInputStateCallbackReceiver.GetStateOffsetForEvent(InputControl control, InputEventPtr eventPtr,
            ref uint offset)
        {
            return false;
        }
    }
}
#endif
