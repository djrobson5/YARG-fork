using System;

namespace YARG.Settings.Metadata
{
    /// <summary>
    /// A short dim line of text that is recomputed whenever a setting changes, such as the Score
    /// Sync status line. Unlike <see cref="TextMetadata"/>, the text is not a localization key.
    /// </summary>
    public sealed class StatusTextMetadata : AbstractMetadata
    {
        public override string[] UnlocalizedSearchNames => null;

        public Func<string> Text { get; }

        /// <summary>Greys the line out while this returns false.</summary>
        public Func<bool> EditableWhen { get; set; }

        public bool IsEditable => EditableWhen?.Invoke() ?? true;

        public StatusTextMetadata(Func<string> text, Func<bool> visibleWhen = null)
            : base(false, visibleWhen)
        {
            Text = text;
        }
    }
}
