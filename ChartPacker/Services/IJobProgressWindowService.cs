using ChartPacker.ViewModels;

namespace ChartPacker.Services
{
    public interface IJobProgressWindowService
    {
        /// <summary>Shows the job progress window for <paramref name="job"/>, or brings it to the front if it's already open.</summary>
        void Show(JobViewModel job);
    }
}
