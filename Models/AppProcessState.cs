namespace app_tự_động.Models
{
    public enum AppProcessState
    {
        Ready,
        Checking,
        Downloading,
        Installing,
        Success,
        Failed,
        Skipped,
        Cancelled
    }
}