namespace app_tự_động.Models
{
    public class AppItem
    {
        public string Name { get; set; }
        public string DownloadUrl { get; set; }
        public string FileName { get; set; }
        public string SilentArgs { get; set; }
        public string InteractiveArgs { get; set; }
        public string DetectKeyword { get; set; }
        public string ExePathHint1 { get; set; }
        public string ExePathHint2 { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }
}