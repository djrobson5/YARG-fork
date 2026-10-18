using System;

namespace YARG.Settings.Metadata
{
    public sealed class HeaderMetadata : AbstractMetadata
    {
        public override string[] UnlocalizedSearchNames => null;

        public string HeaderName { get; }
        public bool ShowPreview { get; }

        public HeaderMetadata(string headerName, bool showPreview = false, Func<bool> visibleWhen = null)
            : base(visibleWhen)
        {
            HeaderName = headerName;
            ShowPreview = showPreview;
        }
    }
}
