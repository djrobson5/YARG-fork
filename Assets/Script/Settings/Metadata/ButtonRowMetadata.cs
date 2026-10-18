using System;

namespace YARG.Settings.Metadata
{
    public sealed class ButtonRowMetadata : AbstractMetadata
    {
        public override string[] UnlocalizedSearchNames { get; }

        public string[] Buttons { get; private set; }

        /// <summary>
        /// Greys the row out while this returns false, like <c>AbstractSetting.EditableWhen</c>.
        /// </summary>
        public Func<bool> EditableWhen { get; set; }

        public bool IsEditable => EditableWhen?.Invoke() ?? true;

        public ButtonRowMetadata(string button)
            : this(button, visibleWhen: null)
        {
        }

        public ButtonRowMetadata(string button, Func<bool> visibleWhen)
            : base(visibleWhen)
        {
            UnlocalizedSearchNames = new[] { $"Button.{button}" };
            Buttons = new[] { button };
        }

        public ButtonRowMetadata(params string[] buttons)
        {
            UnlocalizedSearchNames = new string[buttons.Length];
            for (int i = 0; i < buttons.Length; i++)
            {
                UnlocalizedSearchNames[i] = $"Button.{buttons[i]}";
            }

            Buttons = buttons;
        }
    }
}
