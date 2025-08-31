public class UpdateManifest
{
    public SdInfo Sd { get; set; } = new();

    public class SdInfo
    {
        public string Url { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
    }
}
