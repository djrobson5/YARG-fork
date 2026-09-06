using System;
using UnityEngine;

namespace YARG.Settings.Types
{
    public class SliderSetting : AbstractSetting<float>
    {
        public override string AddressableName => "Setting/Slider";

        public float Min { get; private set; }
        public float Max { get; private set; }

        /// <summary>
        /// Increment the value snaps to, or <c>0</c> for a continuous slider.
        /// </summary>
        /// <remarks>
        /// Snapping happens in the setting rather than in <c>ValueSlider</c> so it applies to
        /// every write — the slider handle, the numeric input field, the navigation-scheme
        /// increase/decrease entries, and a value loaded from the settings file.
        /// </remarks>
        public float Step { get; private set; }

        public SliderSetting(float value, float min = float.NegativeInfinity, float max = float.PositiveInfinity,
            Action<float> onChange = null, float step = 0f) : base(onChange)
        {
            Min = min;
            Max = max;
            Step = step;

            _value = value;
        }

        protected override void SetValue(float value)
        {
            if (Step > 0f)
            {
                // Snap relative to Min so the reachable values always include Min itself.
                value = Min + Mathf.Round((value - Min) / Step) * Step;
            }

            _value = Mathf.Clamp(value, Min, Max);
        }

        public override bool ValueEquals(float value)
            => Mathf.Approximately(value, Value);
    }
}